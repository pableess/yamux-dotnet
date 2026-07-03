using Yamux.Protocol;

namespace Yamux.Internal;

internal interface IChannelSessionAdapter
{
    ValueTask SendFrameAsync(Frame frame, CancellationToken cancel);

    void ChannelDisconnect(SessionChannel channel);

    void ChannelAcknowledge(SessionChannel channel, bool accept);

    TimeSpan? RTT { get; }

    Task SessionFault { get; }
}