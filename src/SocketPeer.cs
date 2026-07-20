namespace Yamux
{
    /// <summary>
    /// An <see cref="ITransport"/> implementation that wraps a <see cref="System.Net.Sockets.Socket"/>.
    /// </summary>
    public class SocketPeer : ITransport
    {
        private readonly System.Net.Sockets.Socket _socket;

        /// <summary>
        /// Initializes a new instance of the <see cref="SocketPeer"/> class.
        /// </summary>
        /// <param name="socket">The underlying socket to use for transport.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="socket"/> is null.</exception>
        public SocketPeer(System.Net.Sockets.Socket socket)
        {
            _socket = socket ?? throw new ArgumentNullException(nameof(socket));
        }

        /// <inheritdoc />
        public void Close()
        {
            _socket.Shutdown(System.Net.Sockets.SocketShutdown.Both);
            _socket.Close();
        }

        /// <inheritdoc />
        public void Dispose()
        {
            _socket.Dispose();
        }

        /// <inheritdoc />
        public async ValueTask<int> ReadAsync(Memory<byte> data, CancellationToken cancellationToken)
        {
            var read = await _socket.ReceiveAsync(data, System.Net.Sockets.SocketFlags.None, cancellationToken).ConfigureAwait(false);

            return read;
        }

        /// <inheritdoc />
        public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        {
            if (data.IsEmpty)
            {
                return;
            }

            await _socket.SendAsync(data, System.Net.Sockets.SocketFlags.None, cancellationToken).ConfigureAwait(false);
        }
    }
}
