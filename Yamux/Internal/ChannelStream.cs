using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Yamux.Internal;

internal class ChannelStream : Stream
{
    private readonly SessionChannel _outputChannel;
    private readonly Stream _inputPipeStream;

    private bool _disposed;

    public ChannelStream(SessionChannel outputChannel, bool leaveOpen)
    {
        _outputChannel = outputChannel;
        _inputPipeStream = outputChannel.Input.AsStream();
        LeaveOpen = leaveOpen;
    }

    public bool LeaveOpen { get; set; }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Flush() => _outputChannel.FlushWrites();

    public override Task FlushAsync(CancellationToken cancellationToken) => _outputChannel.FlushWritesAsync(cancellationToken);

    public override long Seek(long offset, SeekOrigin origin)
    {
        throw new NotImplementedException();
    }

    public override void SetLength(long value)
    {
        throw new NotImplementedException();
    }

    public override void Write(byte[] buffer, int offset, int count)
        => _outputChannel.Write(new ReadOnlyMemory<byte>(buffer, offset, count));

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => _outputChannel.WriteAsync(new ReadOnlyMemory<byte>(buffer, offset, count), cancellationToken).AsTask();

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        => _outputChannel.WriteAsync(buffer, cancellationToken);

    public override int Read(Span<byte> buffer) => _inputPipeStream.Read(buffer);

    public override int Read(byte[] buffer, int offset, int count) => _inputPipeStream.Read(buffer, offset, count);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => _inputPipeStream.ReadAsync(buffer, cancellationToken);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => _inputPipeStream.ReadAsync(buffer, offset, count, cancellationToken);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (!LeaveOpen)
            {
                _outputChannel.Dispose();
            }

            _disposed = true;
        }
    }

    public override ValueTask DisposeAsync()
    {
        if (_disposed)
            return ValueTask.CompletedTask;

        _disposed = true;

        if (!LeaveOpen)
        {
            _outputChannel.Dispose();
        }
        return ValueTask.CompletedTask;
    }
}
