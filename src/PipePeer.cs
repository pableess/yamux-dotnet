using System.IO.Pipelines;

namespace Yamux
{
    /// <summary>
    /// An <see cref="ITransport"/> implementation that wraps an <see cref="IDuplexPipe"/>.
    /// </summary>
    public class PipePeer : ITransport
    {
        private readonly PipeReader _reader;
        private readonly PipeWriter _writer;

        /// <summary>
        /// Initializes a new instance of the <see cref="PipePeer"/> class.
        /// </summary>
        /// <param name="pipe">The duplex pipe to use for transport.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="pipe"/> is null.</exception>
        public PipePeer(IDuplexPipe pipe)
        {
            ArgumentNullException.ThrowIfNull(pipe);
            _reader = pipe.Input;
            _writer = pipe.Output;
        }

        /// <inheritdoc />
        public async ValueTask<int> ReadAsync(Memory<byte> data, CancellationToken cancellationToken)
        {
            if (data.IsEmpty)
                return 0;

            var result = await _reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            var buffer = result.Buffer;

            if (buffer.IsEmpty && result.IsCompleted)
            {
                _reader.AdvanceTo(buffer.End);
                return 0;
            }

            var len = (int)Math.Min(buffer.Length, data.Length);
            var remaining = len;
            foreach (var segment in buffer)
            {
                if (remaining <= 0)
                    break;
                var toCopy = Math.Min(segment.Length, remaining);
                segment.Span[..toCopy].CopyTo(data.Span[(len - remaining)..]);
                remaining -= toCopy;
            }
            _reader.AdvanceTo(buffer.GetPosition(len));

            return len;
        }

        /// <inheritdoc />
        public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        {
            if (data.IsEmpty)
                return;

            await _writer.WriteAsync(data, cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async ValueTask FlushAsync(CancellationToken cancellationToken = default)
        {
            await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public void Close()
        {
            _reader.Complete();
            _writer.Complete();
        }

        /// <inheritdoc />
        public void Dispose()
        {
            _reader.Complete();
            _writer.Complete();
        }
    }
}
