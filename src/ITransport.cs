using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Yamux
{
    /// <summary>
    /// Adapter interface for sending and receiving data to and from the yamux peer
    /// </summary>
    public interface ITransport : IDisposable
    {
        /// <summary>
        /// Reads data from the peer copies it to the provided buffer
        /// </summary>
        /// <param name="buffer"></param>
        /// <returns></returns>
        public ValueTask<int> ReadAsync(Memory<byte> data, CancellationToken cancel);

        /// <summary>
        /// Sends raw data to the peer
        /// </summary>
        /// <param name="data"></param>
        /// <returns></returns>
        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancel);

        /// <summary>
        /// Closes the peer connection
        /// </summary>
        public void Close();
    }
}
