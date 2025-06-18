using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Yamux.Protocol;

namespace Yamux.Internal
{
    /// <summary>
    /// Internal inteface for channels to use which limits what is exposed to the channel implementation, from the session class.
    /// Allows the channel to interact with its session.
    /// </summary>
    internal interface IChannelSessionAdapter
    {
        /// <summary>
        /// Asynchronously writes the data frame 
        /// </summary>
        /// <param name="streamId"></param>
        /// <param name="flags"></param>
        /// <param name="payload"></param>
        /// <param name="cancel"></param>
        /// <returns></returns>
        ValueTask WriteDataFrameAsync(uint streamId, Flags flags, ReadOnlyMemory<byte> payload, CancellationToken cancel);
        
        /// <summary>
        /// Asynchronously writes the window update frame
        /// </summary>
        /// <param name="streamId"></param>
        /// <param name="flags"></param>
        /// <param name="windowSizeIncrement"></param>
        /// <param name="cancel"></param>
        /// <returns></returns>
        ValueTask WriteWindowUpdateFrameAsync(uint streamId, Flags flags, uint windowSizeIncrement, CancellationToken cancel);

        /// <summary>
        /// Writes the frame data
        /// </summary>
        /// <param name="streamId"></param>
        /// <param name="flags"></param>
        /// <param name="payload"></param>
        void WriteDataFrame(uint streamId, Flags flags, ReadOnlyMemory<byte> payload);

        /// <summary>
        /// Writes the frame data
        /// </summary>
        /// <param name="streamId"></param>
        /// <param name="flags"></param>
        /// <param name="windowSizeIncrement"></param>
        void WriteWindowUpdateFrame(uint streamId, Flags flags, uint windowSizeIncrement);

        /// <summary>
        /// Synchronously flushes writes to the underlying stream
        /// </summary>
        void Flush();

        /// <summary>
        /// Asynchronously flushes writes to the underlying stream
        /// </summary>
        Task FlushAsync(CancellationToken cancel);

        /// <summary>
        /// Read frame payload data from the stream.  This must be called directly after reading the frame header and is not thread safe
        /// </summary>
        /// <param name="memory"></param>
        /// <param name="cancel"></param>
        /// <returns></returns>
        ValueTask<int> ReadPayloadDataAsync(Memory<byte> memory, CancellationToken cancel);

        /// <summary>
        /// Disconnect from the session, for cleanup on disposed channels
        /// </summary>
        void ChannelDisconnect(SessionChannel channel);

        /// <summary>
        /// Acknowledge or reject the channel
        /// </summary>
        /// <param name="channel"></param>
        void ChannelAcknowledge(SessionChannel channel, bool accept);

        /// <summary>
        /// The current round trip time for the session
        /// </summary>
        public TimeSpan? RTT { get; }
    }
}
