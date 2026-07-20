using System.Collections.Concurrent;

namespace Yamux.Internal;

internal sealed class ReusableValueTaskSourcePool
{
    private readonly ConcurrentQueue<TaskCompletionSource> _queue = new();
    private const int MaxPoolSize = 1024;
    private int _count;

    public TaskCompletionSource Rent()
    {
        if (_queue.TryDequeue(out var item))
        {
            Interlocked.Decrement(ref _count);
            return item;
        }

        return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public void Return(TaskCompletionSource item)
    {
        if (item.Task.IsCompleted)
        {
            if (Interlocked.Increment(ref _count) <= MaxPoolSize)
            {
                item.TrySetResult();
                _queue.Enqueue(item);
            }
            else
            {
                Interlocked.Decrement(ref _count);
            }
        }
    }
}