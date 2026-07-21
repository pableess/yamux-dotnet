using AwesomeAssertions;
using Bogus;
using System.Buffers;
using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Yamux.Tests;

public class SocketTransportTests
{
    [Fact]
    public async Task SingleOneWayTcpTest()
    {
        var faker = new Faker();
        var data = faker.Random.Chars(count: 1024 * 256);
        var buffer = Encoding.UTF8.GetBytes(data).AsMemory();
        var result = new byte[buffer.Length].AsMemory();

        using var listener = new Socket(SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen();
        var port = ((IPEndPoint)listener.LocalEndPoint!).Port;

        var serverTask = Task.Run(async () =>
        {
            using var clientSocket = await listener.AcceptAsync(TestContext.Current.CancellationToken);
            await using var session = clientSocket.AsYamuxSession(false);
            session.Start();

            using var channel = await session.AcceptAsync(TestContext.Current.CancellationToken);

            long index = 0;
            ReadResult res;
            do
            {
                res = await channel.Input.ReadAsync(TestContext.Current.CancellationToken);
                if (res.Buffer.Length > 0)
                {
                    res.Buffer.CopyTo(result.Slice((int)index, (int)res.Buffer.Length).Span);
                    index += res.Buffer.Length;
                    channel.Input.AdvanceTo(res.Buffer.End, res.Buffer.End);
                }
            } while (!res.IsCanceled && !res.IsCompleted);

            channel.Close();
            await channel.WhenRemoteCloseAsync(TimeSpan.FromSeconds(1));
            channel.Dispose();
        }, TestContext.Current.CancellationToken);

        var clientTask = Task.Run(async () =>
        {
            using var sock = new Socket(SocketType.Stream, ProtocolType.Tcp);
            await sock.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port), TestContext.Current.CancellationToken);

            await using var session = sock.AsYamuxSession(true);
            session.Start();

            using var channel = await session.OpenChannelAsync(cancellationToken: TestContext.Current.CancellationToken);
            await channel.WriteAsync(buffer, TestContext.Current.CancellationToken);
            channel.Close();
            await channel.WhenRemoteCloseAsync(TimeSpan.FromSeconds(1));
        }, TestContext.Current.CancellationToken);

