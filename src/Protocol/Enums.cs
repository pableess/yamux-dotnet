using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Yamux.Protocol
{
    public enum SessionTermination : uint
    {
        Normal = 0x0,
        ProtocolError = 0x1,
        InternalError = 0x2,
    }

    internal enum ProtocolVersion : byte
    {
        Initial = 0x0
    }

    internal enum FrameType : byte
    {
        Data = 0x0,
        WindowUpdate = 0x1,
        Ping = 0x2,
        GoAway = 0x3,
    }

    internal enum Flags : ushort
    {
        None = 0x0,
        SYN = 0x1,
        ACK = 0x2,
        FIN = 0x4,
        RST = 0x8,
    }
}
