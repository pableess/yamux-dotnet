using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Yamux.Internal
{
    /// <summary>
    /// Internal inteface for channels to use to interact with their session during their lifetime
    /// </summary>
    internal interface IChannelSessionAdapter
    {
        /// <summary>
        /// gets the session writer, which is used to send channel frames to the peer
        /// </summary>
        ConnectionWriter Writer { get; }

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
        TimeSpan? RTT { get; }
    }
}
