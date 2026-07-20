namespace Yamux;

/// <summary>
/// Configuration options for a Yamux <see cref="Session"/>.
/// </summary>
public class SessionOptions
{
    /// <summary>
    /// The maximum number of channels that can be queued for acceptance before backpressure is applied.
    /// </summary>
    public int AcceptBacklog { get; set; } = 256;

    /// <summary>
    /// Whether to enable keep-alive pings to detect dead connections.
    /// </summary>
    public bool EnableKeepAlive { get; set; } = true;

    /// <summary>
    /// The interval between keep-alive pings.
    /// </summary>
    public TimeSpan KeepAliveInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The maximum time to wait for a stream to fully close after sending FIN.
    /// </summary>
    public TimeSpan StreamCloseTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The timeout for sending data on a channel before declaring the send failed.
    /// </summary>
    public TimeSpan StreamSendTimeout { get; set; } = TimeSpan.FromSeconds(75);

    /// <summary>
    /// The maximum time to wait for a stream to be acknowledged after opening with SYN.
    /// After this timeout, the session is closed. A zero value disables the timeout.
    /// Matches the Go yamux <c>StreamOpenTimeout</c>.
    /// </summary>
    public TimeSpan StreamOpenTimeout { get; set; } = TimeSpan.FromSeconds(75);

    /// <summary>
    /// The maximum time to wait for a write to the underlying connection to complete.
    /// Acts as a safety valve after which the connection is suspected to be dead.
    /// Matches the Go yamux <c>ConnectionWriteTimeout</c>.
    /// </summary>
    public TimeSpan ConnectionWriteTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The maximum time to wait for open channels to drain during session shutdown.
    /// After this timeout, channels are forcefully closed.
    /// </summary>
    public TimeSpan SessionCloseTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The timeout for reading data from the underlying transport.
    /// </summary>
    public TimeSpan ReadTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Default channel options applied to newly accepted channels.
    /// </summary>
    public SessionChannelOptions DefaultChannelOptions { get; set; } = new SessionChannelOptions();

    /// <summary>
    /// Maximum number of concurrent channels (streams). Exceeding this will cause new channels to be rejected with RST.
    /// </summary>
    public int MaxChannels { get; set; } = 1024;

    /// <summary>
    /// The maximum allowed payload size for an incoming data frame. Frames exceeding this will trigger a protocol error.
    /// </summary>
    public uint MaxIncomingFrameSize { get; set; } = 16 * 1024 * 1024;

    /// <summary>
    /// Enables bandwidth and byte statistics tracking.
    /// </summary>
    public bool EnableStatistics { get; set; }

    /// <summary>
    /// How often to sample statistics, in milliseconds.
    /// </summary>
    public int StatisticsSampleInterval { get; set; } = 1000;

    /// <summary>
    /// Enables OpenTelemetry-compatible metrics via <see cref="System.Diagnostics.Metrics"/>.
    /// </summary>
    public bool EnableMetrics { get; set; } = true;
}
