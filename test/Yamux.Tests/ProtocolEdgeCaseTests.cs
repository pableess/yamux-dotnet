using AwesomeAssertions;
using Nerdbank.Streams;
using System.ComponentModel.DataAnnotations;
using System.IO.Pipelines;
using Yamux.Internal;
using Yamux.Protocol;

namespace Yamux.Tests;

public class ProtocolEdgeCaseTests
{
    [Fact]
    public void FrameHeader_SmallBuffer_Throws()
    {
        byte[] buffer = new byte[FrameHeader.FrameHeaderSize - 1];

        Action act = () => FrameHeader.Parse(buffer);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void FrameHeader_Write_SmallBuffer_Throws()
    {
        var header = new FrameHeader(ProtocolVersion.Initial, FrameType.Data, Flags.None, 1, 0);
        byte[] buffer = new byte[FrameHeader.FrameHeaderSize - 1];

        Action act = () => header.WriteTo(buffer);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Frame_CreateDataFrame_ZeroPayload()
    {
        var frame = Frame.CreateDataFrame(1, Flags.SYN, ReadOnlyMemory<byte>.Empty);
        frame.Header.Length.Should().Be(0);
        frame.Payload.IsEmpty.Should().BeTrue();
        frame.Header.Flags.Should().Be(Flags.SYN);
    }

    [Fact]
    public void Frame_GoAway_TerminationCodes()
    {
        foreach (SessionTermination code in Enum.GetValues<SessionTermination>())
        {
            var frame = Frame.CreateGoAwayFrame(code);
            frame.Header.Length.Should().Be((uint)code);
            frame.Header.FrameType.Should().Be(FrameType.GoAway);
            frame.Header.StreamId.Should().Be(0);
        }
    }

    [Fact]
    public void Frame_PingRequest_OpaqueValue()
    {
        uint value = 0xDEADBEEF;
        var frame = Frame.CreatePingRequestFrame(value);
        frame.Header.Length.Should().Be(value);
        frame.Header.Flags.Should().Be(Flags.SYN);
        frame.Header.StreamId.Should().Be(0);
    }

    [Fact]
    public void Frame_PingResponse_EchoesLength()
    {
        var request = new FrameHeader(ProtocolVersion.Initial, FrameType.Ping, Flags.SYN, 0, 0x12345678);
        var response = Frame.CreatePingResponseFrame(request);
        response.Header.Length.Should().Be(0x12345678);
        response.Header.Flags.Should().Be(Flags.ACK);
    }

    [Fact]
    public void Frame_Dispose_BufferOwner()
    {
        var disposed = false;
        var owner = new DelegateDisposable(() => disposed = true);
        var frame = Frame.CreateDataFrame(1, Flags.None, new byte[4], owner);
        frame.Dispose();
        disposed.Should().BeTrue();
    }

    [Fact]
    public void FrameHeaderRoundtrip_AllFrameTypes()
    {
        foreach (FrameType type in new[] { FrameType.Data, FrameType.WindowUpdate, FrameType.Ping, FrameType.GoAway })
        {
            var original = new FrameHeader(ProtocolVersion.Initial, type, Flags.None, 42, 128);
            byte[] buffer = new byte[FrameHeader.FrameHeaderSize];
            original.WriteTo(buffer);
            var parsed = FrameHeader.Parse(buffer);
            parsed.FrameType.Should().Be(type);
        }
    }

    private sealed class DelegateDisposable : IDisposable
    {
        private readonly Action _onDispose;
        public DelegateDisposable(Action onDispose) => _onDispose = onDispose;
        public void Dispose() => _onDispose();
    }

    [Fact]
    public async Task StreamIdGenerator_Exhaustion()
    {
        var gen = new StreamIdGenerator(false);
        var seen = new HashSet<uint>();

        for (int i = 0; i < 1000; i++)
        {
            var id = gen.Next();
            seen.Add(id).Should().BeTrue();
            (id % 2).Should().Be(1);
        }
    }

    [Fact]
    public void SessionChannelOptions_Validate_MinWindow()
    {
        var opts = new SessionChannelOptions { ReceiveWindowSize = 5 * 1024 };
        Action act = () => opts.Validate();
        act.Should().Throw<ValidationException>();
    }

    [Fact]
    public void SessionChannelOptions_Validate_AutoTuneBounds()
    {
        var opts = new SessionChannelOptions
        {
            ReceiveWindowSize = 16 * 1024 * 1024,
            ReceiveWindowUpperBound = 8 * 1024 * 1024,
            AutoTuneReceiveWindowSize = true,
        };
        Action act = () => opts.Validate();
        act.Should().Throw<ValidationException>();
    }

    [Fact]
    public void SessionChannelOptions_Validate_ZeroMaxDataFrame()
    {
        var opts = new SessionChannelOptions { MaxDataFrameSize = 0 };
        Action act = () => opts.Validate();
        act.Should().Throw<ValidationException>();
    }

    [Fact]
    public async Task PingAsync_ReturnsRtt()
    {
        (var client, var server) = FullDuplexStream.CreatePair();

        var serverTask = Task.Run(async () =>
        {
            await using var session = new Session(new StreamPeer(server), false);
            session.Start();
            await Task.Delay(5000, TestContext.Current.CancellationToken);
        }, TestContext.Current.CancellationToken);

        var clientTask = Task.Run(async () =>
        {
            await using var session = new Session(new StreamPeer(client), true);
            session.Start();

            var rtt = await session.PingAsync(TestContext.Current.CancellationToken);

            rtt.TotalMilliseconds.Should().BeGreaterThan(0);
            rtt.TotalMilliseconds.Should().BeLessThan(5000);
        }, TestContext.Current.CancellationToken);

        await Task.WhenAll(serverTask, clientTask);
    }

    [Fact]
    public async Task WriteAfterClose_Throws()
    {
        (var client, var server) = FullDuplexStream.CreatePair();

        var serverTask = Task.Run(async () =>
        {
            await using var session = new Session(new StreamPeer(server), false);
            session.Start();
            using var channel = await session.AcceptAsync(TestContext.Current.CancellationToken);

            ReadResult res;
            do
            {
                res = await channel.Input.ReadAsync(TestContext.Current.CancellationToken);
                channel.Input.AdvanceTo(res.Buffer.End, res.Buffer.End);
            } while (!res.IsCanceled && !res.IsCompleted);
        }, TestContext.Current.CancellationToken);

        await using var clientSession = new Session(new StreamPeer(client), true);
        clientSession.Start();
        using var channel = await clientSession.OpenChannelAsync(cancellationToken: TestContext.Current.CancellationToken);

        await channel.WriteAsync(new byte[32], TestContext.Current.CancellationToken);
        channel.Close();

        Func<Task> writeAfterClose = async () => await channel.WriteAsync(new byte[1], TestContext.Current.CancellationToken);
        await writeAfterClose.Should().ThrowAsync<SessionChannelException>();

        await Task.WhenAll(serverTask);
    }

    [Fact]
    public async Task MaxChannels_Enforced()
    {
        (var client, var server) = FullDuplexStream.CreatePair();

        var serverTask = Task.Run(async () =>
        {
            await using var session = new Session(new StreamPeer(server), false, options: new SessionOptions { MaxChannels = 2 });
            session.Start();

            using var c1 = await session.AcceptAsync(TestContext.Current.CancellationToken);
            using var c2 = await session.AcceptAsync(TestContext.Current.CancellationToken);

            await Task.Delay(2000, TestContext.Current.CancellationToken);
        }, TestContext.Current.CancellationToken);

        await using var clientSession = new Session(new StreamPeer(client), true, options: new SessionOptions { MaxChannels = 2 });
        clientSession.Start();

        using var ch1 = await clientSession.OpenChannelAsync(cancellationToken: TestContext.Current.CancellationToken);
        using var ch2 = await clientSession.OpenChannelAsync(cancellationToken: TestContext.Current.CancellationToken);

        await Task.Delay(500, TestContext.Current.CancellationToken);

        ch1.Close();
        ch2.Close();

        await Task.WhenAll(serverTask);
    }
}