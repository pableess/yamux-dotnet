using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Yamux.Protocol;

namespace Yamux
{
    /// <summary>
    /// Defines error codes for session-level errors.
    /// </summary>
    public enum SessionErrorCode 
    {
        /// <summary>
        /// The received frame has an invalid protocol version.
        /// </summary>
        InvalidVersion,

        /// <summary>
        /// The received frame has an invalid message type.
        /// </summary>
        InvalidMsgType,

        /// <summary>
        /// The session has been shut down.
        /// </summary>
        SessionShutdown,

        /// <summary>
        /// The remote peer sent a GoAway and is no longer accepting new channels.
        /// </summary>
        RemoteGoAway,

        /// <summary>
        /// This session has sent a GoAway and is no longer accepting new channels.
        /// </summary>
        LocalGoAway,

        /// <summary>
        /// The maximum number of concurrent channels has been reached.
        /// </summary>
        StreamsExhausted,

        /// <summary>
        /// The sender exceeded the receiver's advertised window.
        /// </summary>
        RecvWindowExceeded,

        /// <summary>
        /// The underlying transport encountered an error.
        /// </summary>
        StreamError,

        /// <summary>
        /// The underlying transport was closed.
        /// </summary>
        StreamClosed,
    }

    /// <summary>
    /// Represents an error that occurred at the session level.
    /// </summary>
    [Serializable]
    public class SessionException : YamuxException
    {
        /// <summary>
        /// Gets the session error code.
        /// </summary>
        public SessionErrorCode ErrorCode { get; set; }

        /// <summary>
        /// Gets the GoAway termination code, if the error was caused by a GoAway frame.
        /// </summary>
        public SessionTermination? GoAwayCode { get; set; }

        /// <summary>
        /// Initializes a new instance of the <see cref="SessionException"/> class with the specified error code.
        /// </summary>
        /// <param name="err">The session error code.</param>
        /// <param name="termination">An optional GoAway termination code.</param>
        public SessionException(SessionErrorCode err, SessionTermination? termination = null) 
        {
            GoAwayCode = termination;
            ErrorCode = err;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="SessionException"/> class with a specified error code and message.
        /// </summary>
        /// <param name="err">The session error code.</param>
        /// <param name="message">The error message.</param>
        /// <param name="termination">An optional GoAway termination code.</param>
        public SessionException(SessionErrorCode err, string message, SessionTermination? termination = null) : base(message)
        {
            ErrorCode = err;
            GoAwayCode = termination;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="SessionException"/> class with a specified error code, message,
        /// and inner exception.
        /// </summary>
        /// <param name="err">The session error code.</param>
        /// <param name="message">The error message.</param>
        /// <param name="inner">The inner exception.</param>
        /// <param name="termination">An optional GoAway termination code.</param>
        public SessionException(SessionErrorCode err, string message, Exception inner, SessionTermination? termination = null) : base(message, inner)
        {
            ErrorCode = err;
            GoAwayCode = termination;
        }
    }
}
