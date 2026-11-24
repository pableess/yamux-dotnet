using System.IO.Pipelines;
using System.Reflection;
using System.Text;
using AwesomeAssertions;
using Yamux.Protocol;

namespace Yamux.Tests;

/// <summary>
/// Test-only transport plumbing that lets a test act as a raw yamux peer.
/// </summary>
internal sealed class RawDuplexPipe(PipeReader input, PipeWriter output) : IDuplexPipe
{
    public PipeReader Input { get; } = input;
    public PipeWriter Output { get; } = output;
}

internal sealed class RawYamuxPeer : IAsyncDisposable
{
    private readonly Pipe _toSession = new();
    private readonly Pipe _fromSession = new();
    private readonly Session _session;

    public RawYamuxPeer(bool isClient, SessionOptions? options = null)
    {
        options ??= new SessionOptions();
        options.EnableKeepAlive = false;

        _session = new Session(
            new PipePeer(new RawDuplexPipe(_toSession.Reader, _fromSession.Writer)),
            isClient,
            leaveOpen: true,
            options);
        _session.Start();
    }

    public Session Session => _session;
    public PipeWriter Write => _toSession.Writer;
    public PipeReader Read => _fromSession.Reader;

    public async ValueTask DisposeAsync()
    {
        await _session.DisposeAsync();
        _toSession.Writer.Complete();
        _fromSession.Reader.Complete();
    }
}

internal static class Wire
{
    public static async Task WriteFrameAsync(
        PipeWriter writer,
        FrameType type,
        Flags flags,
        uint streamId,
        uint length,
        ReadOnlyMemory<byte> payload = default,
        CancellationToken cancellationToken = default)
    {
        var header = new FrameHeader(ProtocolVersion.Initial, type, flags, streamId, length);
        var span = writer.GetSpan(FrameHeader.FrameHeaderSize);
        header.WriteTo(span);
        writer.Advance(FrameHeader.FrameHeaderSize);

        if (!payload.IsEmpty)
        {
            var destination = writer.GetSpan(payload.Length);
            payload.Span.CopyTo(destination);
            writer.Advance(payload.Length);
        }

        await writer.FlushAsync(cancellationToken);
    }

    public static async Task<FrameHeader> ReadFrameHeaderAsync(PipeReader reader, CancellationToken cancellationToken)
    {
        while (true)
        {
            var result = await reader.ReadAsync(cancellationToken);
            try
            {
                if (result.Buffer.Length >= FrameHeader.FrameHeaderSize)
                {
                    Span<byte> headerBytes = stackalloc byte[FrameHeader.FrameHeaderSize];
                    result.Buffer.Slice(0, FrameHeader.FrameHeaderSize).FirstSpan.CopyTo(headerBytes);
                    var header = FrameHeader.Parse(headerBytes);
                    reader.AdvanceTo(result.Buffer.GetPosition(FrameHeader.FrameHeaderSize));
                    return header;
                }
            }
            finally
            {
                if (!result.Buffer.IsEmpty && result.Buffer.Length < FrameHeader.FrameHeaderSize)
                {
                    reader.AdvanceTo(result.Buffer.Start, result.Buffer.End);
                }
            }
        }
    }

    public static async Task WaitForSessionClosedAsync(Session session, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        while (!session.IsClosed)
        {
            await Task.Delay(10, cts.Token);
        }
    }
}

public class ProtocolRobustnessTests
{
    private static readonly CancellationToken CancellationToken = TestContext.Current.CancellationToken;

    [Fact]
    public async Task DataFrameWithFin_DeliversPayload()
    {
        await using var peer = new RawYamuxPeer(isClient: false);

        await Wire.WriteFrameAsync(peer.Write, FrameType.WindowUpdate, Flags.SYN, 1, 0, cancellationToken: CancellationToken);
        var payload = Encoding.ASCII.GetBytes("hello");
        await Wire.WriteFrameAsync(peer.Write, FrameType.Data, Flags.FIN, 1, (uint)payload.Length, payload, CancellationToken);

        using var channel = await peer.Session.AcceptAsync(CancellationToken);
        using var result = new MemoryStream();
        await channel.Input.CopyToAsync(result, CancellationToken);

        Encoding.ASCII.GetString(result.ToArray()).Should().Be("hello");
    }

    [Fact]
    public async Task DataFrameForUnknownStream_DoesNotConsumeNextFrame()
    {
        await using var peer = new RawYamuxPeer(isClient: false);

        // Small payload: the old discard path read a full 4 KiB block and ate
        // the following frames.
        await Wire.WriteFrameAsync(peer.Write, FrameType.Data, Flags.None, 99, 4, new byte[4], CancellationToken);

        await Wire.WriteFrameAsync(peer.Write, FrameType.WindowUpdate, Flags.SYN, 1, 0, cancellationToken: CancellationToken);
        var payload = Encoding.ASCII.GetBytes("ok");
        await Wire.WriteFrameAsync(peer.Write, FrameType.Data, Flags.FIN, 1, (uint)payload.Length, payload, CancellationToken);

        using var channel = await peer.Session.AcceptAsync(CancellationToken);
        using var result = new MemoryStream();
        await channel.Input.CopyToAsync(result, CancellationToken);

        Encoding.ASCII.GetString(result.ToArray()).Should().Be("ok");
    }

