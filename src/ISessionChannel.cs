using System.IO.Pipelines;

namespace Yamux;

/// <summary>
/// Represents a Yamux session channel (logical stream) multiplexed over a single connection.
/// </summary>
public interface ISessionChannel : IDisposable, IAsyncDisposable
{
    /// <summary>
    /// Gets the unique ID for this channel.
    /// </summary>
    public uint Id { get; }

    /// <summary>
    /// Gets whether the channel has been fully closed.
    /// </summary>
    public bool IsClosed { get; }

    /// <summary>
    /// Aborts the channel immediately, sending a RST to the remote peer if the channel is not already closed.
    /// </summary>
    public void Abort();

    /// <summary>
    /// Closes the channel for writing, sending a FIN to the remote peer.
    /// More data may still be read from the channel until the remote peer acknowledges the close.
    /// To wait for the remote peer's acknowledgment, continue reading until the pipe is completed,
    /// or call <see cref="WaitForRemoteClose"/> or <see cref="WhenRemoteCloseAsync"/>.
    /// </summary>
    public void Close();

    /// <summary>
    /// Waits until the remote peer has closed the channel.
    /// </summary>
    /// <param name="timeout">The maximum amount of time to wait.</param>
    /// <returns><c>true</c> if the remote peer closed the channel; <c>false</c> if the operation timed out.</returns>
    public bool WaitForRemoteClose(TimeSpan timeout);

    /// <summary>
    /// Returns a task that completes when the remote peer has closed the channel.
    /// Use with caution, as the remote peer may fail to send a proper close acknowledgment.
    /// </summary>
    /// <param name="timeout">The maximum amount of time to wait.</param>
    /// <returns><c>true</c> if the remote peer closed the channel; <c>false</c> if the timeout was reached.</returns>
    public Task<bool> WhenRemoteCloseAsync(TimeSpan timeout);

    /// <summary>
    /// Waits until the remote peer has acknowledged the channel open.
    /// Only useful when a channel was accepted without waiting for acknowledgment.
    /// </summary>
    /// <param name="timeout">The maximum amount of time to wait.</param>
    /// <returns><c>true</c> if the remote peer acknowledged; <c>false</c> if the operation timed out.</returns>
    public bool WaitForRemoteAck(TimeSpan timeout);

    /// <summary>
    /// Returns a task that completes when the remote peer has acknowledged the channel.
    /// Use with caution, as the remote peer may fail to send a proper acknowledgment.
    /// </summary>
    /// <param name="timeout">The maximum amount of time to wait.</param>
    /// <returns><c>true</c> if the remote peer acknowledged before the timeout; <c>false</c> if the timeout was reached.</returns>
    public Task<bool> WhenRemoteAckAsync(TimeSpan timeout);

    /// <summary>
    /// Ensures that all written data has been flushed to the underlying transport.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token to cancel the flush operation.</param>
    public ValueTask FlushWritesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the statistics for the channel, if statistics gathering is enabled.
    /// </summary>
    public Statistics? Stats { get; }
}

/// <summary>
/// Represents a session channel that supports writing data to the remote peer.
/// </summary>
public interface IWriteOnlySessionChannel : ISessionChannel
{
    /// <summary>
    /// Writes data to the channel. The task will not complete until all data has been
    /// passed to the underlying transport, which may require waiting for window updates from the remote peer.
    /// This is not an atomic operation — partial data may be written in the case of a failure.
    /// </summary>
    /// <param name="buffer">The data to write.</param>
    /// <param name="cancellationToken">A cancellation token to cancel the write operation.</param>
    public ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default);
}

/// <summary>
/// Represents a session channel that supports reading data from the remote peer.
/// </summary>
public interface IReadOnlySessionChannel : ISessionChannel
{
    /// <summary>
    /// Gets the <see cref="PipeReader"/> for reading data from this channel.
    /// </summary>
    public PipeReader Input { get; }
}

/// <summary>
/// Represents a full-duplex session channel that supports both reading and writing.
/// </summary>
public interface IDuplexSessionChannel : ISessionChannel, IWriteOnlySessionChannel, IReadOnlySessionChannel
{
    /// <summary>
    /// Creates a <see cref="Stream"/> wrapper for reading from and writing to this channel.
    /// </summary>
    /// <param name="leaveOpen">Whether to leave the channel open when the stream is disposed.</param>
    /// <returns>A <see cref="Stream"/> that reads from and writes to this channel.</returns>
    public Stream AsStream(bool leaveOpen = false);
}
