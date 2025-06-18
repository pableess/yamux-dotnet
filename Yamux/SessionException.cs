using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Yamux.Protocol;

namespace Yamux
{
    public enum SessionErrorCode 
    {
        /// <summary>
        /// Invalid fram
        /// </summary>
        InvalidVersion,

        /// <summary>
        /// Invaid frame message type
        /// </summary>
        InvalidMsgType,

        /// <summary>
        /// The session has been closed
        /// </summary>
        SessionShutdown,

        /// <summary>
        /// the maximum number of open channels has been reached
        /// </summary>
        StreamsExhausted,

        /// <summary>
        /// the sender exceeded the receiver's window
        /// </summary>
        RecvWindowExceeded,

        /// <summary>
        /// underlying stream/connection encountered an error
        /// </summary>
        StreamError,

        /// <summary>
        /// underlying stream/connection was closed
        /// </summary>
        StreamClosed,
    }

    [Serializable]
    public class SessionException : YamuxException
    {
        /// <summary>
        /// Session Error code
        /// </summary>
        public SessionErrorCode ErrorCode { get; set; }

        /// <summary>
        /// If the session error was caused by a Go Away code
        /// </summary>
        public SessionTermination? GoAwayCode { get; set; }

        public SessionException(SessionErrorCode err, SessionTermination? termination = null) 
        {
            GoAwayCode = termination;
            ErrorCode = err;
        }

        public SessionException(SessionErrorCode err, string message, SessionTermination? termination = null) : base(message)
        {
            ErrorCode = err;
            GoAwayCode = termination;
        }

        public SessionException(SessionErrorCode err, string message, Exception inner, SessionTermination? termination = null) : base(message, inner)
        {
            ErrorCode = err;
            GoAwayCode = termination;
        }
    }
}
