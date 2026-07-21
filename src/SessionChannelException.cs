using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Yamux
{
    /// <summary>
    /// Error codes for channel-level errors. These are local to this implementation
    /// and are not transmitted over the wire as part of the Yamux protocol.
    /// </summary>
    public enum ChannelErrorCode 
    {
        /// <summary>
        /// The remote peer has rejected the channel (RST received).
        /// </summary>
        ChannelRejected,

        /// <summary>
        /// The channel's write side has been closed. No more data can be sent.
        /// </summary>
        ChannelWriteClosed,

        /// <summary>
        /// The channel is fully closed and no data can be sent or received.
        /// </summary>
        ChannelClosed,

        /// <summary>
        /// The underlying session has been closed, so the channel is no longer operable.
        /// </summary>
        SessionClosed,

        /// <summary>
        /// The channel is not in a valid state for the attempted operation.
        /// </summary>
        InvalidChannelState,

    }


    /// <summary>
    /// Represents an error that occurred on a specific channel.
    /// </summary>
    [Serializable]
    public class SessionChannelException : YamuxException
    {
        /// <summary>
        /// Gets the channel error code.
        /// </summary>
        public ChannelErrorCode ErrorCode;

        /// <summary>
        /// Initializes a new instance of the <see cref="SessionChannelException"/> class with the specified error code.
        /// </summary>
        /// <param name="errorCode">The channel error code.</param>
        public SessionChannelException(ChannelErrorCode errorCode)
        {
            ErrorCode = errorCode;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="SessionChannelException"/> class with a specified error code and message.
        /// </summary>
        /// <param name="errorCode">The channel error code.</param>
        /// <param name="message">The error message.</param>
        public SessionChannelException(ChannelErrorCode errorCode, string message) : base(message) 
        {
            ErrorCode = errorCode;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="SessionChannelException"/> class with a specified error code,
        /// message, and inner exception.
        /// </summary>
        /// <param name="errorCode">The channel error code.</param>
        /// <param name="message">The error message.</param>
        /// <param name="inner">The inner exception.</param>
        public SessionChannelException(ChannelErrorCode errorCode, string message, Exception inner) : base(message, inner)
        {
            ErrorCode = errorCode;
        }
    }
}
