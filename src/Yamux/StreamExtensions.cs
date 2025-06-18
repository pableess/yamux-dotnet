using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Yamux.Protocol;

namespace Yamux
{
    public static class StreamExtensions
    {
        /// <summary>
        /// Craetes a yamux session from the raw stream
        /// </summary>
        /// <param name="stream"></param>
        /// <returns></returns>
        public static Session AsYamuxSession(this Stream stream, bool isClient, bool keepOpen = false, SessionOptions? options = null)
            => new Session(new StreamFrameFormatter(stream, keepOpen), isClient, options);
    }
}
