using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace Yamux.Protocol
{
    internal readonly record struct FrameHeader(ProtocolVersion Version, FrameType FrameType, Flags Flags, uint StreamId, uint Length)
    {
        public const int FrameHeaderSize = 12;

        public void WriteTo(Span<byte> span)
        {
            if (span.Length < FrameHeader.FrameHeaderSize)
            {
                throw new ArgumentException("Invalid write buffer length");
            }

            var buffer = span;

            buffer[0] = (byte)Version;
            buffer[1] = (byte)FrameType;
            BinaryPrimitives.WriteUInt16BigEndian(buffer.Slice(2, 2), (ushort)Flags);
            BinaryPrimitives.WriteUInt32BigEndian(buffer.Slice(4, 4), StreamId);
            BinaryPrimitives.WriteUInt32BigEndian(buffer.Slice(8, 4), Length);
        }

        public static FrameHeader Parse(ReadOnlySpan<byte> span)
        {
            if (span.Length < FrameHeaderSize)
            {
                throw new ArgumentException("Invalid frame header length");
            }

            var header = new FrameHeader(
                (ProtocolVersion)span[0],
                (FrameType)span[1],
                (Flags)BinaryPrimitives.ReadUInt16BigEndian(span.Slice(2, 2)),
                BinaryPrimitives.ReadUInt32BigEndian(span.Slice(4, 4)),
                BinaryPrimitives.ReadUInt32BigEndian(span.Slice(8, 4))
            );

            Validate(header);
            return header;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Validate(FrameHeader frameHeader)
        {
            if (!Enum.IsDefined(frameHeader.Version))
            {
                throw new SessionException(SessionErrorCode.InvalidVersion, "Invalid version while parsing yamux frame", SessionTermination.ProtocolError);
            }
            if (!Enum.IsDefined(frameHeader.FrameType))
            {
                throw new SessionException(SessionErrorCode.InvalidMsgType, "Invalid message type while parsing yamux frame", SessionTermination.ProtocolError);
            }
        }
    }
}
