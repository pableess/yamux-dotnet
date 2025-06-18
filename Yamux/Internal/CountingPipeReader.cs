using System;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Yamux.Internal;

/// <summary>
/// Pipe reader that exposes the number of consumed bytes and provides a callback for when bytes have been consumed.
/// This class is not thread safe.  The ConsumedBytes property should only be safely examined in the OnConsumed callback
/// Designed for use with yamux window sizes which are less uint.max
/// </summary>
internal class CountingPipeReader : PipeReader
{
    private readonly PipeReader _inner;
    private readonly Action _callback;
    private ReadResult _lastReadResult;
    private uint _bytesProcessed;
    private SequencePosition _lastExaminedPosition;

    internal CountingPipeReader(PipeReader inner, Action onConsumed)
    {
        _inner = inner;
        _callback = onConsumed;
    }

    /// <summary>
    /// Count of consumed bytes since the last Reset call
    /// </summary>
    public uint ConsumedBytes => _bytesProcessed;

    /// <summary>
    /// Resets the count of consumed bytes
    /// </summary>
    public void Reset() 
    {
        _bytesProcessed = 0; 
    }

    public override void AdvanceTo(SequencePosition consumed)
    {
        Consumed(consumed, consumed);
        _inner.AdvanceTo(consumed);
        _callback();
    }

    public override void AdvanceTo(SequencePosition consumed, SequencePosition examined)
    {
        Consumed(consumed, examined);
        _inner.AdvanceTo(consumed, examined);
        _callback();
    }

    public override void CancelPendingRead() => _inner.CancelPendingRead();

    public override void Complete(Exception? exception = null) => _inner.Complete(exception);

    public override async ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default)
    {
        return _lastReadResult = await _inner.ReadAsync(cancellationToken).ConfigureAwait(false);
    }

    public override bool TryRead(out ReadResult readResult)
    {
        bool result = _inner.TryRead(out readResult);
        _lastReadResult = readResult;
        return result;
    }

    public override ValueTask CompleteAsync(Exception? exception = null) => _inner.CompleteAsync(exception);

    [Obsolete]
    public override void OnWriterCompleted(Action<Exception?, object?> callback, object? state) => _inner.OnWriterCompleted(callback, state);

    private void Consumed(SequencePosition consumed, SequencePosition examined)
    {
        SequencePosition lastExamined = _lastExaminedPosition;
        if (lastExamined.Equals(default))
        {
            lastExamined = _lastReadResult.Buffer.Start;
        }

        // If the entirety of the buffer was examined for the first time, just use the buffer length as a perf optimization.
        // Otherwise, slice the buffer from last examined to new examined to get the number of freshly examined bytes.
        long bytesJustProcessed =
            lastExamined.Equals(_lastReadResult.Buffer.Start) && _lastReadResult.Buffer.End.Equals(examined) ? _lastReadResult.Buffer.Length :
            _lastReadResult.Buffer.Slice(lastExamined, examined).Length;

        _bytesProcessed += (uint)bytesJustProcessed;

        // Only store the examined position if it is ahead of the consumed position.
        // Otherwise we'd store a position in an array that may be recycled.
        _lastExaminedPosition = consumed.Equals(examined) ? default : examined;
    }
}
