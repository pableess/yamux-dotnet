using System.IO.Pipelines;

namespace Yamux
{
    /// <summary>
    /// Extension methods for creating Yamux sessions from <see cref="IDuplexPipe"/> instances.
    /// </summary>
    public static class PipeExtensions
    {
        /// <summary>
        /// Creates a Yamux session over the provided duplex pipe.
        /// </summary>
        /// <param name="pipe">The duplex pipe to use as the transport.</param>
        /// <param name="isClient">Whether this is the client side of the connection.</param>
        /// <param name="leaveOpen">Whether to leave the pipe open when the session is disposed.</param>
        /// <param name="options">Session configuration options. If null, default options are used.</param>
        /// <returns>A new <see cref="Session"/> instance.</returns>
        public static Session AsYamuxSession(this IDuplexPipe pipe, bool isClient, bool leaveOpen = false, SessionOptions? options = null)
            => new Session(new PipePeer(pipe), isClient, leaveOpen, options);
    }
}
