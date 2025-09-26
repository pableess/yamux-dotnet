using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Yamux.Internal;

/// <summary>
/// helper class for tracking the data window of the remote peer
/// give ability to wait both synchronously or asynchronously until send window becomes availible
/// 
/// </summary>
/// <param name="defaultSize"></param>
internal class RemoteDataWindow(uint defaultSize = 256 * 1024) : IDisposable
{
    private uint _available = defaultSize;
    private Lock _dataLock = new Lock();
    private Queue<Waiter> _waiters = new Queue<Waiter>();
    private bool _disposedValue;

    public void Extend(uint length)
    {
        // no op
        if (length is 0)
        {
            return;
        }

        // lock the window
        lock (_dataLock)
        {
            _available += length;

            SignalWaiters();
        }
    }

    public uint TryConsume(uint length)
    {
        lock (_dataLock)
        {
            if (_available == 0)
            {
                return 0;
            }
            else if (_available >= length)
            {
                _available -= length;
                return length;
            }
            else
            {
                var consumed = _available;
                _available = 0;
                return consumed;
            }
        }
    }

    /// <summary>
    /// Spends as many bytes up to the requested length from the data window that it can/
    /// If no bytes are currently availble it will block until some become availible but will not wait for all the requestd length.
    /// </summary>
    /// <param name="length"></param>
    /// <param name="timeout"></param>
    /// <returns>The number of bytes that were acquired</returns>
    /// <exception cref="TimeoutException">Throws timeout exception if timeout occurs before any bytes are available from the window</exception>
    public uint WaitConsume(uint length, TimeSpan? timeout)
    {
        ThrowIfDisposed();
        SyncWaiter? waiter;
        uint consumed = 0;
        using (_dataLock.EnterScope())
        {
            // try to consume without waiting for more window
            if ((consumed = TryConsume(length)) > 0)
            {
                return consumed;
            }

            waiter = new SyncWaiter(length);
            _waiters.Enqueue(waiter);
        }
        consumed = waiter.Wait(timeout);

        ThrowIfDisposed();

        return consumed;
    }


    /// <summary>
    /// Spends as many bytes up to the requested length from the data window that it can
    /// If no bytes are currently availble it will asynchronously wait util some become availible but will not wait for all the requestd length.
    /// </summary>
    /// <param name="length"></param>
    /// <param name="timeout"></param>
    /// <returns>The number of bytes that were acquired</returns>
    /// <exception cref="TimeoutException">Throws timeout exception if timeout occurs before any bytes are available from the window</exception>
    public ValueTask<uint> WaitConsumeAsync(uint length, TimeSpan? timeout = null, CancellationToken? cancel = null)
    {
        ThrowIfDisposed();

        AsyncWaiter? waiter;
        using (_dataLock.EnterScope())
        {
            uint consumed = 0;
            // try to consume without waiting for more window
            if ((consumed = TryConsume(length)) > 0)
            {
                return ValueTask.FromResult(consumed);
            }
            waiter = new AsyncWaiter(length, timeout, cancel);
            _waiters.Enqueue(waiter);
        }

        return waiter.Task;
    }

    private void SignalWaiters()
    {
        do
        {
            if (_waiters.TryDequeue(out var waiter))
            {
                waiter.Signal(_available);

                if (waiter.Requested < _available)
                {
                    // update the availble 
                    _available -= waiter.Requested;
                }
                else
                {
                    _available = 0;
                    return;
                }
            }
            else
            {
                return;
            }
        }
        while (_available > 0);
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

    private abstract class Waiter
    {
        public abstract void Signal(uint available);

        public abstract void SignalDisposed();

        public abstract uint Requested { get; }
    }

    private class SyncWaiter(uint requested) : Waiter
    {
        private readonly object _monitor = new object();
        private uint _signaled;
        public override uint Requested => requested;


        public override void Signal(uint available)
        {
            _signaled = available;
            lock (_monitor)
            {
                Monitor.Pulse(_monitor);
            }
        }

        public override void SignalDisposed()
        {
            lock (_monitor)
            {
                Monitor.Pulse(_monitor);
            }
        }

        public uint Wait(TimeSpan? timeout = null)
        {
            lock (_monitor)
            {
                var acquired = timeout == null ? Monitor.Wait(_monitor) : Monitor.Wait(_monitor, (int)timeout.Value.TotalMilliseconds);
                if (!acquired)
                {
                    throw new TimeoutException("Timeout error");
                }
            }

            return _signaled > Requested ? Requested : _signaled;
        }
    }


    /// <summary>
    /// Helper class for ensuring single access 
    /// </summary>
    private class AsyncWaiter : Waiter
    {
        private readonly TaskCompletionSource<uint> _finished;
        private readonly uint _requested;

        public AsyncWaiter(uint requested, TimeSpan? timeout = null, CancellationToken? cancel = null)
        {
            // to prevent a deadlock we need to run the continuations asynchronously
            _finished = new TaskCompletionSource<uint>(TaskCreationOptions.RunContinuationsAsynchronously);
            _requested = requested;

            CancellationTokenRegistration? cancellationTokenRegistration = null;
            CancellationTokenSource? timeOutTcs = null;
            CancellationTokenRegistration? timeoutReg = null;
            if (cancel != null)
            {
                cancellationTokenRegistration = cancel.Value.Register(() =>
                {
                    _finished.TrySetCanceled();
                });
            }

            if (timeout != null)
            {
                timeOutTcs = new CancellationTokenSource((int)timeout.Value.TotalMilliseconds);
                timeOutTcs.Token.Register(() =>
                {
                    _finished.TrySetException(new TimeoutException("Timeout error"));
                });
            }

            _finished.Task.ContinueWith(t =>
            {
                cancellationTokenRegistration?.Dispose();
                timeoutReg?.Dispose();
                timeOutTcs?.Dispose();
            });
        }

        public override uint Requested => _requested;

        public override void Signal(uint available)
        {
            var consumed = available > Requested ? Requested : available;

            _finished.SetResult(consumed);
        }

        public override void SignalDisposed() 
        {
            _finished.TrySetException(new ObjectDisposedException(nameof(RemoteDataWindow)));
        }

        public ValueTask<uint> Task => new ValueTask<uint>(_finished.Task);
    }

    public void Dispose()
    {
        if (_disposedValue) return;

        lock (_dataLock)
        {
            while (_waiters.TryDequeue(out var current)) 
            {
                current.SignalDisposed();
            }
            _disposedValue = true;
        }
    }

    private void ThrowIfDisposed() 
    {
        if (_disposedValue) throw new ObjectDisposedException(nameof(RemoteDataWindow));
    }
}
