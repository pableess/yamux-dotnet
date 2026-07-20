using System.Buffers;
using System.Diagnostics;
using System.Threading.Channels;
using Yamux.Protocol;

namespace Yamux.Internal;


/// <summary>
/// Serializes frame writes to the underlying transport. This is necessary because the underlying transport may not be thread-safe for concurrent writes, and we want to ensure that frames are written in the order they are enqueued.
/// </summary>
internal class SessionFrameWriter
{
private readonly ITransport _peer;
    private readonly Channel<(Frame frame, TaskCompletionSource tcs)> _writeQueue;
    private readonly Statistics? _stats;
    private YamuxMetrics? _metrics;
    private Task? _runTask;
    private readonly TimeSpan _connectionWriteTimeout;
    private readonly ReusableValueTaskSourcePool _tcsPool = new();

    private readonly SemaphoreSlim _flushLock = new SemaphoreSlim(1, 1);

    internal void SetMetrics(YamuxMetrics? metrics) => _metrics = metrics;

public SessionFrameWriter(ITransport connection, Statistics? stats, TimeSpan connectionWriteTimeout)
    {
        _peer = connection ?? throw new ArgumentNullException(nameof(connection));
        _stats = stats;
        _metrics = null;
        _connectionWriteTimeout = connectionWriteTimeout;

        _writeQueue = Channel.CreateBounded<(Frame, TaskCompletionSource)>(new BoundedChannelOptions(100)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
        });
    }

    public void Start()
    {
        _runTask = Task.Run(async () =>
        {
            byte[] headerBuffer = new byte[FrameHeader.FrameHeaderSize];

            try
            {
                while (await _writeQueue.Reader.WaitToReadAsync().ConfigureAwait(false))
                {
                    if (_writeQueue.Reader.TryRead(out var item))
                    {
                        using var _ = item.frame;

try
                        {
                            item.frame.Header.WriteTo(headerBuffer);

                            if (Session.SessionTracer.Switch.ShouldTrace(TraceEventType.Verbose))
                                Session.SessionTracer.TraceEvent(TraceEventType.Verbose, 0, "[Dbg] yamux: writing frame - {0}, payload size = {1}", item.frame.Header.FrameType, item.frame.Header.Length);

                            await _peer.WriteAsync(headerBuffer, default).ConfigureAwait(false);
                            _metrics?.FramesSent.Add(1);
                            if (!item.frame.Payload.IsEmpty)
                            {
                                await _peer.WriteAsync(item.frame.Payload, default).ConfigureAwait(false);
                                _stats?.UpdateSent((uint)item.frame.Payload.Length);
                                _metrics?.BytesSent.Add(item.frame.Payload.Length);
                            }

                            item.tcs.TrySetResult();
                        }
                        catch (OperationCanceledException cancelEx)
                        {
                            if (Session.SessionTracer.Switch.ShouldTrace(TraceEventType.Warning))
                                Session.SessionTracer.TraceEvent(TraceEventType.Warning, 0, "[Warn] yamux: write operation canceled - {0}", cancelEx.Message);
                            item.tcs.TrySetException(cancelEx);
                        }
                        catch (Exception ex)
                        {
                            if (Session.SessionTracer.Switch.ShouldTrace(TraceEventType.Error))
                                Session.SessionTracer.TraceEvent(TraceEventType.Error, 0, "[Err] yamux: error writing frame - {0}", ex.Message);
                            item.tcs.TrySetException(ex);
                        }
                        finally
                        {
                            _tcsPool.Return(item.tcs);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                if (Session.SessionTracer.Switch.ShouldTrace(TraceEventType.Error))
                    Session.SessionTracer.TraceEvent(TraceEventType.Error, 0, "[Err] yamux: error writing frame - {0}", ex.Message);
            }
        });
    }

public async ValueTask WriteAsync(Frame frame, CancellationToken cancellationToken)
    {
        var tcs = _tcsPool.Rent();

        if (Session.SessionTracer.Switch.ShouldTrace(TraceEventType.Verbose))
            Session.SessionTracer.TraceEvent(TraceEventType.Verbose, 0, "[Dbg] yamux: Enqueuing frame for write - {0}, payload size = {1}", frame.Header.FrameType, frame.Header.Length);

        using var timeoutCts = new CancellationTokenSource(_connectionWriteTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        await _writeQueue.Writer.WriteAsync((frame, tcs), linkedCts.Token).ConfigureAwait(false);

        if (Session.SessionTracer.Switch.ShouldTrace(TraceEventType.Verbose))
            Session.SessionTracer.TraceEvent(TraceEventType.Verbose, 0, "[Dbg] yamux: Frame enqueued for write - {0}, payload size = {1}", frame.Header.FrameType, frame.Header.Length);

        await tcs.Task.ConfigureAwait(false);
        if (Session.SessionTracer.Switch.ShouldTrace(TraceEventType.Verbose))
            Session.SessionTracer.TraceEvent(TraceEventType.Verbose, 0, "[Dbg] yamux: Write completed for frame - {0}, payload size = {1}", frame.Header.FrameType, frame.Header.Length);
    }

    public void EnqueueFrame(Frame frame)
    {
        var tcs = _tcsPool.Rent();

        if (_writeQueue.Writer.TryWrite((frame, tcs)))
        {
            return;
        }

        _ = EnqueueAsync(frame, tcs);
    }

private async Task EnqueueAsync(Frame frame, TaskCompletionSource tcs)
    {
        try
        {
            using var timeoutCts = new CancellationTokenSource(_connectionWriteTimeout);
            await _writeQueue.Writer.WriteAsync((frame, tcs), timeoutCts.Token).ConfigureAwait(false);
            await tcs.Task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (Session.SessionTracer.Switch.ShouldTrace(TraceEventType.Warning))
                Session.SessionTracer.TraceEvent(TraceEventType.Warning, 0, "[Warn] yamux: EnqueueFrame write failed: {0}", ex.Message);
            tcs.TrySetException(ex);
        }
        finally
        {
            _tcsPool.Return(tcs);
        }
    }

    public async ValueTask FlushAsync(CancellationToken cancellationToken = default)
    {
        await _flushLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _peer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _flushLock.Release();
        }
    }

    public async Task StopAsync()
    {
        if (_runTask != null && _runTask.IsCompleted == false)
        {
            _writeQueue.Writer.TryComplete();

            await _runTask.ConfigureAwait(false);
        }
    }
}