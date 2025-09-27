
using System.Buffers;
using System.Collections.Concurrent;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.IO.Pipelines;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using System.Threading.Tasks;
using Yamux.Internal;
using Yamux.Protocol;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace Yamux;

/// <summary>
/// A yamux session represents a multiplexed connection between two peers.  This can be used to create multiple logical streams over a single connection using the yamux protocol defined by hanshicorp.
/// https://github.com/hashicorp/yamux/blob/master/spec.md
/// </summary>
/// <remarks>
/// A Yamux session allows multiple logical streams over a single connection using the Yamux protocol.
/// </remarks>
public sealed class Session : IChannelSessionAdapter, IAsyncDisposable
{
    public readonly static TraceSource SessionTracer = new TraceSource("Yamux.Session");

    public enum State { Open, Closing, Closed, Faulted }

    private readonly ITransport _peer;
    private readonly ConnectionReader _reader;
    private readonly ConnectionWriter _writer;
    private readonly StreamIdGenerator _idGenerator;
    private readonly ConcurrentDictionary<uint, SessionChannel> _channels;
    private readonly ConcurrentDictionary<uint, TaskCompletionSource<long>> _pings;

    private readonly ConcurrentDictionary<uint, TaskCompletionSource<IDuplexSessionChannel>> _connects;
    private readonly Channel<SessionChannel> _acceptQueue;

    private readonly CancellationTokenSource _readToken;

    private Task? _keepAlive;
    private bool _started;
    private bool _remoteGoAway;
    private readonly CancellationTokenSource _keepAliveToken;
    private readonly SessionOptions _sessionOptions;
    private volatile bool _disposed;
    uint pingId = 0;

