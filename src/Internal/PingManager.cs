using System.Collections.Concurrent;
using System.Diagnostics;
using Yamux.Protocol;

namespace Yamux.Internal;

internal class PingManager
{
    private readonly ConcurrentDictionary<uint, TaskCompletionSource<long>> _pings = new();
    private uint _nextId;

    public async ValueTask<TimeSpan> PingAsync(ConnectionWriter writer, CancellationToken cancellation)
    {
        var opaqueValue = Interlocked.Increment(ref _nextId);

        TaskCompletionSource<long> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        using var registration = cancellation.Register(() =>
        {
            tcs.TrySetCanceled();
            _pings.TryRemove(opaqueValue, out _);
        });

        var start = Stopwatch.GetTimestamp();

        _pings.TryAdd(opaqueValue, tcs);

        try
        {
            await writer.WriteAsync(Frame.CreatePingRequestFrame(opaqueValue), cancellation);
        }
        catch
        {
            _pings.TryRemove(opaqueValue, out _);
            throw;
        }

        var stop = await tcs.Task;
        return TimeSpan.FromTicks(stop - start);
    }

    public void HandlePing(FrameHeader frameHeader, ConnectionWriter writer, CancellationToken token)
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
            _ = writer.WriteAsync(Frame.CreatePingResponseFrame(frameHeader), token);
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