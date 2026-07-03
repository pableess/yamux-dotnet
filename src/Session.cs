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
    private readonly ConnectionWriter _writer;
    private readonly PingManager _pingManager;
    private readonly ChannelManager _channelManager;
    private readonly FrameReader _frameReader;
    private readonly StreamIdGenerator _idGenerator;
    private readonly TaskCompletionSource _sessionFault = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal readonly YamuxMetrics? Metrics;

    private readonly bool _keepTransportOpenOnClose;

    private Task? _keepAlive;
    private bool _started;

    private readonly CancellationTokenSource _keepAliveToken;
    private readonly SessionOptions _sessionOptions;
    private volatile bool _disposed;

    internal Session(ITransport connection, bool isClient, bool keepTransportOpenOnClose = false, SessionOptions? options = null)
    {
        _sessionOptions = options ?? new SessionOptions();
        _transport = connection ?? throw new ArgumentNullException(nameof(connection));
        _keepTransportOpenOnClose = keepTransportOpenOnClose;
        _idGenerator = new StreamIdGenerator(!isClient);
        _pingManager = new PingManager();
        _keepAliveToken = new CancellationTokenSource();

        if (_sessionOptions.EnableStatistics)
        {
            this.Stats = new Statistics(_sessionOptions.StatisticsSampleInterval, _keepAliveToken.Token);
        }

        _channelManager = new ChannelManager(this, _sessionOptions.DefaultChannelOptions, _sessionOptions.AcceptBacklog, null, _sessionOptions.MaxChannels);
        _writer = new ConnectionWriter(_transport, Stats);
        _frameReader = new FrameReader(
            new ConnectionReader(_transport),
            _channelManager,
            _pingManager,
            _writer,
            Stats,
            null,
            ex => CloseAsync((ex as SessionException) ?? new SessionException(SessionErrorCode.StreamError, "Underlying stream encountered an error", ex, SessionTermination.InternalError)));

        if (_sessionOptions.EnableMetrics)
        {
            Metrics = new YamuxMetrics(
                sessionId: isClient ? "client" : "server",
                isClient: isClient,
                getActiveChannels: () => _channelManager.ActiveChannelCount,
                getWriteQueueDepth: () => 0);

            _channelManager.SetMetrics(Metrics);
            _frameReader.SetMetrics(Metrics);
            _writer.SetMetrics(Metrics);
        }
    }

    public TimeSpan? RTT { get; private set; }

    public Statistics? Stats { get; private set; }

    public ValueTask<IDuplexSessionChannel> OpenChannelAsync(SessionChannelOptions options, bool waitForAcknowledgement = false, CancellationToken? cancel = null)
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

            if (cancel != null)
            {
                var registration = cancel.Value.Register(() =>
                {
                    tcs.TrySetCanceled();
                });
                tcs.Task.ContinueWith(t =>
                {
                    registration.Dispose();
                });
            }

            _channelManager.TrackConnect(id, tcs);
            return new ValueTask<IDuplexSessionChannel>(tcs.Task);
        }

        return ValueTask.FromResult((IDuplexSessionChannel)channel);
    }

    public ValueTask<IDuplexSessionChannel> OpenChannelAsync(bool waitForAcknowledgement = false, CancellationToken? cancel = null) =>
        this.OpenChannelAsync(_sessionOptions.DefaultChannelOptions, waitForAcknowledgement, cancel);

    public ValueTask<IDuplexSessionChannel> AcceptAsync(CancellationToken? cancel = null) => AcceptChannelAsync(_sessionOptions.DefaultChannelOptions, cancel);

    public ValueTask<IDuplexSessionChannel> AcceptAsync(SessionChannelOptions channelOptions, CancellationToken? cancel) => AcceptChannelAsync(channelOptions, cancel);

    public async ValueTask<IReadOnlySessionChannel> AcceptReadOnlyChannelAsync(CancellationToken? cancel)
    {
        var channel = await AcceptChannelAsync(_sessionOptions.DefaultChannelOptions, cancel);
        return channel;
    }

    private async ValueTask<IDuplexSessionChannel> AcceptChannelAsync(SessionChannelOptions channelOptions, CancellationToken? cancel = null)
    {
        try
        {
            var channel = await _channelManager.WaitForAcceptAsync(cancel ?? CancellationToken.None);

            if (this.IsClosed || channel.IsClosed)
            {
                throw new SessionException(SessionErrorCode.SessionShutdown, "Session or channel has been closed");
            }

            channel.Accept();

            await channel.ApplyOptionsAsync(channelOptions, cancel ?? CancellationToken.None);
            return channel;
        }
        catch (ChannelClosedException)
        {
            throw new SessionException(SessionErrorCode.SessionShutdown, "Session has been closed");
        }
    }

    public async ValueTask<TimeSpan> PingAsync(CancellationToken cancellation)
    {
        return await _pingManager.PingAsync(_writer, cancellation);
    }

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

    public bool IsClosed { get; private set; }

    public Task CloseAsync() => this.CloseAsync(null);

    public async Task<bool> CloseOpenChannelsAsync(TimeSpan timeout)
    {
        return await _channelManager.CloseOpenChannelsAsync(timeout);
    }

    public async Task GoAwayAsync(SessionTermination sessionTermination = SessionTermination.Normal, CancellationToken? cancel = null)
    {
        try
        {
            await _writer.WriteAsync(Frame.CreateGoAwayFrame(sessionTermination), cancel ?? CancellationToken.None);
            _channelManager.SetLocalGoAway();
        }
        catch (Exception ex)
        {
            SessionTracer.TraceInformation("[Warn] yamux: Unbable to send GoAway - {0}", ex.Message);
        }
    }

    private async Task CloseAsync(SessionException? err = null)
    {
        await _closeLock.WaitAsync();

        try
        {
            if (!IsClosed)
            {
                if (_keepAlive != null)
                {
                    _keepAliveToken.Cancel();
                    await _keepAlive;
                    _keepAliveToken.Dispose();
                    _keepAlive = null;
                }

                await GoAwayAsync(err?.GoAwayCode ?? SessionTermination.Normal);

                _channelManager.FailAllConnects(
                    new SessionChannelException(ChannelErrorCode.SessionClosed, "The session has been closed"));

                _channelManager.CloseAllChannels(err);

                _frameReader.Stop();

                await _frameReader.WaitForCompletionAsync(TimeSpan.FromSeconds(2));

                if (!_keepTransportOpenOnClose)
                {
                    _transport.Close();

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
                    this.RTT = await PingAsync(_keepAliveToken.Token);
                    Metrics?.RecordRtt(this.RTT.Value);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                }

                await Task.Delay(_sessionOptions.KeepAliveInterval, _keepAliveToken.Token);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    #region channel adapter

    ValueTask IChannelSessionAdapter.SendFrameAsync(Frame frame, CancellationToken cancel)
    {
        return _writer.WriteAsync(frame, cancel);
    }

    Task IChannelSessionAdapter.SessionFault => _sessionFault.Task;

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