    /// <summary>
    /// Creates a yamux session over a data stream
    /// </summary>
    /// <param name="frameFormatter">the frame formatter</param>
    /// <param name="isClient">if this end of the session originated from the client side of the connection</param>
    /// <param name="options"></param>
    internal Session(ITransport connection, bool isClient, SessionOptions? options = null)
    {
        _sessionOptions = options ?? new SessionOptions();
        _peer = connection ?? throw new ArgumentNullException(nameof(connection));
        _idGenerator = new StreamIdGenerator(!isClient);
        _channels = new ConcurrentDictionary<uint, SessionChannel>();
        _connects = new ConcurrentDictionary<uint, TaskCompletionSource<IDuplexSessionChannel>>();
        _pings = new ConcurrentDictionary<uint, TaskCompletionSource<long>>();
        _keepAliveToken = new CancellationTokenSource();
        _readToken = new CancellationTokenSource();

        if (_sessionOptions.EnableStatistics)
        {
            this.Stats = new Statistics(_sessionOptions.StatisticsSampleInterval, _keepAliveToken.Token);
        }
        _reader = new ConnectionReader(_peer);
        _writer = new ConnectionWriter(_peer, Stats);

        // accept the channel queue
        _acceptQueue = Channel.CreateBounded<SessionChannel>(new BoundedChannelOptions(_sessionOptions.AcceptBacklog)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = true,
        });
    }

    /// <summary>
    /// Gets the most recent latency measurement from keep alive ping
    /// </summary>
    public TimeSpan? RTT { get; private set; }

    /// <summary>
    /// Gets the statistics
    /// </summary>
    public Statistics? Stats { get; private set; }

    /// <summary>
    /// Opens a new channel on the session.  The default behavior of this operation is to not wait until an acknowlegment is received before 
    /// returing the channel.  Yamux spec allows sending data to the remote party before an acknowledgment is recieved.
    /// </summary>
    /// <param name="options"></param>
    /// <param name="cancel"></param>
    /// <returns></returns>
    public ValueTask<IDuplexSessionChannel> OpenChannelAsync(SessionChannelOptions options, bool waitForAcknowledgement = false, CancellationToken? cancel = null)
    {
        if (IsClosed) 
        {
            throw new SessionException(SessionErrorCode.SessionShutdown, "Session is closed");
        }

        if (_remoteGoAway) 
        {
            throw new SessionException(SessionErrorCode.SessionShutdown, "Cannot open a new session because Go Away has been received");
        }

        // create a new channel
        var id = _idGenerator.Next();
        var channel = new SessionChannel(this, id, options);

        _channels.TryAdd(id, channel);

        // send a window update
        channel.SendWindowUpdate(0);

        // if we are waiting for the acknowledgement, then create a task completion source and return that
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

            _connects.TryAdd(id, tcs);
            return new ValueTask<IDuplexSessionChannel>(tcs.Task);
        }

        return ValueTask.FromResult((IDuplexSessionChannel)channel);
    }

    /// <summary>
    /// Opens a new channel on the session.  The default behavior of this operation is to not wait until an acknowlegment is received before 
    /// returing the channel.  Yamux spec allows sending data to the remote party before an acknowledgment is recieved.
    /// </summary>
    /// <param name="waitForAcknowledgement"></param>
    /// <param name="cancel"></param>
    /// <returns></returns>
    public ValueTask<IDuplexSessionChannel> OpenChannelAsync(bool waitForAcknowledgement = false, CancellationToken? cancel = null) =>
        this.OpenChannelAsync(_sessionOptions.DefaultChannelOptions, waitForAcknowledgement, cancel);


    /// <summary>
    /// Accepts a new channel using the default options
    /// </summary>
    /// <param name="cancel"></param>
    /// <returns></returns>
    public ValueTask<IDuplexSessionChannel> AcceptAsync(CancellationToken? cancel = null) => AcceptChannelAsync(_sessionOptions.DefaultChannelOptions, cancel);

    /// <summary>
    /// Accepts a new session channel using the default options
    /// </summary>
    /// <param name="cancel"></param>
    /// <returns></returns>
    public ValueTask<IDuplexSessionChannel> AcceptAsync(SessionChannelOptions channelOptions, CancellationToken? cancel) => AcceptChannelAsync(_sessionOptions.DefaultChannelOptions, cancel);

    /// <summary>
    /// Accepts a new channel with read only semantics
    /// </summary>
    /// <param name="cancel"></param>
    /// <returns></returns>
    public async ValueTask<IReadOnlySessionChannel> AcceptReadOnlyChannelAsync(CancellationToken? cancel)
    {
        var channel = await AcceptChannelAsync(_sessionOptions.DefaultChannelOptions, cancel);
        return channel;
    }

    /// <summary>
    /// Accpets a new channel with custom options
    /// </summary>
    /// <param name="channelOptions"></param>
    /// <param name="cancel"></param>
    /// <returns></returns>
    private async ValueTask<IDuplexSessionChannel> AcceptChannelAsync(SessionChannelOptions channelOptions, CancellationToken? cancel = null)
    {
        try
        {
            var channel = await _acceptQueue.Reader.ReadAsync(cancel ?? CancellationToken.None);
            await channel.ApplyOptionsAsync(channelOptions, cancel ?? CancellationToken.None);
            return channel;
        }
        catch (ChannelClosedException)
        {
            throw new SessionException(SessionErrorCode.SessionShutdown, "Session has been closed");
        }
    }

    /// <summary>
    /// Sends a ping message and waits for the a response to measure the RTT (Roud Trip Time)
    /// </summary>
    /// <param name="token"></param>
    /// <returns>RTT</returns>
    public async ValueTask<TimeSpan> PingAsync(CancellationToken cancellation)
    {
        var opaqueValue = Interlocked.Increment(ref pingId);

        TaskCompletionSource<long> tcs = new TaskCompletionSource<long>();

        // if the ping request was cancelled, we can remove it from our tracked pings
        using var registration = cancellation.Register(() => 
        {
            tcs.TrySetCanceled();
            
            _pings.TryRemove(opaqueValue, out _);
        });

        var start = Stopwatch.GetTimestamp();
        await _writer.WriteAsync(Frame.CreatePingRequestFrame(opaqueValue), cancellation);

        _pings.TryAdd(opaqueValue, tcs);

        var stop = await tcs.Task;
        return TimeSpan.FromTicks(stop - start);
    }

    /// <summary>
    /// Starts the session
    /// </summary>
    public void Start()
    {
        _sessionOptions.DefaultChannelOptions?.Validate();


        if (IsClosed)
            throw new SessionException(SessionErrorCode.SessionShutdown, "Session has been closed.");

        if (_started)
            return;

        _writer.Start();

        ReadFrames().ContinueWith(async t => 
        {
            if (t.Exception != null) 
            {
                SessionTracer.TraceInformation("[Err]: Session receive loop faulted");

                // fault all the channels
                await this.CloseAsync((t.Exception.Flatten()?.InnerException as SessionException) ?? new SessionException(SessionErrorCode.StreamError, "Underlying stream encounted an error", t.Exception, SessionTermination.InternalError));
            }
        });

        if (_sessionOptions.EnableKeepAlive)
        {
            _keepAlive = KeepAlive();
        }

        _started = true;
    }

    public bool IsClosed => _readToken.IsCancellationRequested;

    public Task CloseAsync() => this.CloseAsync(null);

    private async Task CloseAsync(SessionException? err = null)
    {
        if (!IsClosed)
        {
            try
            {
                // send a go away frame to the remote peer, so that they will not create new streams
                await _writer.WriteAsync(Frame.CreateGoAwayFrame(err?.GoAwayCode ?? SessionTermination.Normal), CancellationToken.None);
            }
            catch (Exception ex)
            {
                SessionTracer.TraceInformation("[Warn] yamux: Unbable to send GoAway - {0}", ex.Message);
            }
           
            // shutdown keep alive loop
            if (_keepAlive != null)
            {
                _keepAliveToken.Cancel();

                await _keepAlive;

                _keepAliveToken.Dispose();
            }
            await CloseInternal(err);
        }
    }

    private async ValueTask CloseInternal(SessionException? err = null) 
    {       
        _readToken.Cancel(); // shutdown read pump
        _acceptQueue.Writer.TryComplete(); // signal no more accepted channels

        var inFlightConnects = _connects.Values;
        foreach (var c in inFlightConnects)
        {
            c.TrySetException(new SessionChannelException(ChannelErrorCode.SessionClosed, "The session has been closed"));
        }
        _connects.Clear();

        var currentChannels = _channels.Values;
        _channels.Clear();

        if (currentChannels.Count > 0)
        {
            foreach (var c in currentChannels)
            {
                if (!c.IsClosed)
                {
                    await c.CloseAsync();
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            await CloseAsync();

            Stats?.Dispose();
            Stats = null;

            _disposed = true;
        }
    }

    private async Task ReadFrames() 
    {
        await foreach(var frameHeader in _reader.ReadFramesAsync(_readToken.Token))
        {
            switch (frameHeader.FrameType)
            {
                case FrameType.Data:
                    await HandleDataFrame(frameHeader, _readToken.Token);
                    break;
                case FrameType.WindowUpdate:
                    await HandleWindowUpdateFrame(frameHeader, _readToken.Token);
                    break;
                case FrameType.Ping:
                    HandlePingFrame(frameHeader, _readToken.Token);
                    break;
                case FrameType.GoAway:
                    await HandleGoAway(frameHeader, _readToken.Token);
                    break;
                default:
                    throw new NotImplementedException();
            }
        }
    }

    private async ValueTask HandleGoAway(FrameHeader frameHeader, CancellationToken token)
    {
        // how to handle go away
        switch (frameHeader.Length)
        {
            case (uint)SessionTermination.Normal:
                _remoteGoAway = true;
                await this.CloseAsync();
                break;
            case (uint)SessionTermination.ProtocolError:
                SessionTracer.TraceInformation("[Err] yamux: received protocol error go away");
                throw new SessionException(SessionErrorCode.SessionShutdown, SessionTermination.ProtocolError);
            case (uint)SessionTermination.InternalError:
                SessionTracer.TraceInformation("[Err] yamux: received internal error go away");
                throw new SessionException(SessionErrorCode.SessionShutdown, SessionTermination.InternalError);
            default:
                SessionTracer.TraceInformation("[Err] yamux: received unexpected go away");
                throw new SessionException(SessionErrorCode.SessionShutdown, SessionTermination.InternalError);
        }
    }

    private async Task HandleWindowUpdateFrame(FrameHeader frameHeader, CancellationToken token)
    {
        // todo: handle stream frames
        var channel = await GetChannelAsync(frameHeader.StreamId, frameHeader.Flags, token);

        if (channel == null)
        {
            // local channel has already been disposed 
            SessionTracer.TraceInformation($"[WARN] yamux: frame for unknown stream {frameHeader.StreamId}");
            return;
        }

        token.ThrowIfCancellationRequested();

        channel.UpdateRemoteWindow(frameHeader.Length, frameHeader.Flags);
    }

    private async Task HandleDataFrame(FrameHeader frameHeader, CancellationToken token)
    {
        var channel = await GetChannelAsync(frameHeader.StreamId, frameHeader.Flags, token);

        // fill the channel's pipe with the data
        await ReadPayloadData(frameHeader.Length, channel, token);
    }

    /// <summary>
    /// Read frame payload data from the connection and copys it to the channel's input pipe
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    private async ValueTask ReadPayloadData(uint payloadLength, SessionChannel? channel, CancellationToken cancellationToken)
    {
        int bytesToRead = (int)payloadLength;

        // since closing the channel is not coordinated with locks, we need to handle the pipe being closed while we are writing to it
        // this is why we track its state so that if it is closed during this operation we can continue discarding the rest of the frame data
        bool pipeOpen = channel != null && !channel.IsClosed;
        do
        {
            if (pipeOpen && channel != null)
            {
                try
                {
                    var pipeWriter = channel.GetPipeWriter();
                    var buffer = pipeWriter.GetMemory();
                                
                    if (buffer.Length > bytesToRead)
                    {
                        buffer = buffer.Slice(0, bytesToRead);
                    }

                    var read = await _reader.ReadFramePayloadAsync(buffer, cancellationToken);

                    pipeWriter.Advance(read);

                    bytesToRead -= read;

                    var flushResult = await pipeWriter.FlushAsync(cancellationToken);

                    // reader has indicated that it is complete, we should close the channel as well
                    if (flushResult.IsCompleted)
                    {
                        // close the channel
                        channel.CloseWrite();

                        await pipeWriter.CompleteAsync();

                    }
                }
                catch (InvalidOperationException)
                {
                    // the channel has been closed, we cannot write to it
                    pipeOpen = false;
                }

                Stats?.UpdateReceived(payloadLength);
                channel.Stats?.UpdateReceived(payloadLength);
            }
            else 
            {
                SessionTracer.TraceInformation("[WARN] yamux: channel is closed, discarding data for stream {0}", channel?.Id ?? 0);

                // if the channel has already been closed, then we should still read the session data for the rest of the frame and discard it
                // the session is not corrupted
                using var buffer = MemoryPool<byte>.Shared.Rent(4048);

                var read = await _reader.ReadFramePayloadAsync(buffer.Memory, cancellationToken);
                bytesToRead -= read;

                Stats?.UpdateReceived(payloadLength);
            }
        } while (bytesToRead > 0);
    }

    private async ValueTask<SessionChannel?> GetChannelAsync(uint id, Flags flags, CancellationToken cancel)
    {
        // if we already have a channel for the id
        if (_channels.TryGetValue(id, out SessionChannel? channel)) 
        {
            return channel;
        }

        if (flags.HasFlag(Flags.SYN))
        {
            // create a new channel
            SessionChannel newChannel = new(this, id, this._sessionOptions.DefaultChannelOptions);
            _channels.TryAdd(id, newChannel);

            await _acceptQueue.Writer.WriteAsync(newChannel, cancel);
            return newChannel;
        }

        return null;
    }

    private void HandlePingFrame(FrameHeader frameHeader, CancellationToken token)
    {
        if (frameHeader.Flags.HasFlag(Flags.ACK))
        {
            // receive the ping ack
            if (_pings.TryRemove(frameHeader.Length, out var tcs))
            {
                // complete awating using timestamp as this is more accurate than meausring after task completions begins to execute
                tcs.TrySetResult(Stopwatch.GetTimestamp());
            }
        }
        else 
        {
            // no need to block reading, send an ack for the ping from the remote party
            _ = _writer.WriteAsync(Frame.CreatePingResponseFrame(frameHeader), token);
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

                    // keep the last few RTT's 
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    // TODO: log ping error

                    // ignore, but continue to ping
                }

                await Task.Delay(_sessionOptions.KeepAliveInterval, _keepAliveToken.Token);

            }
        }
        catch (OperationCanceledException)
        {
            // ignore
        }
    }

    #region channel adapter

    /// <summary>
    /// Gets the connection writer, used to sequentially send frames to the remote peer.
    /// </summary>
    ConnectionWriter IChannelSessionAdapter.Writer => _writer;

    void IChannelSessionAdapter.ChannelDisconnect(SessionChannel channel)
    {
        // disconnect the channel
        _channels.TryRemove(channel.Id, out _);
    }

    void IChannelSessionAdapter.ChannelAcknowledge(SessionChannel channel, bool accept)
    {
        // if the caller is awaiting the acknowledgement, then they don't yet own the channel and cannot dispose of it, if it was rejected
        if (_connects.TryRemove(channel.Id, out var connectTcs)) 
        {
            if (accept)
            {
                connectTcs.TrySetResult(channel);
            }
            else
            {
                connectTcs.TrySetException(new SessionChannelException(ChannelErrorCode.ChannelRejected, "Session channel was rejected by the remote party"));
                channel.Dispose();
            }
        }
        else if (!accept)  // caller already owns the channel and can call dispose themselves
        {
            _channels.TryRemove(channel.Id, out _);
        }
    }

    #endregion
}
