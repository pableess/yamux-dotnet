using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Yamux
{
    public class StreamPeer : ITransport
    {
        private readonly System.IO.Stream _stream;
        public StreamPeer(System.IO.Stream stream)
        {
            _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        }

        public async ValueTask<int> ReadAsync(Memory<byte> data, CancellationToken cancel)
        {
            if (data.IsEmpty)
                return 0;
            return await _stream.ReadAsync(data, cancel);
        }

        public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancel)
        {
            if (data.IsEmpty)
                return;

            await _stream.WriteAsync(data, cancel);
        }

        public void Close()
        {
            _stream.Close();
        }

        public void Dispose()
        {
            _stream.Dispose();
        }
    }
}
