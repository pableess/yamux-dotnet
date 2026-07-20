using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Yamux.Protocol;

namespace Yamux
{
    /// <summary>
    /// Extension methods for creating Yamux sessions from <see cref="Stream"/> instances.
    /// </summary>
    public static class StreamExtensions
    {
        /// <summary>
        /// Creates a Yamux session over the provided stream.
        /// </summary>
        /// <param name="stream">The stream to use as the transport.</param>
        /// <param name="isClient">Whether this is the client side of the connection.</param>
        /// <param name="leaveOpen">Whether to leave the stream open when the session is disposed.</param>
        /// <param name="options">Session configuration options. If null, default options are used.</param>
        /// <returns>A new <see cref="Session"/> instance.</returns>
        public static Session AsYamuxSession(this Stream stream, bool isClient, bool leaveOpen = false, SessionOptions? options = null)
            => new Session(new StreamPeer(stream), isClient, leaveOpen, options);
    }
}
