using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace Yamux
{
    public static class SocketExtensions
    {
        /// <summary>
        /// Creates a yamux session from the socket
        /// </summary>
        /// <param name="stream"></param>
        /// <returns></returns>
        public static Session AsYamuxSession(this Socket socket, bool isClient, bool keepOpen = false, SessionOptions? options = null)
            => new Session(new SocketPeer(socket), isClient, keepOpen, options);
    }
}
