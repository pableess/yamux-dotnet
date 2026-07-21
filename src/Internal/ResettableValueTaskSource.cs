using System.Threading.Tasks.Sources;

namespace Yamux.Internal;

internal sealed class ResettableValueTaskSource : IValueTaskSource
{
    private ManualResetValueTaskSourceCore<int> _core = new() { RunContinuationsAsynchronously = false };

    public short Version => _core.Version;

    public void Reset() => _core.Reset();

    public void SetResult() => _core.SetResult(0);

    public void SetException(Exception ex) => _core.SetException(ex);

    public ValueTask GetValueTask() => new ValueTask(this, _core.Version);

    ValueTaskSourceStatus IValueTaskSource.GetStatus(short token) => _core.GetStatus(token);

    void IValueTaskSource.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
        => _core.OnCompleted(continuation, state, token, flags);

    void IValueTaskSource.GetResult(short token) => _core.GetResult(token);
}
