using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Yamux
{
    /// <summary>
    /// Base exception for yamux errors
    /// </summary>
    public abstract class YamuxException : System.Exception
    {
        public YamuxException()
        {
        }
        public YamuxException(string message) : base(message)
        {
        }
        public YamuxException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }
}
