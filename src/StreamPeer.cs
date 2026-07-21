namespace Yamux
{
    /// <summary>
    /// An <see cref="ITransport"/> implementation that wraps a <see cref="System.IO.Stream"/>.
    /// </summary>
    public class StreamPeer : ITransport
    {
        private readonly System.IO.Stream _stream;

        /// <summary>
        /// Initializes a new instance of the <see cref="StreamPeer"/> class.
        /// </summary>
        /// <param name="stream">The underlying stream to use for transport.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="stream"/> is null.</exception>
        public StreamPeer(System.IO.Stream stream)
        {
            _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        }

        /// <inheritdoc />
        public async ValueTask<int> ReadAsync(Memory<byte> data, CancellationToken cancellationToken)
        {
            if (data.IsEmpty)
                return 0;
            return await _stream.ReadAsync(data, cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        {
            if (data.IsEmpty)
                return;

            await _stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async ValueTask FlushAsync(CancellationToken cancellationToken = default)
        {
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public void Close()
        {
            _stream.Close();
        }

        /// <inheritdoc />
        public void Dispose()
        {
            _stream.Dispose();
        }
    }
}
