using System.Diagnostics;
using System.Threading.Channels;
using Yamux.Internal;
using Yamux.Protocol;

namespace Yamux;

public sealed class Session : IChannelSessionAdapter, IAsyncDisposable
{
    public readonly static TraceSource SessionTracer = new TraceSource("Yamux.Session");

    private SemaphoreSlim _closeLock = new SemaphoreSlim(1, 1);

    private readonly ITransport _transport;
    private readonly SessionFrameWriter _writer;
    private readonly PingManager _pingManager;
    private readonly ChannelManager _channelManager;
    private readonly FrameReader _frameReader;
    private readonly StreamIdGenerator _idGenerator;
    private readonly TaskCompletionSource _sessionFault = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal readonly YamuxMetrics? Metrics;

    private readonly bool _leaveOpen;

    private Task? _keepAlive;
    private bool _started;

    private readonly CancellationTokenSource _keepAliveToken;
    private readonly SessionOptions _sessionOptions;
    private volatile bool _disposed;
    private int _isClosing;

    /// <summary>
    /// Initializes a new instance of the <see cref="Session"/> class.
    /// </summary>
    /// <param name="transport">The underlying transport for this session.</param>
    /// <param name="isClient">Whether this is the client side of the connection. Client uses odd stream IDs, server uses even.</param>
    /// <param name="leaveOpen">Whether to leave the transport open when the session is closed. When true, the caller is responsible for disposing the transport.</param>
    /// <param name="options">Session configuration options. If null, default options are used.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="transport"/> is null.</exception>
    public Session(ITransport transport, bool isClient, bool leaveOpen = false, SessionOptions? options = null)
    {
        _sessionOptions = options ?? new SessionOptions();
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _leaveOpen = leaveOpen;
        _idGenerator = new StreamIdGenerator(!isClient);
        _pingManager = new PingManager();
        _keepAliveToken = new CancellationTokenSource();

        if (_sessionOptions.EnableStatistics)
        {
            this.Stats = new Statistics(_sessionOptions.StatisticsSampleInterval, _keepAliveToken.Token);
        }

        _channelManager = new ChannelManager(this, _sessionOptions.DefaultChannelOptions, _sessionOptions.AcceptBacklog, null, _sessionOptions.MaxChannels);
        bool useBatching = _sessionOptions.WriteSegmentBatchingEnabled ?? _transport.SupportsBatching;
        _writer = new SessionFrameWriter(_transport, Stats, _sessionOptions.ConnectionWriteTimeout, _sessionOptions.WriteQueueDepth, useBatching, _sessionOptions.MinWriteBatchSize);
        _frameReader = new FrameReader(
            new ConnectionReader(_transport),
            _channelManager,
            _pingManager,
            _writer,
            Stats,
            null,
            ex => CloseAsync((ex as SessionException) ?? new SessionException(SessionErrorCode.StreamClosed, "Underlying stream encountered an error", ex, SessionTermination.InternalError)),
            _sessionOptions);

        if (_sessionOptions.EnableMetrics)
        {
            Metrics = new YamuxMetrics(
                sessionId: isClient ? "client" : "server",
                isClient: isClient,
                getActiveChannels: () => _channelManager.ActiveChannelCount,
                getWriteQueueDepth: () => _writer.WriteQueueDepth);

            _channelManager.SetMetrics(Metrics);
            _frameReader.SetMetrics(Metrics);
            _writer.SetMetrics(Metrics);
        }
    }

    /// <summary>
    /// Gets the current round-trip time to the remote peer, measured by keep-alive pings.
    /// <c>null</c> if no ping has completed yet.
    /// </summary>
    public TimeSpan? RTT { get; private set; }

    /// <summary>
    /// Gets the bandwidth and byte statistics for this session, if enabled via <see cref="SessionOptions.EnableStatistics"/>.
    /// </summary>
    public Statistics? Stats { get; private set; }

