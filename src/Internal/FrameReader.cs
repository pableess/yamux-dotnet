using System.Buffers;
using System.Diagnostics;
using Yamux.Protocol;

namespace Yamux.Internal;

internal class FrameReader
{
    private readonly ConnectionReader _reader;
    private readonly ChannelManager _channelManager;
    private readonly PingManager _pingManager;
    private readonly SessionFrameWriter _writer;
    private readonly Statistics? _stats;
    private YamuxMetrics? _metrics;
    private readonly Func<Exception, Task> _onFault;
    private readonly CancellationTokenSource _readToken = new();
    private readonly SessionOptions _sessionOptions;

    private Task? _readLoop;
    private volatile bool _isStopping;

    public FrameReader(
        ConnectionReader reader,
        ChannelManager channelManager,
        PingManager pingManager,
        SessionFrameWriter writer,
        Statistics? stats,
        YamuxMetrics? metrics,
        Func<Exception, Task> onFault,
        SessionOptions sessionOptions)
    {
        _reader = reader;
        _channelManager = channelManager;
        _pingManager = pingManager;
        _writer = writer;
        _stats = stats;
        _metrics = metrics;
        _onFault = onFault;
        _sessionOptions = sessionOptions;
    }

    internal void SetMetrics(YamuxMetrics? metrics) => _metrics = metrics;

    public void Start()
    {
        _readLoop = RunAsync();
    }

    public void Stop()
    {
        _isStopping = true;
        _reader.Stop();
        _readToken.Cancel();
    }

    public void PrepareForClose()
    {
        _isStopping = true;
    }

    public CancellationToken CancellationToken => _readToken.Token;

    public async Task WaitForCompletionAsync(TimeSpan timeout)
    {
        if (_readLoop != null)
        {
            if (_readLoop != await Task.WhenAny(_readLoop, Task.Delay(timeout)))
            {
                _readToken.Cancel();
            }
        }
    }

    public async Task WaitForCompletionAsync()
    {
        if (_readLoop != null)
        {
            await _readLoop.ConfigureAwait(false);
        }
    }

    private async Task RunAsync()
    {
        try
        {
            await foreach (var frameHeader in _reader.ReadFramesAsync(_readToken.Token))
            {
                _metrics?.FrameReceived();

                switch (frameHeader.FrameType)
                {
                    case FrameType.Data:
                        await HandleDataFrame(frameHeader, _readToken.Token);
                        break;
                    case FrameType.WindowUpdate:
                        await HandleWindowUpdateFrame(frameHeader, _readToken.Token);
                        break;
                    case FrameType.Ping:
                        await _pingManager.HandlePingAsync(frameHeader, _writer, _readToken.Token).ConfigureAwait(false);
                        break;
                    case FrameType.GoAway:
                        _channelManager.SetRemoteGoAway((SessionTermination)frameHeader.Length);
                        break;
                    default:
                        throw new NotImplementedException();
                }
            }
        }
        catch (Exception ex)
        {
            if (!_isStopping)
            {
                Session.SessionTracer.TraceInformation("[Err]: Session receive loop faulted");
                _metrics?.SessionError();
                _ = Task.Run(() => _onFault(ex));
            }
        }
    }

    private async Task HandleWindowUpdateFrame(FrameHeader frameHeader, CancellationToken token)
    {
        var channel = await _channelManager.GetOrCreateAsync(frameHeader.StreamId, frameHeader.Flags, _writer, token);

        if (channel == null)
        {
            Session.SessionTracer.TraceInformation($"[WARN] yamux: frame for unknown stream {frameHeader.StreamId}");
            return;
        }

        token.ThrowIfCancellationRequested();

        channel.UpdateRemoteWindow(frameHeader.Length, frameHeader.Flags);
    }

