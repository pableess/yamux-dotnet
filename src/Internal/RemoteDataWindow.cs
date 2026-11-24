using Yamux.Protocol;

namespace Yamux.Internal;

/// <summary>
/// Tracks the send window granted by the remote peer.
/// Allows waiting asynchronously until window credit becomes available.
/// </summary>
internal sealed class RemoteDataWindow(uint defaultSize = Constants.Initial_Window_Size) : IDisposable
{
    private uint _available = defaultSize;
    private readonly Lock _dataLock = new();
    private readonly Queue<Waiter> _waiters = new();
    private volatile bool _disposedValue;

    public void Extend(uint length)
    {
        if (length == 0)
        {
            return;
        }

        lock (_dataLock)
        {
            if (_disposedValue)
            {
                return;
            }

            _available = (uint)Math.Min((ulong)_available + length, uint.MaxValue);
            SignalWaiters();
        }
    }

    public uint TryConsume(uint length)
    {
        lock (_dataLock)
        {
            return TryConsumeLocked(length);
        }
    }

    private uint TryConsumeLocked(uint length)
    {
        if (_available == 0 || length == 0)
        {
            return 0;
        }

        if (_available >= length)
        {
            _available -= length;
            return length;
        }

        var consumed = _available;
        _available = 0;
        return consumed;
    }

    /// <summary>
    /// Spends as many bytes up to the requested length from the data window that it can.
    /// If no bytes are currently available it blocks until some become available but does not
    /// wait for the full requested length.
    /// </summary>
    /// <returns>The number of bytes that were acquired.</returns>
    /// <exception cref="TimeoutException">Thrown when the timeout expires before any bytes are available.</exception>
    public uint WaitConsume(uint length, TimeSpan? timeout)
    {
        ThrowIfDisposed();

        if (length == 0)
        {
            return 0;
        }

        SyncWaiter? waiter;
        using (_dataLock.EnterScope())
        {
            var consumed = TryConsumeLocked(length);
            if (consumed > 0)
            {
                return consumed;
            }

            waiter = new SyncWaiter(length);
            _waiters.Enqueue(waiter);
        }

        var granted = waiter.Wait(timeout ?? Timeout.InfiniteTimeSpan);

        ThrowIfDisposed();

        return granted;
    }

    /// <summary>
    /// Spends as many bytes up to the requested length from the data window that it can.
    /// If no bytes are currently available it waits asynchronously until some become available
    /// but does not wait for the full requested length.
    /// </summary>
    /// <returns>The number of bytes that were acquired.</returns>
    /// <exception cref="TimeoutException">Thrown when the timeout expires before any bytes are available.</exception>
    public ValueTask<uint> WaitConsumeAsync(uint length, TimeSpan? timeout = null, CancellationToken? cancellationToken = null)
    {
        ThrowIfDisposed();

        if (length == 0)
        {
            return ValueTask.FromResult(0u);
        }

        AsyncWaiter? waiter;
        using (_dataLock.EnterScope())
        {
            var consumed = TryConsumeLocked(length);
            if (consumed > 0)
            {
                return ValueTask.FromResult(consumed);
            }

            waiter = new AsyncWaiter(length, timeout, cancellationToken);
            _waiters.Enqueue(waiter);
        }

        return waiter.Task;
    }

    private void SignalWaiters()
    {
        while (_available > 0 && _waiters.TryDequeue(out var waiter))
        {
            // A waiter that was already canceled, timed out, or disposed does not
            // consume window credit. It stays eligible for the next extension only
            // if it was granted, so skip it and continue with the next one.
            if (waiter.TryGrant(_available, out var granted))
            {
                _available -= granted;
            }
        }
    }

    public uint Available
    {
        get
        {
            ThrowIfDisposed();

            lock (_dataLock)
            {
                return _available;
            }
        }
    }

    private abstract class Waiter(uint requested)
    {
        private const int Pending = 0;
        private const int Granted = 1;
        private const int Abandoned = 2;

        private int _state;
        private bool _disposed;

        public uint Requested { get; } = requested;

        protected bool Disposed => _disposed;

