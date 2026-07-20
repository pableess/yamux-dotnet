using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Yamux
{
    /// <summary>
    /// Base exception for all Yamux-related errors.
    /// </summary>
    public abstract class YamuxException : System.Exception
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="YamuxException"/> class.
        /// </summary>
        public YamuxException()
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="YamuxException"/> class with a specified error message.
        /// </summary>
        /// <param name="message">The error message.</param>
        public YamuxException(string message) : base(message)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="YamuxException"/> class with a specified error message
        /// and a reference to the inner exception that caused this exception.
        /// </summary>
        /// <param name="message">The error message.</param>
        /// <param name="innerException">The inner exception.</param>
        public YamuxException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }
}
