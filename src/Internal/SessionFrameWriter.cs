using System.Buffers;
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
    private readonly bool _useBatching;
    private readonly int _minBatchSize;

    private readonly SemaphoreSlim _flushLock = new SemaphoreSlim(1, 1);
    private readonly Stack<WriteSegment> _segmentPool = new();

    internal void SetMetrics(YamuxMetrics? metrics) => _metrics = metrics;

    public SessionFrameWriter(ITransport connection, Statistics? stats, TimeSpan connectionWriteTimeout,
        int writeQueueDepth = 100, bool enableBatching = false, int minBatchSize = 8192)
    {
        _peer = connection ?? throw new ArgumentNullException(nameof(connection));
        _stats = stats;
        _metrics = null;
        _connectionWriteTimeout = connectionWriteTimeout;
        _useBatching = enableBatching;
        _minBatchSize = minBatchSize;

        _writeQueue = Channel.CreateBounded<WriteItem>(new BoundedChannelOptions(writeQueueDepth)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
        });

        if (_useBatching)
        {
            for (int i = 0; i < 16; i++)
                _segmentPool.Push(new WriteSegment());
        }
    }

    public void Start()
    {
        _runTask = Task.Run(_useBatching ? BatchedLoop : DirectLoop);
    }

    private async Task DirectLoop()
    {
        byte[] headerBuffer = new byte[FrameHeader.FrameHeaderSize];
        WriteSegment seg1 = new(), seg2 = new();

        try
        {
            while (await _writeQueue.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (_writeQueue.Reader.TryRead(out var item))
                {
                    using var _ = item.Frame;

                    try
                    {
                        item.Frame.Header.WriteTo(headerBuffer);

                        if (Session.SessionTracer.Switch.ShouldTrace(TraceEventType.Verbose))
                            Session.SessionTracer.TraceEvent(TraceEventType.Verbose, 0, "[Dbg] yamux: writing frame - {0}, payload size = {1}", item.Frame.Header.FrameType, item.Frame.Header.Length);

                        if (!item.Frame.Payload.IsEmpty)
                        {
                            var header = headerBuffer.AsMemory(0, FrameHeader.FrameHeaderSize);
                            seg1.Set(header, 0);
                            seg2.Set(item.Frame.Payload, FrameHeader.FrameHeaderSize);
                            seg1.SetNext(seg2);
                            var sequence = new ReadOnlySequence<byte>(seg1, 0, seg2, item.Frame.Payload.Length);
                            await _peer.WriteAsync(sequence, default).ConfigureAwait(false);
                            _stats?.UpdateSent((uint)item.Frame.Payload.Length);
                            _metrics?.BytesSent.Add(item.Frame.Payload.Length);
                        }
                        else
                        {
                            await _peer.WriteAsync(headerBuffer.AsMemory(0, FrameHeader.FrameHeaderSize), default).ConfigureAwait(false);
                        }

                        _metrics?.FramesSent.Add(1);
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
    }

    private async Task BatchedLoop()
    {
        try
        {
            while (await _writeQueue.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                List<WriteItem> batch = new();
                WriteSegment? head = null, tail = null;
                long runningIndex = 0;
                int totalBytes = 0;

                while (_writeQueue.Reader.TryRead(out var item))
                {
                    if (item.IsFlushMarker)
                    {
                        batch.Add(item);
                        break;
                    }

                    batch.Add(item);

                    if (Session.SessionTracer.Switch.ShouldTrace(TraceEventType.Verbose))
                        Session.SessionTracer.TraceEvent(TraceEventType.Verbose, 0, "[Dbg] yamux: writing frame - {0}, payload size = {1}", item.Frame.Header.FrameType, item.Frame.Header.Length);

                    using var _ = item.Frame;
                    var hdrBuf = ArrayPool<byte>.Shared.Rent(FrameHeader.FrameHeaderSize);
                    item.Frame.Header.WriteTo(hdrBuf);
                    var hdrSeg = RentSegment();
                    hdrSeg.Set(hdrBuf.AsMemory(0, FrameHeader.FrameHeaderSize), runningIndex);
                    hdrSeg.HeaderBuffer = hdrBuf;
                    AppendSegment(ref head, ref tail, hdrSeg);
                    runningIndex += FrameHeader.FrameHeaderSize;
                    totalBytes += FrameHeader.FrameHeaderSize;

                    if (!item.Frame.Payload.IsEmpty)
                    {
                        var paySeg = RentSegment();
                        paySeg.Set(item.Frame.Payload, runningIndex);
                        AppendSegment(ref head, ref tail, paySeg);
                        runningIndex += item.Frame.Payload.Length;
                        totalBytes += item.Frame.Payload.Length;
                    }

                    if (totalBytes >= _minBatchSize)
                        break;
                }

                Exception? batchError = null;
                if (head != null)
                {
                    try
                    {
                        var sequence = new ReadOnlySequence<byte>(head, 0, tail!, tail!.Memory.Length);
                        await _peer.WriteAsync(sequence, default).ConfigureAwait(false);
                        _stats?.UpdateSent((uint)runningIndex);
                        _metrics?.BytesSent.Add(runningIndex);
                    }
                    catch (OperationCanceledException cancelEx)
                    {
                        if (Session.SessionTracer.Switch.ShouldTrace(TraceEventType.Warning))
                            Session.SessionTracer.TraceEvent(TraceEventType.Warning, 0, "[Warn] yamux: write operation canceled - {0}", cancelEx.Message);
                        batchError = cancelEx;
                    }
                    catch (Exception ex)
                    {
                        if (Session.SessionTracer.Switch.ShouldTrace(TraceEventType.Error))
                            Session.SessionTracer.TraceEvent(TraceEventType.Error, 0, "[Err] yamux: error writing frame - {0}", ex.Message);
                        batchError = ex;
                    }

                    foreach (var seg in TraverseSegments(head))
                        ReturnSegment(seg);
                }

                foreach (var item in batch)
                {
                    if (item.IsFlushMarker)
                    {
                        if (batchError != null)
                            item.Fault(batchError);
                        else
                            item.Complete();
                    }
                    else
                    {
                        if (batchError != null)
                            item.Fault(batchError);
                        else
                            item.Complete();
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
        if (!_useBatching)
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
            return;
        }

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await _writeQueue.Writer.WriteAsync(new WriteItem(tcs), cancellationToken)
            .AsTask()
            .WaitAsync(_connectionWriteTimeout, cancellationToken)
            .ConfigureAwait(false);

        await tcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
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

    private WriteSegment RentSegment()
    {
        if (_segmentPool.TryPop(out var seg))
            return seg;
        return new WriteSegment();
    }

    private void ReturnSegment(WriteSegment seg)
    {
        if (seg.HeaderBuffer != null)
        {
            ArrayPool<byte>.Shared.Return(seg.HeaderBuffer);
            seg.HeaderBuffer = null;
        }
        seg.Reset();
        _segmentPool.Push(seg);
    }

    private static void AppendSegment(ref WriteSegment? head, ref WriteSegment? tail, WriteSegment seg)
    {
        if (head == null)
            head = seg;
        else
            tail!.SetNext(seg);
        tail = seg;
    }

    private static IEnumerable<WriteSegment> TraverseSegments(WriteSegment head)
    {
        var current = head;
        while (current != null)
        {
            var next = (WriteSegment?)current.Next;
            yield return current;
            current = next;
        }
    }

    private sealed class WriteSegment : ReadOnlySequenceSegment<byte>
    {
        public byte[]? HeaderBuffer { get; set; }

        public WriteSegment Set(ReadOnlyMemory<byte> memory, long runningIndex)
        {
            Memory = memory;
            RunningIndex = runningIndex;
            Next = null;
            HeaderBuffer = null;
            return this;
        }

        public void SetNext(WriteSegment next)
        {
            Next = next;
        }

        public void Reset()
        {
            Memory = default;
            RunningIndex = 0;
            Next = null;
        }
    }

    private readonly struct WriteItem
    {
        public readonly Frame Frame;
        public readonly ResettableValueTaskSource? FrameCompletion;
        public readonly TaskCompletionSource? FlushCompletion;
        public readonly bool IsFlushMarker;

        public WriteItem(Frame frame, ResettableValueTaskSource? completion)
        {
            Frame = frame;
            FrameCompletion = completion;
            FlushCompletion = null;
            IsFlushMarker = false;
        }

        public WriteItem(TaskCompletionSource flushCompletion)
        {
            Frame = default;
            FrameCompletion = null;
            FlushCompletion = flushCompletion;
            IsFlushMarker = true;
        }

        public void Complete()
        {
            if (IsFlushMarker)
                FlushCompletion?.TrySetResult();
            else
                FrameCompletion?.SetResult();
        }

        public void Fault(Exception ex)
        {
            if (IsFlushMarker)
                FlushCompletion?.TrySetException(ex);
            else
                FrameCompletion?.SetException(ex);
        }
    }
}