    [Fact]
    public async Task ZeroLengthDataFrame_DoesNotCloseSession()
    {
        await using var peer = new RawYamuxPeer(isClient: false);

        await Wire.WriteFrameAsync(peer.Write, FrameType.WindowUpdate, Flags.SYN, 1, 0, cancellationToken: CancellationToken);
        await Wire.WriteFrameAsync(peer.Write, FrameType.Data, Flags.None, 1, 0, cancellationToken: CancellationToken);

        var payload = Encoding.ASCII.GetBytes("ok");
        await Wire.WriteFrameAsync(peer.Write, FrameType.Data, Flags.FIN, 1, (uint)payload.Length, payload, CancellationToken);

        using var channel = await peer.Session.AcceptAsync(CancellationToken);
        using var result = new MemoryStream();
        await channel.Input.CopyToAsync(result, CancellationToken);

        Encoding.ASCII.GetString(result.ToArray()).Should().Be("ok");
        peer.Session.IsClosed.Should().BeFalse();
    }

    [Fact]
    public async Task ReceiveWindowExceeded_ClosesSession()
    {
        await using var peer = new RawYamuxPeer(isClient: false);

        await Wire.WriteFrameAsync(peer.Write, FrameType.WindowUpdate, Flags.SYN, 1, 0, cancellationToken: CancellationToken);

        // Initial window is 256 KiB. Announce (but do not send) one byte more.
        await Wire.WriteFrameAsync(peer.Write, FrameType.Data, Flags.None, 1, (256 * 1024) + 1, cancellationToken: CancellationToken);

        await Wire.WaitForSessionClosedAsync(peer.Session, TimeSpan.FromSeconds(5), CancellationToken);
        peer.Session.IsClosed.Should().BeTrue();
    }

    [Fact]
    public async Task RemoteResetBeforeAck_FailsOpenPromptly()
    {
        await using var peer = new RawYamuxPeer(isClient: true);

        var open = peer.Session.OpenChannelAsync(waitForAcknowledgement: true, cancellationToken: CancellationToken).AsTask();

        var syn = await Wire.ReadFrameHeaderAsync(peer.Read, CancellationToken);
        syn.FrameType.Should().Be(FrameType.WindowUpdate);
        syn.Flags.HasFlag(Flags.SYN).Should().BeTrue();

        await Wire.WriteFrameAsync(peer.Write, FrameType.WindowUpdate, Flags.RST, syn.StreamId, 0, cancellationToken: CancellationToken);

        Func<Task> act = () => open;

        (await act.Should().ThrowAsync<SessionChannelException>()).Which.ErrorCode.Should().Be(ChannelErrorCode.ChannelRejected);
        peer.Session.IsClosed.Should().BeFalse();
    }

    [Fact]
    public async Task ResetChannelInBacklog_IsSkippedByAccept()
    {
        await using var peer = new RawYamuxPeer(isClient: false);

        await Wire.WriteFrameAsync(peer.Write, FrameType.WindowUpdate, Flags.SYN, 1, 0, cancellationToken: CancellationToken);
        await Wire.WriteFrameAsync(peer.Write, FrameType.WindowUpdate, Flags.RST, 1, 0, cancellationToken: CancellationToken);

        // Barrier: wait for the ping reply so the RST above has definitely been
        // processed by the read loop before we accept.
        await Wire.WriteFrameAsync(peer.Write, FrameType.Ping, Flags.SYN, 0, 0x1234, cancellationToken: CancellationToken);
        while (true)
        {
            var reply = await Wire.ReadFrameHeaderAsync(peer.Read, CancellationToken);
            if (reply.FrameType == FrameType.Ping && reply.Flags.HasFlag(Flags.ACK) && reply.Length == 0x1234)
            {
                break;
            }
        }

        await Wire.WriteFrameAsync(peer.Write, FrameType.WindowUpdate, Flags.SYN, 2, 0, cancellationToken: CancellationToken);
        var payload = Encoding.ASCII.GetBytes("ok");
        await Wire.WriteFrameAsync(peer.Write, FrameType.Data, Flags.FIN, 2, (uint)payload.Length, payload, CancellationToken);

        using var channel = await peer.Session.AcceptAsync(CancellationToken);
        channel.Id.Should().Be(2);

        using var result = new MemoryStream();
        await channel.Input.CopyToAsync(result, CancellationToken);
        Encoding.ASCII.GetString(result.ToArray()).Should().Be("ok");
    }

