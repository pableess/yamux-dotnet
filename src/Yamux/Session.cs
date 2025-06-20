
using System.Buffers;
using System.Collections.Concurrent;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.IO.Pipelines;
using System.Net.Sockets;
using System.Threading.Channels;
using Yamux.Internal;
using Yamux.Protocol;

namespace Yamux;

/// <summary>
/// A yamux session represents a multiplexed connection between two peers.  This can be used to create multiple logical streams over a single connection using the yamux protocol defined by hanshicorp.
/// https://github.com/hashicorp/yamux/blob/master/spec.md
/// </summary>
/// <remarks>
/// A Yamux session allows multiple logical streams over a single connection using the Yamux protocol.
/// </remarks>
public class Session : IChannelSessionAdapter, IDisposable,  IAsyncDisposable
{
    private readonly static TraceSource SessionTracer = new TraceSource("Yamux.Session");

    public enum State { Open, Closing, Closed, Faulted }

    private readonly FrameFormatterBase _formatter;
    private readonly StreamIdGenerator _idGenerator;
    private readonly ConcurrentDictionary<uint, SessionChannel> _channels;
    private readonly ConcurrentDictionary<uint, TaskCompletionSource<long>> _pings;

    private readonly ConcurrentDictionary<uint, TaskCompletionSource<IDuplexSessionChannel>> _connects;
    private readonly Channel<SessionChannel> _acceptQueue;

    private Task? _keepAlive;
    private bool _started;
    private bool _remoteGoAway;
    private readonly CancellationTokenSource _keepAliveToken;
    private readonly CancellationTokenSource _readToken;
    private readonly SessionOptions _sessionOptions;
    private SemaphoreSlim _closingLock;
    private volatile bool _disposed;
    uint pingId = 0;

    /// <summary>
    /// Creates a yamux session over a data stream
    /// </summary>
    /// <param name="frameFormatter">the frame formatter</param>
    /// <param name="isClient">if this end of the session originated from the client side of the connection</param>
    /// <param name="options"></param>
    internal Session(FrameFormatterBase frameFormatter, bool isClient, SessionOptions? options = null)
    {
        _sessionOptions = options ?? new SessionOptions();
        _formatter = frameFormatter;
        _idGenerator = new StreamIdGenerator(!isClient);
        _channels = new ConcurrentDictionary<uint, SessionChannel>();
        _connects = new ConcurrentDictionary<uint, TaskCompletionSource<IDuplexSessionChannel>>();
        _pings = new ConcurrentDictionary<uint, TaskCompletionSource<long>>();
        _keepAliveToken = new CancellationTokenSource();
        _readToken = new CancellationTokenSource();

        _closingLock = new SemaphoreSlim(1);

        // accept the channel queue
        _acceptQueue = Channel.CreateBounded<SessionChannel>(new BoundedChannelOptions(_sessionOptions.AcceptBacklog)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = true,
        });

