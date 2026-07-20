using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace Yamux
{
    /// <summary>
    /// Extension methods for creating Yamux sessions from <see cref="System.Net.Sockets.Socket"/> instances.
    /// </summary>
    public static class SocketExtensions
    {
        /// <summary>
        /// Creates a Yamux session over the provided socket.
        /// </summary>
        /// <param name="socket">The socket to use as the transport.</param>
        /// <param name="isClient">Whether this is the client side of the connection.</param>
        /// <param name="leaveOpen">Whether to leave the socket open when the session is disposed.</param>
        /// <param name="options">Session configuration options. If null, default options are used.</param>
        /// <returns>A new <see cref="Session"/> instance.</returns>
        public static Session AsYamuxSession(this System.Net.Sockets.Socket socket, bool isClient, bool leaveOpen = false, SessionOptions? options = null)
            => new Session(new SocketPeer(socket), isClient, leaveOpen, options);
    }
}
