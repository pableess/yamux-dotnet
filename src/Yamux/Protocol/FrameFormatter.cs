using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using System.Threading.Tasks;
using Yamux.Internal;

namespace Yamux.Protocol;

/// <summary>
/// Internal class for reading and writing the Yamux protocol frames to an underlying stream.
/// Public read methods are not thread safe and should be called in sequence from a sequential task
/// Public write methods are thread safe and can be called concurrently from multiple write channels
/// </summary>
internal abstract class FrameFormatterBase : IDisposable, IAsyncDisposable
{
    /// <summary>
    /// Only a single version of the spec is defined at this time.
    /// </summary>
    private readonly static ProtocolVersion Version = ProtocolVersion.Initial;


    internal readonly record struct FrameHeader(ProtocolVersion Version, FrameType FrameType, Flags Flags, uint StreamId, uint Length)
    {
        public const int FrameHeaderSize = 12;

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

    /// <summary>
    /// Read frame payload data from the stream.  This must be called directly after reading the frame header and is not thread safe
    /// 
    /// </summary>
    /// <param name="memory"></param>
    /// <param name="cancel"></param>
    /// <returns></returns>
    public abstract ValueTask<int> ReadPayloadDataAsync(Memory<byte> memory, CancellationToken cancel);

    /// <summary>
    /// Read a data frame to the stream
    /// </summary>
    /// <param name="streamId"></param>
    /// <param name="flags"></param>
    /// <param name="payload"></param>
    /// <param name="cancel"></param>
    /// <returns></returns>
    public ValueTask WriteDataFrameAsync(uint streamId, Flags flags, ReadOnlyMemory<byte> payload, CancellationToken cancel)
        => WriteFrameAsync(FrameType.Data, flags, streamId, payload, cancel);

    /// <summary>
    /// Write a data frame to the stream
    /// </summary>
    /// <param name="streamId"></param>
    /// <param name="flags"></param>
    /// <param name="payload"></param>
    public void WriteDataFrame(uint streamId, Flags flags, ReadOnlyMemory<byte> payload)
        => WriteFrame(FrameType.Data, flags, streamId, (uint)payload.Length, payload);

    /// <summary>
    /// Write a window update frame to the stream
    /// </summary>
    /// <param name="streamId"></param>
    /// <param name="flags"></param>
    /// <param name="windowSizeIncrement"></param>
    /// <param name="cancel"></param>
    /// <returns></returns>
    public ValueTask WriteWindowUpdateFrameAsync(uint streamId, Flags flags, uint windowSizeIncrement, CancellationToken cancel)
        => WriteFrameAsync(FrameType.WindowUpdate, flags, streamId, windowSizeIncrement, cancel);

    /// <summary>
    /// Write a window update frame to the stream
    /// </summary>
    /// <param name="streamId"></param>
    /// <param name="flags"></param>
    /// <param name="windowSizeIncrement"></param>
    public void WriteWindowUpdateFrame(uint streamId, Flags flags, uint windowSizeIncrement)
        => WriteFrame(FrameType.WindowUpdate, flags, streamId, windowSizeIncrement, ReadOnlyMemory<byte>.Empty);

    /// <summary>
    /// Write a ping frame to the stream
    /// </summary>
    /// <param name="streamId"></param>
    /// <param name="flags"></param>
    /// <param name="opaqueValue"></param>
    /// <param name="cancel"></param>
    public ValueTask WritePingAsync(uint opaqueValue, CancellationToken cancel)
       => WriteFrameAsync(FrameType.Ping, Flags.None, 0, opaqueValue, cancel);

    /// <summary>
    /// Write a ping ack frame to the stream
    /// </summary>
    /// <param name="streamId"></param>
    /// <param name="flags"></param>
    /// <param name="opaqueValue"></param>
    /// <param name="cancel"></param>
    public ValueTask WritePingAckAsync(uint opaqueValue, CancellationToken cancel)
     => WriteFrameAsync(FrameType.Ping, Flags.ACK, 0, opaqueValue, cancel);

    /// <summary>
    /// Write a go away frame to the stream
    /// </summary>
    /// <param name="streamId"></param>
    /// <param name="errorCode"></param>
    /// <param name="cancel"></param>
    /// <returns></returns>
    public ValueTask WriteGoAwayAsync(SessionTermination errorCode, CancellationToken cancel)
        => WriteFrameAsync(FrameType.GoAway, Flags.None, 0, (uint)errorCode, cancel);

    /// <summary>
    /// Write go away
    /// </summary>
    /// <param name="errorCode"></param>
    public void WriteGoAway(SessionTermination errorCode)
     => WriteFrame(FrameType.GoAway, Flags.None, 0, (uint)errorCode, ReadOnlyMemory<byte>.Empty);

    public abstract void Flush();

    public abstract Task FlushAsync(CancellationToken cancel);

    public abstract ValueTask DisposeAsync();

    public abstract void Dispose();

    public abstract ValueTask<FrameHeader> ReadFrameHeaderAsync(CancellationToken cancel);

    public void SetFrameHeaderBuffer(FrameType type, Flags flags, uint streamId, uint length, Memory<byte> mem)
    {
        
        if (mem.Length < FrameHeader.FrameHeaderSize)
        {
            throw new ArgumentException("Invalid write buffer length");
        }

        var buffer = mem.Span;

        buffer[0] = (byte)Version;
        buffer[1] = (byte)type;
        BinaryPrimitives.WriteUInt16BigEndian(buffer.Slice(2, 2), (ushort)flags);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.Slice(4, 4), streamId);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.Slice(8, 4), length);
    }

    protected async ValueTask WriteFrameAsync(FrameType type, Flags flags, uint streamId, ReadOnlyMemory<byte> payload, CancellationToken cancel)
        => await WriteFrameInternalAsync(type, flags, streamId, (uint)payload.Length, payload, cancel);

    protected async ValueTask WriteFrameAsync(FrameType type, Flags flags, uint streamId, uint length, CancellationToken cancel)
        => await WriteFrameInternalAsync(type, flags, streamId, length, ReadOnlyMemory<byte>.Empty, cancel);

    protected void WriteFrame(FrameType type, Flags flags, uint streamId, uint length, ReadOnlyMemory<byte> payload) => WriteFrameInternal(type, flags, streamId, length, payload);

    protected abstract void WriteFrameInternal(FrameType type, Flags flags, uint streamId, uint length, ReadOnlyMemory<byte> payload);

    // write a frame to the stream, this is thread safe as only a single writer is allowed
    protected abstract ValueTask WriteFrameInternalAsync(FrameType type, Flags flags, uint streamId, uint length, ReadOnlyMemory<byte> payload, CancellationToken cancel);
}