    [Fact]
    public async Task CustomInitialWindow_WorksForRemotelyInitiatedChannel()
    {
        (var client, var server) = Nerdbank.Streams.FullDuplexStream.CreatePair();
        var options = new SessionOptions
        {
            EnableKeepAlive = false,
            DefaultChannelOptions = new SessionChannelOptions { ReceiveWindowSize = 512 * 1024 },
        };

        var serverTask = Task.Run(async () =>
        {
            await using var session = new Session(new StreamPeer(server), false, options: options);
            session.Start();
            using var channel = await session.AcceptAsync(CancellationToken);

            using var received = new MemoryStream();
            await channel.Input.CopyToAsync(received, CancellationToken);
            return received.ToArray();
        }, CancellationToken);

        await using var clientSession = new Session(new StreamPeer(client), true, options: options);
        clientSession.Start();
        using var clientChannel = await clientSession.OpenChannelAsync(cancellationToken: CancellationToken);

        var data = Encoding.ASCII.GetBytes(new string('x', 64 * 1024));
        await clientChannel.WriteAsync(data, CancellationToken);
        clientChannel.Close();

        (await serverTask.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken)).Should().BeEquivalentTo(data);
    }

    [Fact]
    public void ReceiveWindowBelowInitial_IsRejected()
    {
        var options = new SessionChannelOptions { ReceiveWindowSize = 128 * 1024 };

        Action act = () => options.Validate();

        act.Should().Throw<System.ComponentModel.DataAnnotations.ValidationException>();
    }

    [Fact]
    public async Task OpenChannelWithInvalidOptions_Throws()
    {
        (var client, _) = Nerdbank.Streams.FullDuplexStream.CreatePair();
        await using var session = new Session(new StreamPeer(client), true, options: new SessionOptions { EnableKeepAlive = false });
        session.Start();

        var invalid = new SessionChannelOptions { MaxDataFrameSize = 0 };

        Func<Task> act = async () => await session.OpenChannelAsync(invalid, cancellationToken: CancellationToken);

        await act.Should().ThrowAsync<System.ComponentModel.DataAnnotations.ValidationException>();
    }

    [Fact]
    public void StreamIdGenerator_ThrowsWhenExhausted()
    {
        var generator = new Yamux.Internal.StreamIdGenerator(even: false);
        var seedField = typeof(Yamux.Internal.StreamIdGenerator)
            .GetField("_seed", BindingFlags.Instance | BindingFlags.NonPublic)!;
        seedField.SetValue(generator, uint.MaxValue - 1);

        Action act = () => generator.Next();

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public async Task DisposeAfterGracefulClose_DoesNotSendReset()
    {
        await using var peer = new RawYamuxPeer(isClient: false);

        await Wire.WriteFrameAsync(peer.Write, FrameType.WindowUpdate, Flags.SYN, 1, 0, cancellationToken: CancellationToken);
        using var channel = await peer.Session.AcceptAsync(CancellationToken);

        var ack = await Wire.ReadFrameHeaderAsync(peer.Read, CancellationToken);
        ack.Flags.HasFlag(Flags.ACK).Should().BeTrue();

        channel.Close();
        var fin = await ReadUntilAsync(peer.Read, h => h.StreamId == 1 && h.Flags.HasFlag(Flags.FIN));
        fin.Flags.HasFlag(Flags.FIN).Should().BeTrue();

        await Wire.WriteFrameAsync(peer.Write, FrameType.WindowUpdate, Flags.FIN, 1, 0, cancellationToken: CancellationToken);
        (await channel.WhenRemoteCloseAsync(TimeSpan.FromSeconds(2))).Should().BeTrue();

        channel.Dispose();

        // Barrier: the ping reply is enqueued after any frame Dispose produced.
        await Wire.WriteFrameAsync(peer.Write, FrameType.Ping, Flags.SYN, 0, 0x55, cancellationToken: CancellationToken);
        while (true)
        {
            var header = await Wire.ReadFrameHeaderAsync(peer.Read, CancellationToken);
            if (header.FrameType == FrameType.Ping && header.Flags.HasFlag(Flags.ACK) && header.Length == 0x55)
            {
                break;
            }

            header.Flags.HasFlag(Flags.RST).Should().BeFalse("a gracefully closed channel must not be reset on dispose");
        }
    }

    [Fact]
    public async Task WriteAfterDispose_ThrowsSessionChannelException()
    {
        await using var peer = new RawYamuxPeer(isClient: true);
        var channel = await peer.Session.OpenChannelAsync(cancellationToken: CancellationToken);

        channel.Dispose();

        Func<Task> write = async () => await channel.WriteAsync(new byte[1], CancellationToken);

        await write.Should().ThrowAsync<SessionChannelException>();
    }

    private static async Task<FrameHeader> ReadUntilAsync(
        PipeReader reader, Func<FrameHeader, bool> predicate)
    {
        while (true)
        {
            var header = await Wire.ReadFrameHeaderAsync(reader, CancellationToken);
            if (predicate(header))
            {
                return header;
            }
        }
    }
}