        if (_sessionOptions.EnableStatistics)
        {
            this.Stats = new Statistics(_sessionOptions.StatisticsSampleInterval, _keepAliveToken.Token);
        }
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
        var channel = await _acceptQueue.Reader.ReadAsync(cancel ?? CancellationToken.None);
        await channel.ApplyOptionsAsync(channelOptions, cancel ?? CancellationToken.None);
        return channel;
    }

    /// <summary>
    /// Sends a ping message and waits for the a response to measure the RTT (Roud Trip Time)
    /// </summary>
    /// <param name="token"></param>
    /// <returns>RTT</returns>
    public async Task<TimeSpan> PingAsync(CancellationToken cancellation)
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
        await _formatter.WritePingAsync(opaqueValue, cancellation);

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

        Run().ContinueWith(async t => 
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


    private async Task CloseAsync(SessionException? err)
    {
        await _closingLock.WaitAsync();
        using var release = new SemaphoreReleaser(_closingLock);

        if (!IsClosed)
        {
            try
            {
                // send a go away frame to the remote peer, so that they will not create new streams
                await _formatter.WriteGoAwayAsync(err?.GoAwayCode ?? SessionTermination.Normal, CancellationToken.None);
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
            CloseInternal(err);
        }
    }

    private void Close(SessionException? err)
    {
        _closingLock.Wait();
        using var release = new SemaphoreReleaser(_closingLock);

        if (!IsClosed)
        {
            // send a go away frame to the remote peer, so that they will not create new streams
            try
            {
                _formatter.WriteGoAway(err?.GoAwayCode ?? SessionTermination.Normal);
            }
            catch (YamuxException ex)
            {
                SessionTracer.TraceInformation("[Warn] yamux: Unbable to send GoAway - {0}", ex.Message);
            }

            // shutdown keep alive loop
            if (_keepAlive != null)
            {
                if (_keepAliveToken.IsCancellationRequested)
                {
                    _keepAliveToken.Dispose();
                }
                else
                {
                    _keepAlive.ContinueWith(t =>
                    {
                        _keepAliveToken.Dispose();
                    });

                    _keepAliveToken.Cancel();
                }
            }
            CloseInternal(err);
        }
    }

    private void CloseInternal(SessionException? err = null) 
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
        foreach (var c in currentChannels)
        { 
            c.ForceClose(err);
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            Close(null);
            _formatter.Dispose();
            _closingLock.Dispose();

            Stats?.Dispose();
            Stats = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            await CloseAsync();
            await _formatter.DisposeAsync();
            _closingLock?.Dispose();

            Stats?.Dispose();
            Stats = null;
        }
    }

    private async Task Run() 
    {
        while (!_readToken.IsCancellationRequested)
        {
            try
            {
                var frameHeader = await _formatter.ReadFrameHeaderAsync(_readToken.Token);

                // TODO;
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
            catch (OperationCanceledException)
            {
                // shutting down, ignore
            }
        }
    }

    private async ValueTask HandleGoAway(Yamux.Protocol.FrameFormatterBase.FrameHeader frameHeader, CancellationToken token)
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

    private async Task HandleWindowUpdateFrame(Yamux.Protocol.FrameFormatterBase.FrameHeader frameHeader, CancellationToken token)
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

        channel.HandleWindowUpdate(frameHeader.Length, frameHeader.Flags);
    }

    private async Task HandleDataFrame(FrameFormatterBase.FrameHeader frameHeader, CancellationToken token)
    {
        var channel = await GetChannelAsync(frameHeader.StreamId, frameHeader.Flags, token);

        Stats?.UpdateReceived(frameHeader.Length);

        if (channel != null)
        {
            await channel.HandleDataFrameAsync(frameHeader.Length, frameHeader.Flags, token);
        }
        else
        {
            // local channel has already been disposed, ie we probably sent a RST
            var payloadLength = frameHeader.Length;
            if (payloadLength > 0) 
            {
                //read an discard the frame 

                SessionTracer.TraceInformation($"[WARN] yamux: discarding data for stream {frameHeader.StreamId}");

                using var buffer = MemoryPool<byte>.Shared.Rent((int)payloadLength);
                await _formatter.ReadPayloadDataAsync(buffer.Memory, token);
            }
        }
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

    private void HandlePingFrame(FrameFormatterBase.FrameHeader frameHeader, CancellationToken token)
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
            _ = _formatter.WritePingAckAsync(frameHeader.Length, token);
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

    async ValueTask IChannelSessionAdapter.WriteDataFrameAsync(uint streamId, Flags flags, ReadOnlyMemory<byte> payload, CancellationToken cancel)
    {
        try
        {
            Stats?.UpdateSent((uint)payload.Length);
            await _formatter.WriteDataFrameAsync(streamId, flags, payload, cancel);
        }
        catch (SessionException ye)
        {
            await this.CloseAsync(ye);
            throw;
        }
        catch (Exception)
        {
            await this.CloseAsync(new SessionException(SessionErrorCode.StreamError, SessionTermination.InternalError));
            throw;
        }
    }

    void IChannelSessionAdapter.WriteDataFrame(uint streamId, Flags flags, ReadOnlyMemory<byte> payload)
    {
        try
        {
            Stats?.UpdateSent((uint)payload.Length);
            _formatter.WriteDataFrame(streamId, flags, payload);
        }
        catch (SessionException ye)
        {
            this.Close(ye);
            throw;
        }
        catch (Exception)
        {
            this.Close(new SessionException(SessionErrorCode.StreamError, SessionTermination.InternalError));
            throw;
        }
    }


    async ValueTask IChannelSessionAdapter.WriteWindowUpdateFrameAsync(uint streamId, Flags flags, uint windowSizeIncrement, CancellationToken cancel)
    {
        try
        {
            await _formatter.WriteWindowUpdateFrameAsync(streamId, flags, windowSizeIncrement, cancel);
        }
        catch (SessionException ye) 
        {
            await this.CloseAsync(ye);
            throw;
        }
        catch (Exception)
        {
            await this.CloseAsync(new SessionException(SessionErrorCode.StreamError, SessionTermination.InternalError));
            throw;
        }
    }

    void IChannelSessionAdapter.WriteWindowUpdateFrame(uint streamId, Flags flags, uint windowSizeIncrement)
    {
        try
        {
            _formatter.WriteWindowUpdateFrame(streamId, flags, windowSizeIncrement);
        }
        catch (SessionException ye)
        {
            this.Close(ye);
        }
        catch (Exception)
        {
            this.Close(new SessionException(SessionErrorCode.StreamError, SessionTermination.InternalError));
        }
    }

    void IChannelSessionAdapter.Flush() => _formatter.Flush();

    Task IChannelSessionAdapter.FlushAsync(CancellationToken cancel) => _formatter.FlushAsync(cancel);

    async ValueTask<int> IChannelSessionAdapter.ReadPayloadDataAsync(System.Memory<byte> memory, System.Threading.CancellationToken cancel)
    {
        try
        {
            return await _formatter.ReadPayloadDataAsync(memory, cancel);
        }
        catch (SessionException ye)
        {
            await this.CloseAsync(ye);
            throw;
        }
        catch (Exception)
        {
            await this.CloseAsync(new SessionException(SessionErrorCode.StreamError, SessionTermination.InternalError));
            throw;
        }
       
    }
        

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
