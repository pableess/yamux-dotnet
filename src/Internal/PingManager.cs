using System.Collections.Concurrent;
using System.Diagnostics;
using Yamux.Protocol;

namespace Yamux.Internal;

internal class PingManager
{
    private readonly ConcurrentDictionary<uint, TaskCompletionSource<long>> _pings = new();
    private uint _nextId;

    public async ValueTask<TimeSpan> PingAsync(SessionFrameWriter writer, TimeSpan timeout, CancellationToken cancellation)
    {
        var opaqueValue = Interlocked.Increment(ref _nextId);

        TaskCompletionSource<long> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        using var registration = cancellation.Register(static state =>
        {
            var (manager, id, completion) = ((PingManager, uint, TaskCompletionSource<long>))state!;
            if (completion.TrySetCanceled())
            {
                manager._pings.TryRemove(id, out _);
            }
        }, (this, opaqueValue, tcs));

        var start = Stopwatch.GetTimestamp();

        _pings.TryAdd(opaqueValue, tcs);

        try
        {
            await writer.WriteAsync(Frame.CreatePingRequestFrame(opaqueValue), cancellation).ConfigureAwait(false);
        }
        catch
        {
            _pings.TryRemove(opaqueValue, out _);
            throw;
        }

        try
        {
            // Match Go yamux: a ping that is not answered within the connection
            // write timeout fails instead of blocking forever.
            var stop = await tcs.Task.WaitAsync(timeout, cancellation).ConfigureAwait(false);
            var elapsedTicks = stop - start;
            return TimeSpan.FromTicks(elapsedTicks * TimeSpan.TicksPerSecond / Stopwatch.Frequency);
        }
        catch
        {
            _pings.TryRemove(opaqueValue, out _);
            throw;
        }
    }

    public async ValueTask HandlePingAsync(FrameHeader frameHeader, SessionFrameWriter writer, CancellationToken token)
    {
        if (frameHeader.Flags.HasFlag(Flags.ACK))
        {
            if (_pings.TryRemove(frameHeader.Length, out var tcs))
            {
                tcs.TrySetResult(Stopwatch.GetTimestamp());
            }
        }
        else
        {
            await writer.WriteAsync(Frame.CreatePingResponseFrame(frameHeader), token).ConfigureAwait(false);
        }
    }

    public void FailAllPings(Exception? exception = null)
    {
        foreach (var kvp in _pings)
        {
            if (exception != null)
            {
                kvp.Value.TrySetException(exception);
            }
            else
            {
                kvp.Value.TrySetCanceled();
            }
        }
        _pings.Clear();
    }
}