using System.Buffers;
using System.Diagnostics;
using Yamux.Protocol;

namespace Yamux.Internal;

internal class FrameReader
{
    private readonly ConnectionReader _reader;
    private readonly ChannelManager _channelManager;
    private readonly PingManager _pingManager;
    private readonly ConnectionWriter _writer;
    private readonly Statistics? _stats;
    private YamuxMetrics? _metrics;
    private readonly Func<Exception, Task> _onFault;
    private readonly CancellationTokenSource _readToken = new();

    private Task? _readLoop;

    public FrameReader(
        ConnectionReader reader,
        ChannelManager channelManager,
        PingManager pingManager,
        ConnectionWriter writer,
        Statistics? stats,
        YamuxMetrics? metrics,
        Func<Exception, Task> onFault)
    {
        _reader = reader;
        _channelManager = channelManager;
        _pingManager = pingManager;
        _writer = writer;
        _stats = stats;
        _metrics = metrics;
        _onFault = onFault;
    }

    internal void SetMetrics(YamuxMetrics? metrics) => _metrics = metrics;

    public void Start()
    {
        _readLoop = RunAsync();
    }

    public void Stop()
    {
        _reader.Stop();
    }

    public CancellationToken CancellationToken => _readToken.Token;

    public async Task WaitForCompletionAsync(TimeSpan timeout)
    {
        if (_readLoop != null)
        {
            if (_readLoop != await Task.WhenAny(_readLoop, Task.Delay(timeout, _readToken.Token)))
            {
                _readToken.Cancel();
            }
        }
    }

    private async Task RunAsync()
    {
        try
        {
            await foreach (var frameHeader in _reader.ReadFramesAsync(_readToken.Token))
            {
                _metrics?.FramesReceived.Add(1);

                switch (frameHeader.FrameType)
                {
                    case FrameType.Data:
                        await HandleDataFrame(frameHeader, _readToken.Token);
                        break;
                    case FrameType.WindowUpdate:
                        await HandleWindowUpdateFrame(frameHeader, _readToken.Token);
                        break;
                    case FrameType.Ping:
                        _pingManager.HandlePing(frameHeader, _writer, _readToken.Token);
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
            Session.SessionTracer.TraceInformation("[Err]: Session receive loop faulted");
            _metrics?.SessionErrors.Add(1);
            await _onFault(ex);
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
        var channel = await _channelManager.GetOrCreateAsync(frameHeader.StreamId, frameHeader.Flags, _writer, token);

        await ReadPayloadData(frameHeader.Length, channel, token);
    }

    private async ValueTask ReadPayloadData(uint payloadLength, SessionChannel? channel, CancellationToken cancellationToken)
    {
        int bytesToRead = (int)payloadLength;

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

                    if (read == 0)
                    {
                        throw new SessionException(SessionErrorCode.StreamClosed, "Remote connection closed mid-frame");
                    }

                    pipeWriter.Advance(read);

                    bytesToRead -= read;

                    var flushResult = await pipeWriter.FlushAsync(cancellationToken);

                    if (flushResult.IsCompleted)
                    {
                        channel.CloseWrite();

                        await pipeWriter.CompleteAsync();

                    }
                }
                catch (InvalidOperationException)
                {
                    pipeOpen = false;
                }

                _stats?.UpdateReceived(payloadLength);
                channel.Stats?.UpdateReceived(payloadLength);
                _metrics?.BytesReceived.Add(payloadLength);
            }
            else
            {
                Session.SessionTracer.TraceInformation("[WARN] yamux: channel is closed, discarding data for stream {0}", channel?.Id ?? 0);

                using var buffer = MemoryPool<byte>.Shared.Rent(4096);

                var read = await _reader.ReadFramePayloadAsync(buffer.Memory, cancellationToken);
                if (read == 0)
                {
                    throw new SessionException(SessionErrorCode.StreamClosed, "Remote connection closed mid-frame");
                }
                bytesToRead -= read;

                _stats?.UpdateReceived(payloadLength);
                _metrics?.BytesReceived.Add(payloadLength);
            }
        } while (bytesToRead > 0);
    }
}