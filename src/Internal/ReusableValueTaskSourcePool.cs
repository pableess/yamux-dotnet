using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;

namespace Yamux.Internal
{

    internal class ReusableValueTaskSource : IValueTaskSource
    {
        private ManualResetValueTaskSourceCore<bool> _core;

        public ReusableValueTaskSource()
        {
            Reset();
        }

        public void Reset()
        {
            _core = new ManualResetValueTaskSourceCore<bool>
            {
                RunContinuationsAsynchronously = true
            };
        }

        public void SetResult() => _core.SetResult(true);
        public void SetException(Exception error) => _core.SetException(error);
        public ValueTaskSourceStatus GetStatus(short token) => _core.GetStatus(token);
        public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
            => _core.OnCompleted(continuation, state, token, flags);

        public void GetResult(short token) => _core.GetResult(token);

        public short Version => _core.Version;
    }

    internal sealed class ReusableValueTaskSourcePool
    {
        private readonly ConcurrentQueue<ReusableValueTaskSource> _queue = new();
        private const int MaxPoolSize = 1024;
        private int _count;

        public ReusableValueTaskSource Rent()
        {
            if (_queue.TryDequeue(out var item))
            {
                Interlocked.Decrement(ref _count);
                item.Reset();
                return item;
            }

            return new ReusableValueTaskSource();
        }

        public void Return(ReusableValueTaskSource item)
        {
            if (Interlocked.Increment(ref _count) <= MaxPoolSize)
            {
                _queue.Enqueue(item);
            }
            Interlocked.Decrement(ref _count);
        }
    }
}
