using System.Diagnostics.Metrics;

namespace Yamux;

internal sealed class YamuxMetrics : IDisposable
{
    private readonly Meter _meter;
    private readonly KeyValuePair<string, object?>[] _tags;

    private readonly Counter<long> _channelsOpened;
    private readonly Counter<long> _channelsClosed;
    private readonly Counter<long> _bytesSent;
    private readonly Counter<long> _bytesReceived;
    private readonly Counter<long> _framesSent;
    private readonly Counter<long> _framesReceived;
    private readonly Counter<long> _sessionErrors;
    private readonly Histogram<double> _rttMilliseconds;

    private bool _disposed;

    public YamuxMetrics(string sessionId, bool isClient, Func<int> getActiveChannels, Func<int> getWriteQueueDepth)
    {
        _tags = new[]
        {
            new KeyValuePair<string, object?>("session_id", sessionId),
            new KeyValuePair<string, object?>("is_client", isClient),
        };

        // Each session owns its meter. Disposing the meter unregisters the
        // instruments, including the observable gauges, so disposed sessions are
        // not retained by the metrics infrastructure.
        _meter = new Meter("Yamux", "1.0.0");

        _channelsOpened = _meter.CreateCounter<long>("yamux.channels.opened", "channels", "Total channels opened");
        _channelsClosed = _meter.CreateCounter<long>("yamux.channels.closed", "channels", "Total channels closed");
        _bytesSent = _meter.CreateCounter<long>("yamux.bytes.sent", "bytes", "Total bytes sent");
        _bytesReceived = _meter.CreateCounter<long>("yamux.bytes.received", "bytes", "Total bytes received");
        _framesSent = _meter.CreateCounter<long>("yamux.frames.sent", "frames", "Total frames sent");
        _framesReceived = _meter.CreateCounter<long>("yamux.frames.received", "frames", "Total frames received");
        _sessionErrors = _meter.CreateCounter<long>("yamux.errors", "errors", "Total session errors");
        _rttMilliseconds = _meter.CreateHistogram<double>("yamux.rtt.ms", "ms", "Round-trip time");

        _meter.CreateObservableGauge("yamux.channels.active", () =>
            new Measurement<int>(getActiveChannels(), _tags), "channels", "Active channels");

        _meter.CreateObservableGauge("yamux.write_queue.depth", () =>
            new Measurement<int>(getWriteQueueDepth(), _tags), "items", "Write queue depth");
    }

    public void ChannelOpened() => _channelsOpened.Add(1, _tags);

    public void ChannelClosed() => _channelsClosed.Add(1, _tags);

    public void BytesSent(long bytes) => _bytesSent.Add(bytes, _tags);

    public void BytesReceived(long bytes) => _bytesReceived.Add(bytes, _tags);

    public void FrameSent() => _framesSent.Add(1, _tags);

    public void FrameReceived() => _framesReceived.Add(1, _tags);

    public void SessionError() => _sessionErrors.Add(1, _tags);

    public void RecordRtt(TimeSpan rtt) => _rttMilliseconds.Record(rtt.TotalMilliseconds, _tags);

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _meter.Dispose();
        }
    }
}
