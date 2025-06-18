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
        ChannelRejected,
        ChannelClosed,
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