    private async Task HandleDataFrame(FrameHeader frameHeader, CancellationToken token)
    {
        var channel = await _channelManager.GetOrCreateAsync(frameHeader.StreamId, frameHeader.Flags, _writer, token).ConfigureAwait(false);

        if (channel == null)
        {
            Session.SessionTracer.TraceInformation("[WARN] yamux: discarding data frame for unknown stream {0}, sending RST", frameHeader.StreamId);

            _writer.EnqueueFrame(Frame.CreateWindowUpdateFrame(frameHeader.StreamId, Flags.RST, 0));

            await ReadPayloadData(frameHeader.Length, null, token, discard: true).ConfigureAwait(false);
            return;
        }

        if (frameHeader.Length > _sessionOptions.MaxIncomingFrameSize)
        {
            Session.SessionTracer.TraceEvent(TraceEventType.Error, 0, $"[Err] yamux: incoming frame size exceeded (stream: {channel.Id}, length: {frameHeader.Length}, max: {_sessionOptions.MaxIncomingFrameSize})");

            _writer.EnqueueFrame(Frame.CreateGoAwayFrame(SessionTermination.ProtocolError));
            throw new SessionException(SessionErrorCode.RecvWindowExceeded,
                $"incoming frame size exceeded (stream: {channel.Id})",
                SessionTermination.ProtocolError);
        }

        if (!channel.TryConsumeReceiveWindow(frameHeader.Length))
        {
            Session.SessionTracer.TraceEvent(TraceEventType.Error, 0, $"[Err] yamux: receive window exceeded (stream: {channel.Id}, length: {frameHeader.Length})");

            _writer.EnqueueFrame(Frame.CreateGoAwayFrame(SessionTermination.ProtocolError));
            throw new SessionException(SessionErrorCode.RecvWindowExceeded,
                $"receive window exceeded (stream: {channel.Id})",
                SessionTermination.ProtocolError);
        }

        // RST payloads are drained but not delivered. FIN and other flags are
        // applied only after the payload has been written so a data frame with
        // FIN does not lose its final bytes.
        bool discard = frameHeader.Flags.HasFlag((Flags)Flags.RST);
        await ReadPayloadData(frameHeader.Length, channel, token, discard).ConfigureAwait(false);

        channel.ProcessIncomingFlags(frameHeader.Flags);
    }

    private async ValueTask ReadPayloadData(uint payloadLength, SessionChannel? channel, CancellationToken cancellationToken, bool discard)
    {
        int remaining = (int)payloadLength;
        if (remaining == 0)
        {
            // A zero-length data frame carries no payload. Do not fall through to
            // the read path: that would either block or consume the next frame.
            return;
        }

        if (!discard && channel != null)
        {
            while (remaining > 0)
            {
                try
                {
                    await channel.WaitForPendingFlushAsync().ConfigureAwait(false);

                    var pipeWriter = channel.GetPipeWriter();
                    var buffer = pipeWriter.GetMemory();

                    if (buffer.Length > remaining)
                    {
                        buffer = buffer.Slice(0, remaining);
                    }

                    var read = await _reader.ReadFramePayloadAsync(buffer, cancellationToken).ConfigureAwait(false);

                    if (read == 0)
                    {
                        throw new SessionException(SessionErrorCode.StreamClosed, "Remote connection closed mid-frame");
                    }

                    pipeWriter.Advance(read);
                    remaining -= read;

                    var flushTask = pipeWriter.FlushAsync(cancellationToken);

                    if (flushTask.IsCompletedSuccessfully)
                    {
                        if (flushTask.Result.IsCompleted)
                        {
                            channel.CloseWrite();
                            await pipeWriter.CompleteAsync().ConfigureAwait(false);
                        }
                    }
                    else
                    {
                        channel.OffloadPipeFlush(flushTask, pipeWriter);
                    }

                    continue;
                }
                catch (InvalidOperationException)
                {
                    // The input pipe was completed (for example by the application
                    // or by a FIN); drain the rest of the frame without delivering it.
                    break;
                }
            }
        }

        if (remaining > 0)
        {
            using var discardBuffer = MemoryPool<byte>.Shared.Rent(4096);

            while (remaining > 0)
            {
                // Cap the read to the bytes still belonging to this frame. Reading
                // into the full rented buffer would consume the next frame header.
                var memory = discardBuffer.Memory;
                if (memory.Length > remaining)
                {
                    memory = memory.Slice(0, remaining);
                }

                var read = await _reader.ReadFramePayloadAsync(memory, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new SessionException(SessionErrorCode.StreamClosed, "Remote connection closed mid-frame");
                }

                remaining -= read;
            }
        }

        // Count each frame once, regardless of how many reads it took.
        _stats?.UpdateReceived(payloadLength);
        channel?.RecordReceivedBytes(payloadLength);
        _metrics?.BytesReceived(payloadLength);
    }
}