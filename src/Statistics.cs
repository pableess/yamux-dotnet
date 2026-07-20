using ByteSizeLib;

namespace Yamux
{
    /// <summary>
/// Tracks bandwidth and byte statistics for a Yamux session or channel.
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
    private bool _disposed;

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
    /// <param name="cancellationToken">A cancellation token to stop the statistics timer.</param>
    public Statistics(int intervalMilliseconds, CancellationToken cancellationToken)
    {
        SampleInterval = TimeSpan.FromMilliseconds(intervalMilliseconds);
        _timer = new Timer(SampleBandwidth, null, intervalMilliseconds, intervalMilliseconds);
        _cancel = cancellationToken;
    }

    /// <summary>
    /// Updates the statistics with the bytes sent.
    /// </summary>
    /// <param name="bytesSent">The bytes sent.</param>
    public void UpdateSent(ulong bytesSent)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
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
        ObjectDisposedException.ThrowIf(_disposed, this);
        unchecked
        {
            Interlocked.Add(ref _totalBytesReceived, bytesReceived);
        }
    }

    private void SampleBandwidth(object? state)
    {
        if (!_cancel.IsCancellationRequested)
        {
            ulong currentSent = Interlocked.Read(ref _totalBytesSent);
            ulong currentReceived = Interlocked.Read(ref _totalBytesReceived);
            ulong previousBytesSent = Interlocked.Exchange(ref _previousBytesSent, currentSent);
            ulong previousBytesReceived = Interlocked.Exchange(ref _previousBytesReceived, currentReceived);

            ulong bytesSent = currentSent - previousBytesSent;
            ulong bytesReceived = currentReceived - previousBytesReceived;

            _currentSendBandwidth = bytesSent / SampleInterval.TotalSeconds;
            _currentReceiveBandwidth = bytesReceived / SampleInterval.TotalSeconds;

            Sampled?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _timer.Dispose();
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}
}
