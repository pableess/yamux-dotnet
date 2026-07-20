using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Yamux
{
    /// <summary>
    /// Adapter interface for sending and receiving data to and from the yamux peer.
    /// </summary>
    public interface ITransport : IDisposable
    {
        /// <summary>
        /// Reads data from the peer and copies it to the provided buffer.
        /// </summary>
        /// <param name="data">The destination buffer.</param>
        /// <param name="cancellationToken">A cancellation token to cancel the read operation.</param>
        /// <returns>The number of bytes read, or 0 if the connection was closed.</returns>
        public ValueTask<int> ReadAsync(Memory<byte> data, CancellationToken cancellationToken);

        /// <summary>
        /// Sends raw data to the peer.
        /// </summary>
        /// <param name="data">The data to send.</param>
        /// <param name="cancellationToken">A cancellation token to cancel the write operation.</param>
        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken);

        /// <summary>
        /// Closes the peer connection.
        /// </summary>
        public void Close();

        /// <summary>
        /// Flushes any buffered data to the underlying transport.
        /// </summary>
        /// <param name="cancellationToken">A cancellation token to cancel the flush operation.</param>
        public ValueTask FlushAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}
