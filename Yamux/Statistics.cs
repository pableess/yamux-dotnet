using ByteSizeLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace Yamux
{
    /// <summary>
    /// Class to track the current send and receive bandwidth and the total bytes sent and received.
    /// </summary>
    public class Statistics : IDisposable
    {
        private ulong _totalBytesSent;
        private ulong _totalBytesReceived;

        private double _currentSendBandwidth;
        private double _currentReceiveBandwidth;
        private ulong _previousBytesSent;
        private ulong _previousBytesReceived;
        private CancellationToken _cancel;
        private Timer _timer;
        private bool disposedValue;

        /// <summary>
        /// Gets the total bytes sent.
        /// </summary>
        public ulong TotalBytesSent => Interlocked.Read(ref _totalBytesSent);

        /// <summary>
        /// Gets the total bytes received.
        /// </summary>
        public ulong TotalBytesReceived => Interlocked.Read(ref _totalBytesReceived);

        /// <summary>
        /// Gets the current send bandwidth in bytes per second.
        /// </summary>
        public ByteSize SendRate => ByteSize.FromBytes(_currentSendBandwidth);

        /// <summary>
        /// Gets the current receive bandwidth in bytes per second.
        /// </summary>
        public ByteSize ReceiveRate => ByteSize.FromBytes(_currentReceiveBandwidth);

        /// <summary>
        /// Gets the sample interval
        /// </summary>
        public TimeSpan SampleInterval { get; }

        public event EventHandler? Sampled;

        /// <summary>
        /// Initializes a new instance of the <see cref="Statistics"/> class and starts the timer.
        /// </summary>
        /// <param name="intervalMilliseconds">The interval in milliseconds for sampling the bandwidth.</param>
        public Statistics(int intervalMilliseconds, CancellationToken cancel)
        {
            SampleInterval = TimeSpan.FromMilliseconds(intervalMilliseconds);
            _timer = new Timer(SampleBandwidth, null, intervalMilliseconds, intervalMilliseconds);
            _cancel = cancel;
        }

        /// <summary>
        /// Updates the statistics with the bytes sent.
        /// </summary>
        /// <param name="bytesSent">The bytes sent.</param>
        public void UpdateSent(ulong bytesSent)
        {
            this.ThrowIfDisposed();
            unchecked
            {
                Interlocked.Add(ref _totalBytesSent, bytesSent);
            }
        }

        /// <summary>
        /// Updates the statistics with the bytes received.
        /// </summary>
        /// <param name="bytesReceived">The bytes received.</param>
        public void UpdateReceived(ulong bytesReceived)
        {
            this.ThrowIfDisposed();
            unchecked
            {
                Interlocked.Add(ref _totalBytesReceived, bytesReceived);
            }
        }

        private void SampleBandwidth(object? state)
        {
            if (!_cancel.IsCancellationRequested)
            {
                ulong previousBytesSent = Interlocked.Exchange(ref _previousBytesSent, _totalBytesSent);
                ulong previousBytesReceived = Interlocked.Exchange(ref _previousBytesReceived, _totalBytesReceived);

                ulong bytesSent = _totalBytesSent - previousBytesSent;
                ulong bytesReceived = _totalBytesReceived - previousBytesReceived;

                _currentSendBandwidth = bytesSent / (SampleInterval.TotalSeconds);
                _currentReceiveBandwidth = bytesReceived / (SampleInterval.TotalSeconds);

                Sampled?.Invoke(this, EventArgs.Empty);
            }
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    // TODO: dispose managed state (managed objects)
                    _timer.Dispose();
                }

                // TODO: free unmanaged resources (unmanaged objects) and override finalizer
                // TODO: set large fields to null
                disposedValue = true;
            }
        }

        // // TODO: override finalizer only if 'Dispose(bool disposing)' has code to free unmanaged resources
        // ~Statistics()
        // {
        //     // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
        //     Dispose(disposing: false);
        // }

        public void Dispose()
        {
            // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ThrowIfDisposed() 
        {
            if (disposedValue)
                throw new ObjectDisposedException(nameof(Statistics));
        }
    }
}
