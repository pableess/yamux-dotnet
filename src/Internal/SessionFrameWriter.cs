using System.Diagnostics;
using System.Threading.Channels;
using Yamux.Protocol;

namespace Yamux.Internal;

internal class SessionFrameWriter
{
    private readonly ITransport _peer;
    private readonly Channel<WriteItem> _writeQueue;
    private readonly Statistics? _stats;
    private YamuxMetrics? _metrics;
    private Task? _runTask;
    private readonly TimeSpan _connectionWriteTimeout;

    private readonly SemaphoreSlim _flushLock = new SemaphoreSlim(1, 1);

    internal void SetMetrics(YamuxMetrics? metrics) => _metrics = metrics;

    public SessionFrameWriter(ITransport connection, Statistics? stats, TimeSpan connectionWriteTimeout, int writeQueueDepth = 100)
    {
        _peer = connection ?? throw new ArgumentNullException(nameof(connection));
        _stats = stats;
        _metrics = null;
        _connectionWriteTimeout = connectionWriteTimeout;

        _writeQueue = Channel.CreateBounded<WriteItem>(new BoundedChannelOptions(writeQueueDepth)
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
                        using var _ = item.Frame;

                        try
                        {
                            item.Frame.Header.WriteTo(headerBuffer);

                            if (Session.SessionTracer.Switch.ShouldTrace(TraceEventType.Verbose))
                                Session.SessionTracer.TraceEvent(TraceEventType.Verbose, 0, "[Dbg] yamux: writing frame - {0}, payload size = {1}", item.Frame.Header.FrameType, item.Frame.Header.Length);

                                                        await _peer.WriteAsync(headerBuffer.AsMemory(0, FrameHeader.FrameHeaderSize), default).ConfigureAwait(false);
                            _metrics?.FramesSent.Add(1);
                            if (!item.Frame.Payload.IsEmpty)
                            {
                                await _peer.WriteAsync(item.Frame.Payload, default).ConfigureAwait(false);
                                _stats?.UpdateSent((uint)item.Frame.Payload.Length);
                                _metrics?.BytesSent.Add(item.Frame.Payload.Length);
                            }

                            item.Complete();
                        }
                        catch (OperationCanceledException cancelEx)
                        {
                            if (Session.SessionTracer.Switch.ShouldTrace(TraceEventType.Warning))
                                Session.SessionTracer.TraceEvent(TraceEventType.Warning, 0, "[Warn] yamux: write operation canceled - {0}", cancelEx.Message);
                            item.Fault(cancelEx);
                        }
                        catch (Exception ex)
                        {
                            if (Session.SessionTracer.Switch.ShouldTrace(TraceEventType.Error))
                                Session.SessionTracer.TraceEvent(TraceEventType.Error, 0, "[Err] yamux: error writing frame - {0}", ex.Message);
                            item.Fault(ex);
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
        var source = new ResettableValueTaskSource();
        var completionTask = source.GetValueTask();
        var item = new WriteItem(frame, source);

        if (Session.SessionTracer.Switch.ShouldTrace(TraceEventType.Verbose))
            Session.SessionTracer.TraceEvent(TraceEventType.Verbose, 0, "[Dbg] yamux: Enqueuing frame for write - {0}, payload size = {1}", frame.Header.FrameType, frame.Header.Length);

        await _writeQueue.Writer.WriteAsync(item, cancellationToken)
            .AsTask()
            .WaitAsync(_connectionWriteTimeout, cancellationToken)
            .ConfigureAwait(false);

        if (Session.SessionTracer.Switch.ShouldTrace(TraceEventType.Verbose))
            Session.SessionTracer.TraceEvent(TraceEventType.Verbose, 0, "[Dbg] yamux: Frame enqueued for write - {0}, payload size = {1}", frame.Header.FrameType, frame.Header.Length);

        await completionTask.ConfigureAwait(false);

        if (Session.SessionTracer.Switch.ShouldTrace(TraceEventType.Verbose))
            Session.SessionTracer.TraceEvent(TraceEventType.Verbose, 0, "[Dbg] yamux: Write completed for frame - {0}, payload size = {1}", frame.Header.FrameType, frame.Header.Length);
    }

    public void EnqueueFrame(Frame frame)
    {
        var item = new WriteItem(frame, null);

        if (_writeQueue.Writer.TryWrite(item))
        {
            return;
        }

        _ = EnqueueAsync(item);
    }

    private async Task EnqueueAsync(WriteItem item)
    {
        try
        {
            await _writeQueue.Writer.WriteAsync(item, default)
                .AsTask()
                .WaitAsync(_connectionWriteTimeout)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (Session.SessionTracer.Switch.ShouldTrace(TraceEventType.Warning))
                Session.SessionTracer.TraceEvent(TraceEventType.Warning, 0, "[Warn] yamux: EnqueueFrame write failed: {0}", ex.Message);
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

    internal int WriteQueueDepth => _writeQueue.Reader.Count;

    private readonly struct WriteItem
    {
        public readonly Frame Frame;
        public readonly ResettableValueTaskSource? Completion;

        public WriteItem(Frame frame, ResettableValueTaskSource? completion)
        {
            Frame = frame;
            Completion = completion;
        }

        public void Complete()
        {
            Completion?.SetResult();
        }

        public void Fault(Exception ex)
        {
            Completion?.SetException(ex);
        }
    }
}
