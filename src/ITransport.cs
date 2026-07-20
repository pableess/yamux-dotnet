using System.Buffers;

namespace Yamux
{
/// <summary>
/// Represents a bidirectional transport layer that can be used by a Yamux session.
/// Implementations wrap stream-oriented transports such as TCP sockets, named pipes,
/// or in-memory pipes.
/// </summary>
/// <remarks>
/// The transport must provide reliable, ordered, full-duplex byte delivery.
/// Partial reads are handled internally by the session, so implementations may return
/// fewer bytes than requested.
/// </remarks>
public interface ITransport : IDisposable
{
    /// <summary>
    /// Reads data from the transport into the provided buffer.
    /// </summary>
    /// <param name="data">The buffer to fill with read data.</param>
    /// <param name="cancellationToken">A cancellation token to cancel the read operation.</param>
    /// <returns>The number of bytes read. 0 indicates the transport has been closed by the remote peer.</returns>
    ValueTask<int> ReadAsync(Memory<byte> data, CancellationToken cancellationToken);

    /// <summary>
    /// Writes data to the transport.
    /// </summary>
    /// <param name="data">The data to write.</param>
    /// <param name="cancellationToken">A cancellation token to cancel the write operation.</param>
    /// <returns>A <see cref="ValueTask"/> representing the asynchronous write operation.</returns>
    ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken);

    /// <summary>
    /// Closes the transport. After calling this, no further reads or writes should be attempted.
    /// </summary>
    void Close();

    /// <summary>
    /// Flushes any buffered data to the underlying transport. A default no-op implementation is provided.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token to cancel the flush operation.</param>
    /// <returns>A <see cref="ValueTask"/> representing the asynchronous flush operation.</returns>
    ValueTask FlushAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    /// <summary>
    /// Writes a sequence of byte segments to the transport. The default implementation
    /// iterates over the segments and calls <see cref="WriteAsync(ReadOnlyMemory{byte}, CancellationToken)"/> for each.
    /// </summary>
    /// <param name="data">The sequence of byte segments to write.</param>
    /// <param name="cancellationToken">A cancellation token to cancel the write operation.</param>
    /// <returns>A <see cref="ValueTask"/> representing the asynchronous write operation.</returns>
    ValueTask WriteAsync(ReadOnlySequence<byte> data, CancellationToken cancellationToken = default)
    {
        return WriteSequenceAsync(this, data, cancellationToken);

        static async ValueTask WriteSequenceAsync(ITransport transport, ReadOnlySequence<byte> data, CancellationToken ct)
        {
            foreach (var segment in data)
                await transport.WriteAsync(segment, ct).ConfigureAwait(false);
        }
    }
}
}
