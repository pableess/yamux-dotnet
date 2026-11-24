namespace Yamux;

/// <summary>
/// Configuration options for a Yamux <see cref="Session"/>.
/// </summary>
public class SessionOptions
{
    /// <summary>
    /// The maximum number of incoming channels that can be queued for acceptance.
    /// When the backlog or <see cref="MaxChannels"/> limit is exceeded, new streams are reset.
    /// </summary>
    public int AcceptBacklog { get; set; } = 256;

    /// <summary>
    /// The maximum number of outgoing frames to buffer in the write queue before applying backpressure.
    /// </summary>
    public int WriteQueueDepth { get; set; } = 100;

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
    /// The maximum time to wait for a stream to be acknowledged after opening with SYN
    /// when <c>waitForAcknowledgement</c> is true. After this timeout, the session is closed.
    /// A zero value disables the timeout. Matches the Go yamux <c>StreamOpenTimeout</c>.
    /// </summary>
    public TimeSpan StreamOpenTimeout { get; set; } = TimeSpan.FromSeconds(75);

    /// <summary>
    /// The maximum time to wait to enqueue a frame when the write queue is full,
    /// and the timeout for ping responses. Acts as a safety valve after which the
    /// connection is suspected to be dead.
    /// Matches the Go yamux <c>ConnectionWriteTimeout</c>.
    /// </summary>
    public TimeSpan ConnectionWriteTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The maximum time to wait for the frame reader to stop during session shutdown.
    /// </summary>
    public TimeSpan SessionCloseTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Default channel options used for newly opened channels and applied to accepted channels.
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

    /// <summary>
    /// Overrides the transport's batching mode. When <c>null</c>, the value of
    /// <see cref="ITransport.SupportsBatching"/> is used. When enabled, queued frames
    /// are written to the transport with
    /// <see cref="ITransport.WriteAsync(System.Buffers.ReadOnlySequence{byte}, System.Threading.CancellationToken)"/>.
    /// Only frames already queued at the same time are batched together.
    /// Default is <c>null</c>.
    /// </summary>
    public bool? WriteSegmentBatchingEnabled { get; set; }

    /// <summary>
    /// The minimum accumulated data (in bytes) that triggers a batched transport write.
    /// Accumulated data below this threshold is flushed when the write queue is drained
    /// or when <see cref="SessionChannel.FlushWritesAsync"/> is called.
    /// Only relevant when <see cref="WriteSegmentBatchingEnabled"/> is <c>true</c>.
    /// Default is 8,192 (8 KB).
    /// </summary>
    public int MinWriteBatchSize { get; set; } = 8192;
}
