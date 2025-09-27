using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace Yamux
{
    public class SocketPeer : ITransport
    {
        private readonly System.Net.Sockets.Socket _socket;
        public SocketPeer(System.Net.Sockets.Socket socket)
        {
            _socket = socket ?? throw new ArgumentNullException(nameof(socket));
        }

        public void Close()
        {
            _socket.Shutdown(System.Net.Sockets.SocketShutdown.Both);
            _socket.Close();
        }

        public async ValueTask<int> ReadAsync(Memory<byte> data, CancellationToken cancel)
        {
            var read = await _socket.ReceiveAsync(data, System.Net.Sockets.SocketFlags.None, cancel);

            return read;
        }
        public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancel)
        {
            if (data.IsEmpty)
            {
                return;
            }

            await _socket.SendAsync(data, System.Net.Sockets.SocketFlags.None, cancel);
        }
    }
}
