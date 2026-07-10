using System.IO.Pipelines;

namespace Yamux
{
    public static class PipeExtensions
    {
        /// <summary>
        /// Creates a yamux session from the duplex pipe
        /// </summary>
        public static Session AsYamuxSession(this IDuplexPipe pipe, bool isClient, bool keepOpen = false, SessionOptions? options = null)
            => new Session(new PipePeer(pipe), isClient, keepOpen, options);
    }
}
