using System.Diagnostics.Metrics;

namespace Yamux;

internal sealed class YamuxMetrics : IDisposable
{
    public static readonly Meter Meter = new("Yamux", "1.0.0");

    private readonly KeyValuePair<string, object?>[] _tags;
    private bool _disposed;

    public Counter<long> ChannelsOpened { get; }
    public Counter<long> ChannelsClosed { get; }
    public Counter<long> BytesSent { get; }
    public Counter<long> BytesReceived { get; }
    public Counter<long> FramesSent { get; }
    public Counter<long> FramesReceived { get; }
    public Counter<long> SessionErrors { get; }
    public Histogram<double> RttMilliseconds { get; }

    public YamuxMetrics(string sessionId, bool isClient, Func<int> getActiveChannels, Func<int> getWriteQueueDepth)
    {
        _tags = new[]
        {
            new KeyValuePair<string, object?>("session_id", sessionId),
            new KeyValuePair<string, object?>("is_client", isClient),
        };

        ChannelsOpened = Meter.CreateCounter<long>("yamux.channels.opened", "channels", "Total channels opened");
        ChannelsClosed = Meter.CreateCounter<long>("yamux.channels.closed", "channels", "Total channels closed");
        BytesSent = Meter.CreateCounter<long>("yamux.bytes.sent", "bytes", "Total bytes sent");
        BytesReceived = Meter.CreateCounter<long>("yamux.bytes.received", "bytes", "Total bytes received");
        FramesSent = Meter.CreateCounter<long>("yamux.frames.sent", "frames", "Total frames sent");
        FramesReceived = Meter.CreateCounter<long>("yamux.frames.received", "frames", "Total frames received");
        SessionErrors = Meter.CreateCounter<long>("yamux.errors", "errors", "Total session errors");
        RttMilliseconds = Meter.CreateHistogram<double>("yamux.rtt.ms", "ms", "Round-trip time");

        _ = Meter.CreateObservableGauge("yamux.channels.active", () =>
            new Measurement<int>(getActiveChannels(), _tags), "channels", "Active channels");

        _ = Meter.CreateObservableGauge("yamux.write_queue.depth", () =>
            new Measurement<int>(getWriteQueueDepth(), _tags), "items", "Write queue depth");
    }

    public void RecordRtt(TimeSpan rtt)
    {
        RttMilliseconds.Record(rtt.TotalMilliseconds, _tags);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
        }
    }
}