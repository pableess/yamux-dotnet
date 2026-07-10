using System.IO.Pipelines;

namespace Yamux
{
    public class PipePeer : ITransport
    {
        private readonly PipeReader _reader;
        private readonly PipeWriter _writer;

        public PipePeer(IDuplexPipe pipe)
        {
            ArgumentNullException.ThrowIfNull(pipe);
            _reader = pipe.Input;
            _writer = pipe.Output;
        }

        public async ValueTask<int> ReadAsync(Memory<byte> data, CancellationToken cancel)
        {
            if (data.IsEmpty)
                return 0;

            var result = await _reader.ReadAsync(cancel);
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

        public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancel)
        {
            if (data.IsEmpty)
                return;

            await _writer.WriteAsync(data, cancel);
        }

        public void Close()
        {
            _reader.Complete();
            _writer.Complete();
        }

        public void Dispose()
        {
            _reader.Complete();
            _writer.Complete();
        }
    }
}
