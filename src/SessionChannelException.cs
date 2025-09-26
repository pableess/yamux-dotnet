using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Yamux
{
    /// <summary>
    /// Errors codes for channels (not part of the Yamux spec or transmitted), just used locally for this implementation
    /// </summary>
    public enum ChannelErrorCode 
    {
        /// <summary>
        /// The peer has rejected the channel
        /// </summary>
        ChannelRejected,

        /// <summary>
        /// Channel is half closed and data can no longer be sent on it
        /// </summary>
        ChannelWriteClosed,

        /// <summary>
        /// The channel is fully closed and no data can be sent or received
        /// </summary>
        ChannelClosed,

        /// <summary>
        /// The underlying session transmission has been closed, so the channel is not operable anymore
        /// </summary>
        SessionClosed,
    }


    [Serializable]
    public class SessionChannelException : YamuxException
    {
        public ChannelErrorCode ErrorCode;

        public SessionChannelException(ChannelErrorCode errorCode)
        {
            ErrorCode = errorCode;
        }

        public SessionChannelException(ChannelErrorCode errorCode, string message) : base(message) 
        {
            ErrorCode = errorCode;
        }

        public SessionChannelException(ChannelErrorCode errorCode, string message, Exception inner) : base(message, inner)
        {
            ErrorCode = errorCode;
        }
    }
}
