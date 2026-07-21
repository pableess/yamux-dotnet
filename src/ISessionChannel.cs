using System.IO.Pipelines;

namespace Yamux;

/// <summary>
/// Represents a single multiplexed channel within a Yamux session.
/// Provides the core operations for channel lifecycle management.
/// </summary>
public interface ISessionChannel : IDisposable, IAsyncDisposable
{
    /// <summary>
    /// Gets the unique identifier for this channel.
    /// Client-initiated channels use odd IDs; server-initiated channels use even IDs.
    /// </summary>
    public uint Id { get; }

    /// <summary>
    /// Gets whether the channel has been fully closed (both read and write sides).
    /// </summary>
    public bool IsClosed { get; }

    /// <summary>
    /// Forcibly terminates the channel by sending a RST frame to the remote peer.
    /// Unlike <see cref="Close"/>, this bypasses the graceful FIN handshake.
    /// </summary>
    public void Abort();

    /// <summary>
    /// Gracefully closes the write side of the channel by sending a FIN frame.
    /// The read side remains open until the remote peer closes their side.
    /// </summary>
    public void Close();

    /// <summary>
    /// Waits for the remote peer to close their side of the channel, with a timeout.
    /// </summary>
    /// <param name="timeout">The maximum time to wait for the remote close.</param>
    /// <returns><c>true</c> if the remote peer acknowledged the close within the timeout; otherwise <c>false</c>.</returns>
    public Task<bool> WhenRemoteCloseAsync(TimeSpan timeout);

    /// <summary>
    /// Waits for the remote peer to acknowledge this channel (SYN/ACK handshake), with a timeout.
    /// </summary>
    /// <param name="timeout">The maximum time to wait for the remote acknowledgement.</param>
    /// <returns><c>true</c> if the remote peer acknowledged within the timeout; otherwise <c>false</c>.</returns>
    public Task<bool> WhenRemoteAckAsync(TimeSpan timeout);

    /// <summary>
    /// Flushes any buffered writes to the underlying transport.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token to cancel the flush operation.</param>
    /// <returns>A <see cref="ValueTask"/> representing the asynchronous flush operation.</returns>
    public ValueTask FlushWritesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the bandwidth and byte statistics for this channel, if enabled.
    /// </summary>
    public Statistics? Stats { get; }
}

/// <summary>
/// Represents a channel that supports write operations.
/// </summary>
public interface IWriteOnlySessionChannel : ISessionChannel
{
    /// <summary>
    /// Writes data to the channel. The data will be framed and sent to the remote peer.
    /// Respects the remote peer's flow control window.
    /// </summary>
    /// <param name="buffer">The data to write.</param>
    /// <param name="cancellationToken">A cancellation token to cancel the write operation.</param>
    /// <returns>A <see cref="ValueTask"/> representing the asynchronous write operation.</returns>
    public ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default);
}

/// <summary>
/// Represents a channel that supports read operations via a <see cref="System.IO.Pipelines.PipeReader"/>.
/// </summary>
public interface IReadOnlySessionChannel : ISessionChannel
{
    /// <summary>
    /// Gets the <see cref="System.IO.Pipelines.PipeReader"/> for reading data from this channel.
    /// </summary>
    public PipeReader Input { get; }
}

/// <summary>
/// Represents a full-duplex channel that supports both read and write operations.
/// This is the primary channel type used in Yamux sessions.
/// </summary>
public interface IDuplexSessionChannel : ISessionChannel, IWriteOnlySessionChannel, IReadOnlySessionChannel
{
    /// <summary>
    /// Wraps the channel as a <see cref="Stream"/> for compatibility with stream-based APIs.
    /// </summary>
    /// <param name="leaveOpen">If <c>true</c>, the underlying channel is not disposed when the stream is disposed.</param>
    /// <returns>A <see cref="Stream"/> that reads from and writes to this channel.</returns>
    public Stream AsStream(bool leaveOpen = false);
}
