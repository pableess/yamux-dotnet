using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Channels;
using System.Threading.Tasks;
using Yamux.Internal;

namespace Yamux.Protocol;

internal class StreamFrameFormatter : FrameFormatterBase
{
    private readonly byte[] _frameHeaderReadBuffer;
    private readonly byte[] _frameHeaderWriteBuffer;

    private readonly Stream _stream;
    private readonly bool _keepOpen;

    private readonly SemaphoreSlim _writeLock;

    public StreamFrameFormatter(Stream stream, bool keepOpen = false)       
    {
        _stream = stream;
        _keepOpen = keepOpen;
        _frameHeaderReadBuffer = new byte[FrameHeader.FrameHeaderSize];
        _frameHeaderWriteBuffer = new byte[FrameHeader.FrameHeaderSize];
        _writeLock = new SemaphoreSlim(1);
    }

    /// <summary>
    /// Read frame payload data from the stream.  This must be called directly after reading the frame header and is not thread safe
    /// 
    /// </summary>
    /// <param name="memory"></param>
    /// <param name="cancel"></param>
    /// <returns></returns>
    public override ValueTask<int> ReadPayloadDataAsync(Memory<byte> memory, CancellationToken cancel) => _stream.ReadAsync(memory, cancel);

    public override void Flush() => _stream.Flush();

    public override Task FlushAsync(CancellationToken cancel) => _stream.FlushAsync(cancel);

    public override async ValueTask DisposeAsync()
    {
        if (!_keepOpen)
        {
            await _stream.FlushAsync();
            await _stream.DisposeAsync();
        }
    }

    public override void Dispose()
    {
        if (!_keepOpen)
        {
            _stream.Flush();
            _stream.Dispose();
        }
    }

    public override async ValueTask<FrameHeader> ReadFrameHeaderAsync(CancellationToken cancel)
    {
        await _stream.ReadExactlyAsync(_frameHeaderReadBuffer, cancel);

        return FrameHeader.Parse(_frameHeaderReadBuffer);
    }

    private async ValueTask WriteFrameHeaderAsync(FrameType type, Flags flags, uint streamId, uint length, CancellationToken cancel)
    {
        SetFrameHeaderBuffer(type, flags, streamId, length, _frameHeaderWriteBuffer);
        await _stream.WriteAsync(_frameHeaderWriteBuffer, cancel);
    }

    private void WriteFrameHeader(FrameType type, Flags flags, uint streamId, uint length)
    {
        SetFrameHeaderBuffer(type, flags, streamId, length, _frameHeaderWriteBuffer);
        _stream.Write(_frameHeaderWriteBuffer, 0, _frameHeaderWriteBuffer.Length);
    }


    // write a frame to the stream, this is thread safe as only a single writer is allowed
    protected override async ValueTask WriteFrameInternalAsync(FrameType type, Flags flags, uint streamId, uint length, ReadOnlyMemory<byte> payload, CancellationToken cancel)
    {
        await _writeLock.WaitAsync(cancel);
        using SemaphoreReleaser lockRelease = new SemaphoreReleaser(_writeLock);


        await WriteFrameHeaderAsync(type, flags, streamId, length, cancel);
        if (payload.Length > 0)
        {
            await _stream.WriteAsync(payload, cancel);
        }
    }

    protected override void WriteFrameInternal(FrameType type, Flags flags, uint streamId, uint length, ReadOnlyMemory<byte> payload)
    {
        _writeLock.Wait();
        using SemaphoreReleaser lockRelease = new SemaphoreReleaser(_writeLock);


        WriteFrameHeader(type, flags, streamId, length);
        if (payload.Length > 0)
        {
            _stream.Write(payload.Span);
        }
    }

}
