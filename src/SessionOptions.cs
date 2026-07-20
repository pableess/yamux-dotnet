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

    /// <summary>
    /// Overrides the transport's default of mode of the writer accumulates multiple frames into a single Write call
    /// The transport must support the <see cref="ITransport.WriteAsync(System.Buffers.ReadOnlySequence{byte}, System.Threading.CancellationToken)"/> overload for this to be effective.
    /// This would only be useful if the transport is capable of scatter/gather writes (e.g., <c>SocketAsyncEventArgs.BufferList</c>).
    /// <see cref="System.Buffers.ReadOnlySequence{T}"/> and writes them to the transport
    /// in one call via <see cref="ITransport.WriteAsync(System.Buffers.ReadOnlySequence{byte}, System.Threading.CancellationToken)"/>.
    /// To realize throughput gains, the transport must implement that overload
    /// efficiently (e.g., scatter/gather via <c>SocketAsyncEventArgs.BufferList</c>).
    /// When disabled, each frame component (header, payload) is written individually
    /// via <see cref="ITransport.WriteAsync(System.ReadOnlyMemory{byte}, System.Threading.CancellationToken)"/>.
    /// Only frames already queued in the write channel at the same time are batched together.
    /// Default is <c>false</c>.
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
