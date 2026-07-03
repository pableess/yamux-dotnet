namespace Yamux;

public class SessionOptions
{
    public int AcceptBacklog { get; set; } = 256;

    public bool EnableKeepAlive { get; set; } = true;

    public TimeSpan KeepAliveInterval { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan StreamCloseTimeout { get; set; } = TimeSpan.FromMinutes(5);

    public TimeSpan StreamSendTimeout { get; set; } = TimeSpan.FromSeconds(75);

    public SessionChannelOptions DefaultChannelOptions { get; set; } = new SessionChannelOptions();

    /// <summary>
    /// Maximum number of concurrent channels (streams). Exceeding this will cause new channels to be rejected with RST.
    /// </summary>
    public int MaxChannels { get; set; } = 1024;

    /// <summary>
    /// Enables statistics
    /// </summary>
    public bool EnableStatistics { get; set; }

    /// <summary>
    /// How often to sample the statistics
    /// </summary>
    public int StatisticsSampleInterval { get; set; } = 1000;

    /// <summary>
    /// Enables OpenTelemetry-compatible metrics via System.Diagnostics.Metrics
    /// </summary>
    public bool EnableMetrics { get; set; } = true;
}