    /// <summary>
    /// Opens a new channel with the specified options.
    /// </summary>
    /// <param name="options">The channel options to apply.</param>
    /// <param name="waitForAcknowledgement">If <c>true</c>, the returned task completes only after the remote peer acknowledges the new channel.</param>
    /// <param name="cancellationToken">A cancellation token to cancel the open operation.</param>
    /// <returns>A <see cref="ValueTask"/> representing the asynchronous operation, with the opened channel.</returns>
    /// <exception cref="SessionException">Thrown when the session is in GoAway state or channels have been exhausted.</exception>
    public ValueTask<IDuplexSessionChannel> OpenChannelAsync(SessionChannelOptions options, bool waitForAcknowledgement = false, CancellationToken cancellationToken = default)
    {
        if (!_channelManager.CanAcceptNew)
        {
            if (_channelManager.IsRemoteGoAway)
                throw new SessionException(SessionErrorCode.RemoteGoAway, "Cannot open a new channel because Go Away has been received from the remote peer");
            throw new SessionException(SessionErrorCode.LocalGoAway, "Cannot open a new channel because Go Away has been sent to the remote peer");
        }

        var id = _idGenerator.Next();
        var channel = new SessionChannel(this, id, options);

        _channelManager.AddChannel(id, channel);

        channel.SendWindowUpdate(0);

        if (waitForAcknowledgement)
        {
            TaskCompletionSource<IDuplexSessionChannel> tcs = new TaskCompletionSource<IDuplexSessionChannel>();

            if (cancellationToken != default)
            {
                var registration = cancellationToken.Register(() =>
                {
                    tcs.TrySetCanceled(cancellationToken);
                });
                tcs.Task.ContinueWith(t =>
                {
                    registration.Dispose();
                });
            }

            if (_sessionOptions.StreamOpenTimeout > TimeSpan.Zero)
            {
                var timeoutCts = new CancellationTokenSource(_sessionOptions.StreamOpenTimeout);
                timeoutCts.Token.Register(() =>
                {
                    if (!tcs.Task.IsCompleted)
                    {
                        tcs.TrySetException(new TimeoutException("Stream open timeout exceeded"));
                    }
                });
                tcs.Task.ContinueWith(_ =>
                {
                    try { timeoutCts.Dispose(); } catch { }
                }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            }

            _channelManager.TrackConnect(id, tcs);
            return new ValueTask<IDuplexSessionChannel>(tcs.Task);
        }

        return ValueTask.FromResult((IDuplexSessionChannel)channel);
    }

    /// <summary>
    /// Opens a new channel with the default session options.
    /// </summary>
    /// <param name="waitForAcknowledgement">If <c>true</c>, the returned task completes only after the remote peer acknowledges the new channel.</param>
    /// <param name="cancellationToken">A cancellation token to cancel the open operation.</param>
    /// <returns>A <see cref="ValueTask"/> representing the asynchronous operation, with the opened channel.</returns>
    public ValueTask<IDuplexSessionChannel> OpenChannelAsync(bool waitForAcknowledgement = false, CancellationToken cancellationToken = default) =>
        this.OpenChannelAsync(_sessionOptions.DefaultChannelOptions, waitForAcknowledgement, cancellationToken);

    /// <summary>
    /// Accepts an incoming channel from the remote peer.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token to cancel the accept operation.</param>
    /// <returns>A <see cref="ValueTask"/> representing the asynchronous operation, with the accepted channel.</returns>
    public ValueTask<IDuplexSessionChannel> AcceptAsync(CancellationToken cancellationToken = default) => AcceptChannelAsync(_sessionOptions.DefaultChannelOptions, cancellationToken);

    /// <summary>
    /// Accepts an incoming channel with custom channel options.
    /// </summary>
    /// <param name="channelOptions">The channel options to apply to the accepted channel.</param>
    /// <param name="cancellationToken">A cancellation token to cancel the accept operation.</param>
    /// <returns>A <see cref="ValueTask"/> representing the asynchronous operation, with the accepted channel.</returns>
    public ValueTask<IDuplexSessionChannel> AcceptAsync(SessionChannelOptions channelOptions, CancellationToken cancellationToken) => AcceptChannelAsync(channelOptions, cancellationToken);

    /// <summary>
    /// Accepts an incoming channel as a read-only channel.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token to cancel the accept operation.</param>
    /// <returns>A <see cref="ValueTask"/> representing the asynchronous operation, with the accepted read-only channel.</returns>
    public async ValueTask<IReadOnlySessionChannel> AcceptReadOnlyChannelAsync(CancellationToken cancellationToken = default)
    {
        var channel = await AcceptChannelAsync(_sessionOptions.DefaultChannelOptions, cancellationToken);
        return channel;
    }

    private async ValueTask<IDuplexSessionChannel> AcceptChannelAsync(SessionChannelOptions channelOptions, CancellationToken cancellationToken = default)
    {
        try
        {
            var channel = await _channelManager.WaitForAcceptAsync(cancellationToken);

            if (this.IsClosed)
            {
                throw new SessionException(SessionErrorCode.SessionShutdown, "Session has been closed");
            }

            channel.Accept();

            await channel.ApplyOptionsAsync(channelOptions, cancellationToken);
            return channel;
        }
        catch (ChannelClosedException)
        {
            throw new SessionException(SessionErrorCode.SessionShutdown, "Session has been closed");
        }
    }

    /// <summary>
    /// Sends a ping to the remote peer and measures the round-trip time.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token to cancel the ping operation.</param>
    /// <returns>A <see cref="ValueTask"/> representing the asynchronous operation, with the measured round-trip time.</returns>
    public async ValueTask<TimeSpan> PingAsync(CancellationToken cancellationToken)
    {
        return await _pingManager.PingAsync(_writer, cancellationToken);
    }

    /// <summary>
    /// Starts the session, beginning frame reading, writing, and optional keep-alive pings.
    /// Must be called before opening or accepting channels.
    /// </summary>
    /// <exception cref="SessionException">Thrown if the session has already been closed.</exception>
    public void Start()
    {
        _sessionOptions.DefaultChannelOptions?.Validate();

        if (IsClosed)
            throw new SessionException(SessionErrorCode.SessionShutdown, "Session has been closed.");

        if (_started)
            return;

        _writer.Start();
        _frameReader.Start();

        if (_sessionOptions.EnableKeepAlive)
        {
            _keepAlive = KeepAlive();
        }

        _started = true;
    }

    /// <summary>
    /// Gets whether the session has been closed.
    /// </summary>
    public bool IsClosed { get; private set; }

    /// <summary>
    /// Gracefully closes the session, draining all open channels before disposing the transport.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous close operation.</returns>
    public Task CloseAsync() => this.CloseAsync(null);

    /// <summary>
    /// Sends a GoAway notification and waits for all open channels to close gracefully within the specified timeout.
    /// </summary>
    /// <param name="timeout">The maximum time to wait for channels to close.</param>
    /// <returns><c>true</c> if all channels closed within the timeout; otherwise <c>false</c>.</returns>
    public async Task<bool> CloseOpenChannelsAsync(TimeSpan timeout)
    {
        return await _channelManager.CloseOpenChannelsAsync(timeout);
    }

    /// <summary>
    /// Sends a GoAway frame to the remote peer, indicating this session will no longer accept new channels.
    /// Existing channels continue to operate until closed.
    /// </summary>
    /// <param name="sessionTermination">The reason for the GoAway.</param>
    /// <param name="cancellationToken">A cancellation token to cancel the operation.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public async Task GoAwayAsync(SessionTermination sessionTermination = SessionTermination.Normal, CancellationToken cancellationToken = default)
    {
        try
        {
            await _writer.WriteAsync(Frame.CreateGoAwayFrame(sessionTermination), cancellationToken);
            _channelManager.SetLocalGoAway();
        }
        catch (Exception ex)
        {
            SessionTracer.TraceInformation("[Warn] yamux: Unable to send GoAway - {0}", ex.Message);
        }
    }

    private async Task CloseAsync(SessionException? err = null)
    {
        if (Interlocked.CompareExchange(ref _isClosing, 1, 0) != 0)
            return;

        await _closeLock.WaitAsync().ConfigureAwait(false);

        try
        {
            if (!IsClosed)
            {
                if (_keepAlive != null)
                {
                    _keepAliveToken.Cancel();
                    await _keepAlive.ConfigureAwait(false);
                    _keepAliveToken.Dispose();
                    _keepAlive = null;
                }

                _channelManager.FailAllConnects(
                    new SessionChannelException(ChannelErrorCode.SessionClosed, "The session has been closed"));

                _frameReader.PrepareForClose();

                if (!_leaveOpen)
                {
                    _transport.Close();
                }

                _frameReader.Stop();
                await _frameReader.WaitForCompletionAsync().ConfigureAwait(false);

                _channelManager.CloseAllChannels(err);

                await _writer.StopAsync().ConfigureAwait(false);

                if (!_leaveOpen)
                {
                    if (_transport is IDisposable d)
                    {
                        d.Dispose();
                    }
                }

                _sessionFault.TrySetResult();
                IsClosed = true;
            }
        }
        finally
        {
            _closeLock.Release();
        }
    }

    /// <summary>
    /// Disposes the session asynchronously, closing it and releasing all resources.
    /// </summary>
    /// <returns>A <see cref="ValueTask"/> representing the asynchronous dispose operation.</returns>
    public async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            await CloseAsync();

            Stats?.Dispose();
            Stats = null;
            Metrics?.Dispose();

            _disposed = true;
        }
    }

    private async Task KeepAlive()
    {
        try
        {
            while (!_keepAliveToken.IsCancellationRequested)
            {
                try
                {
                    this.RTT = await PingAsync(_keepAliveToken.Token).ConfigureAwait(false);
                    Metrics?.RecordRtt(this.RTT.Value);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                }

                await Task.Delay(_sessionOptions.KeepAliveInterval, _keepAliveToken.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    #region channel adapter

    ValueTask IChannelSessionAdapter.SendFrameAsync(Frame frame, CancellationToken cancellationToken)
    {
        return _writer.WriteAsync(frame, cancellationToken);
    }

    void IChannelSessionAdapter.EnqueueFrame(Frame frame)
    {
        _writer.EnqueueFrame(frame);
    }

    ValueTask IChannelSessionAdapter.FlushWritesAsync(CancellationToken cancellationToken)
    {
        return _writer.FlushAsync(cancellationToken);
    }

    Task IChannelSessionAdapter.SessionFault => _sessionFault.Task;

    TimeSpan IChannelSessionAdapter.StreamSendTimeout => _sessionOptions.StreamSendTimeout;

    TimeSpan IChannelSessionAdapter.StreamCloseTimeout => _sessionOptions.StreamCloseTimeout;

    void IChannelSessionAdapter.ChannelDisconnect(SessionChannel channel)
    {
        _channelManager.RemoveChannel(channel.Id);
    }

    void IChannelSessionAdapter.ChannelAcknowledge(SessionChannel channel, bool accept)
    {
        channel.RemoteAckReceived();

        if (!_channelManager.TryCompleteConnect(channel.Id, channel))
        {
            if (!accept)
            {
                _channelManager.FailConnect(channel.Id, accept);
            }
        }
    }

    #endregion
}