        /// <summary>
        /// Tries to complete the waiter with up to <paramref name="available"/> bytes.
        /// Returns false when the waiter was already abandoned (canceled/timed out).
        /// </summary>
        public bool TryGrant(uint available, out uint granted)
        {
            granted = Math.Min(available, Requested);

            if (Interlocked.CompareExchange(ref _state, Granted, Pending) != Pending)
            {
                granted = 0;
                return false;
            }

            Grant(granted);
            return true;
        }

        /// <summary>
        /// Marks the waiter as abandoned. Returns true if this call won the race
        /// against a concurrent grant or completion.
        /// </summary>
        public bool TryAbandon()
        {
            return Interlocked.CompareExchange(ref _state, Abandoned, Pending) == Pending;
        }

        public abstract void SignalDisposed();

        protected void MarkDisposed() => _disposed = true;

        protected abstract void Grant(uint granted);
    }

    private sealed class SyncWaiter(uint requested) : Waiter(requested)
    {
        private readonly ManualResetEventSlim _signal = new(false);
        private uint _granted;

        protected override void Grant(uint granted)
        {
            _granted = granted;
            _signal.Set();
        }

        public override void SignalDisposed()
        {
            MarkDisposed();
            _signal.Set();
        }

        public uint Wait(TimeSpan timeout)
        {
            if (!_signal.Wait(timeout))
            {
                // If a grant raced with the timeout, prefer the grant.
                if (!TryAbandon())
                {
                    _signal.Wait();
                    return _granted;
                }

                throw new TimeoutException("Timed out waiting for send window");
            }

            if (Disposed)
            {
                throw new ObjectDisposedException(nameof(RemoteDataWindow));
            }

            return _granted;
        }
    }

    private sealed class AsyncWaiter : Waiter
    {
        private readonly TaskCompletionSource<uint> _finished;

        public AsyncWaiter(uint requested, TimeSpan? timeout = null, CancellationToken? cancel = null)
            : base(requested)
        {
            // Run continuations asynchronously to avoid completing a caller while holding _dataLock.
            _finished = new TaskCompletionSource<uint>(TaskCreationOptions.RunContinuationsAsynchronously);

            CancellationTokenRegistration cancellationRegistration = default;
            if (cancel is { CanBeCanceled: true } token)
            {
                cancellationRegistration = token.Register(static state =>
                {
                    var (waiter, cancellationToken) = ((AsyncWaiter, CancellationToken))state!;
                    if (waiter.TryAbandon())
                    {
                        waiter._finished.TrySetCanceled(cancellationToken);
                    }
                }, (this, token));
            }

            CancellationTokenRegistration timeoutRegistration = default;
            CancellationTokenSource? timeoutSource = null;
            if (timeout is { } value && value > TimeSpan.Zero && value != Timeout.InfiniteTimeSpan)
            {
                timeoutSource = new CancellationTokenSource(value);
                timeoutRegistration = timeoutSource.Token.Register(static state =>
                {
                    var waiter = (AsyncWaiter)state!;
                    if (waiter.TryAbandon())
                    {
                        waiter._finished.TrySetException(new TimeoutException("Timed out waiting for send window"));
                    }
                }, this);
            }

            if (cancellationRegistration != default || timeoutRegistration != default)
            {
                _finished.Task.ContinueWith(static (_, state) =>
                {
                    var (cancellation, timeoutReg, source) = ((CancellationTokenRegistration, CancellationTokenRegistration, CancellationTokenSource?))state!;
                    cancellation.Dispose();
                    timeoutReg.Dispose();
                    source?.Dispose();
                }, (cancellationRegistration, timeoutRegistration, timeoutSource), CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }

        protected override void Grant(uint granted)
        {
            _finished.TrySetResult(granted);
        }

        public override void SignalDisposed()
        {
            MarkDisposed();
            if (TryAbandon())
            {
                _finished.TrySetException(new ObjectDisposedException(nameof(RemoteDataWindow)));
            }
        }

        public ValueTask<uint> Task => new(_finished.Task);
    }

    public void Dispose()
    {
        if (_disposedValue)
        {
            return;
        }

        lock (_dataLock)
        {
            if (_disposedValue)
            {
                return;
            }

            _disposedValue = true;
            while (_waiters.TryDequeue(out var current))
            {
                current.SignalDisposed();
            }
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposedValue)
        {
            throw new ObjectDisposedException(nameof(RemoteDataWindow));
        }
    }
}
