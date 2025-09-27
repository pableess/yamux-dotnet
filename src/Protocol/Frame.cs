using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices.Marshalling;
using System.Text;
using System.Threading.Tasks;

namespace Yamux.Protocol
{
    internal readonly record struct Frame(FrameHeader Header, ReadOnlyMemory<byte> Payload) 
    {
        public static Frame CreateDataFrame(uint streamId, Flags flags, ReadOnlyMemory<byte> payload)
        {
            var header = new FrameHeader(ProtocolVersion.Initial, FrameType.Data, flags, streamId, (uint)payload.Length);
            return new Frame(header, payload);
        }

        public static Frame CreateWindowUpdateFrame(uint streamId, Flags flags, uint length)
        {
            var header = new FrameHeader(ProtocolVersion.Initial, FrameType.WindowUpdate, flags, streamId, length);
            return new Frame(header, ReadOnlyMemory<byte>.Empty);
        }

        public static Frame CreatePingRequestFrame(uint opaqueValue)
        {
            var header = new FrameHeader(ProtocolVersion.Initial, FrameType.Ping, Flags.SYN, 0, opaqueValue);
            return new Frame(header, ReadOnlyMemory<byte>.Empty);
        }
        public static Frame CreatePingResponseFrame(FrameHeader pingRequest)
        {
            var header = new FrameHeader(ProtocolVersion.Initial, FrameType.Ping, Flags.ACK, pingRequest.StreamId, pingRequest.Length);
            return new Frame(header, ReadOnlyMemory<byte>.Empty);
        }

        public static Frame CreateGoAwayFrame(SessionTermination termination)
        {
            var header = new FrameHeader(ProtocolVersion.Initial, FrameType.GoAway, Flags.None, 0, (uint)termination);
            return new Frame(header, ReadOnlyMemory<byte>.Empty);
        }
    }
}