        await Task.WhenAll(serverTask, clientTask);
        result.ToArray().Should().BeEquivalentTo(buffer.ToArray());
    }

    [Fact]
    public async Task MultipleChannelsTcpTest()
    {
        var faker = new Faker();
        var data = faker.Random.Bytes(1024 * 128);
        var channels = 10;
        var results = new ConcurrentDictionary<uint, byte[]>();

        using var listener = new Socket(SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen();
        var port = ((IPEndPoint)listener.LocalEndPoint!).Port;

        var serverTask = Task.Run(async () =>
        {
            using var clientSocket = await listener.AcceptAsync(TestContext.Current.CancellationToken);
            await using var session = clientSocket.AsYamuxSession(false);
            session.Start();

            var tasks = new List<Task>();
            for (int i = 0; i < channels; i++)
            {
                tasks.Add(Task.Run(async () =>
                {
                    using var channel = await session.AcceptAsync(TestContext.Current.CancellationToken);
                    var ms = new MemoryStream();
                    await channel.Input.CopyToAsync(ms);
                    results[channel.Id] = ms.ToArray();
                    channel.Close();
                    await channel.WhenRemoteCloseAsync(TimeSpan.FromSeconds(3));
                    channel.Dispose();
                }, TestContext.Current.CancellationToken));
            }

            await Task.WhenAll(tasks);
        }, TestContext.Current.CancellationToken);

        var clientTask = Task.Run(async () =>
        {
            using var sock = new Socket(SocketType.Stream, ProtocolType.Tcp);
            await sock.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port), TestContext.Current.CancellationToken);

            await using var session = sock.AsYamuxSession(true);
            session.Start();

            var tasks = new List<Task>();
            for (int i = 0; i < channels; i++)
            {
                tasks.Add(Task.Run(async () =>
                {
                    using var channel = await session.OpenChannelAsync(cancellationToken: TestContext.Current.CancellationToken);
                    await channel.WriteAsync(data, TestContext.Current.CancellationToken);
                    channel.Close();
                    await channel.WhenRemoteCloseAsync(TimeSpan.FromSeconds(3));
                }, TestContext.Current.CancellationToken));
            }

            await Task.WhenAll(tasks);
        }, TestContext.Current.CancellationToken);

        await Task.WhenAll(serverTask, clientTask);

        results.Count.Should().Be(channels);
        foreach (var kv in results)
        {
            kv.Value.Should().BeEquivalentTo(data);
        }
    }

    [Fact]
    public async Task ClientDisconnectDetected()
    {
        using var listener = new Socket(SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen();
        var port = ((IPEndPoint)listener.LocalEndPoint!).Port;

        bool errorDetected = false;

var serverTask = Task.Run(async () =>
        {
            try
            {
                using var clientSocket = await listener.AcceptAsync(TestContext.Current.CancellationToken);
                await using var session = clientSocket.AsYamuxSession(false);
                session.Start();

                using var channel = await session.AcceptAsync(TestContext.Current.CancellationToken);

                try
                {
                    ReadResult res;
                    do
                    {
res = await channel.Input.ReadAsync(TestContext.Current.CancellationToken);
                        if (res.IsCompleted)
                        {
                            break;
                        }
                        channel.Input.AdvanceTo(res.Buffer.End, res.Buffer.End);
                    } while (!res.IsCanceled && !res.IsCompleted);
                    errorDetected = true;
                }
                catch (SessionException ex) when (ex.ErrorCode == SessionErrorCode.StreamClosed)
                {
                    errorDetected = true;
                }
            }
            catch
            {
            }
        }, TestContext.Current.CancellationToken);

        var clientTask = Task.Run(async () =>
        {
            using var sock = new Socket(SocketType.Stream, ProtocolType.Tcp);
            await sock.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port), TestContext.Current.CancellationToken);

            var session = sock.AsYamuxSession(true);
            session.Start();

            var channel = await session.OpenChannelAsync(cancellationToken: TestContext.Current.CancellationToken);
            await channel.WriteAsync(new byte[1024], TestContext.Current.CancellationToken);
            await Task.Delay(200, TestContext.Current.CancellationToken);

            sock.Close();
        }, TestContext.Current.CancellationToken);

        await Task.WhenAll(serverTask, clientTask);
        errorDetected.Should().BeTrue();
    }

    [Fact]
    public async Task BidirectionalTcpTest()
    {
        var clientData = new Faker().Random.Bytes(1024 * 64);
        var serverData = new Faker().Random.Bytes(1024 * 64);

        byte[]? serverReceived = null;
        byte[]? clientReceived = null;

        using var listener = new Socket(SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen();
        var port = ((IPEndPoint)listener.LocalEndPoint!).Port;

        var serverTask = Task.Run(async () =>
        {
            using var clientSocket = await listener.AcceptAsync(TestContext.Current.CancellationToken);
            await using var session = clientSocket.AsYamuxSession(false);
            session.Start();
            using var channel = await session.AcceptAsync(TestContext.Current.CancellationToken);

            await channel.WriteAsync(serverData, TestContext.Current.CancellationToken);
            channel.Close();

            var ms = new MemoryStream();
            await channel.Input.CopyToAsync(ms);
            serverReceived = ms.ToArray();

            await channel.WhenRemoteCloseAsync(TimeSpan.FromSeconds(1));
            channel.Dispose();
        }, TestContext.Current.CancellationToken);

        var clientTask = Task.Run(async () =>
        {
            using var sock = new Socket(SocketType.Stream, ProtocolType.Tcp);
            await sock.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port), TestContext.Current.CancellationToken);

            await using var session = sock.AsYamuxSession(true);
            session.Start();
            using var channel = await session.OpenChannelAsync(cancellationToken: TestContext.Current.CancellationToken);

            await channel.WriteAsync(clientData, TestContext.Current.CancellationToken);
            channel.Close();

            var ms = new MemoryStream();
            await channel.Input.CopyToAsync(ms);
            clientReceived = ms.ToArray();

            await channel.WhenRemoteCloseAsync(TimeSpan.FromSeconds(1));
            channel.Dispose();
        }, TestContext.Current.CancellationToken);

        await Task.WhenAll(serverTask, clientTask);

        clientReceived.Should().NotBeNull();
        serverReceived.Should().NotBeNull();
        clientReceived.Should().BeEquivalentTo(serverData);
        serverReceived.Should().BeEquivalentTo(clientData);
    }
}