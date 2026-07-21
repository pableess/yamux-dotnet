using AwesomeAssertions;
using Nerdbank.Streams;
using System.Collections.Concurrent;
using System.IO.Pipelines;
using Xunit;

namespace Yamux.Tests;

public class StressTests
{
    [Fact]
    public async Task ManyChannels_Concurrent()
    {
        var channels = 200;
        (var client, var server) = FullDuplexStream.CreatePair();

var serverTask = Task.Run(async () =>
        {
            await using var session = new Session(new StreamPeer(server), false);
            session.Start();

            var tasks = new List<Task>();
            for (int i = 0; i < channels; i++)
            {
                tasks.Add(Task.Run(async () =>
                {
                    using var channel = await session.AcceptAsync();
                    try
                    {
                        ReadResult res;
                        do
                        {
res = await channel.Input.ReadAsync(TestContext.Current.CancellationToken);
                            channel.Input.AdvanceTo(res.Buffer.End, res.Buffer.End);
                        } while (!res.IsCanceled && !res.IsCompleted);
                    }
                    catch
                    {
                    }
                }, TestContext.Current.CancellationToken));
            }

            await Task.WhenAll(tasks);
        }, TestContext.Current.CancellationToken);

        var clientTask = Task.Run(async () =>
        {
            await using var session = new Session(new StreamPeer(client), true);
            session.Start();

            var tasks = new List<Task>();
            for (int i = 0; i < channels; i++)
            {
                tasks.Add(Task.Run(async () =>
                {
                    using var channel = await session.OpenChannelAsync();
                    await channel.WriteAsync(new byte[64], TestContext.Current.CancellationToken);
                    channel.Close();
                    await channel.WhenRemoteCloseAsync(TimeSpan.FromSeconds(1));
                }, TestContext.Current.CancellationToken));
            }

            await Task.WhenAll(tasks);
        }, TestContext.Current.CancellationToken);

        await Task.WhenAll(serverTask, clientTask);
    }

    [Fact]
    public async Task RapidOpenClose()
    {
        (var client, var server) = FullDuplexStream.CreatePair();
        var iterations = 50;

var serverTask = Task.Run(async () =>
        {
            await using var session = new Session(new StreamPeer(server), false);
            session.Start();

            for (int i = 0; i < iterations; i++)
            {
                using var channel = await session.AcceptAsync();
                try
                {
                    ReadResult res;
                    do
                    {
res = await channel.Input.ReadAsync(TestContext.Current.CancellationToken);
                    channel.Input.AdvanceTo(res.Buffer.End, res.Buffer.End);
                } while (!res.IsCanceled && !res.IsCompleted);
                }
                catch
                {
                }
            }
        }, TestContext.Current.CancellationToken);

        var clientTask = Task.Run(async () =>
        {
            await using var session = new Session(new StreamPeer(client), true);
            session.Start();

            for (int i = 0; i < iterations; i++)
            {
                using var channel = await session.OpenChannelAsync();
                await channel.WriteAsync(new byte[16], TestContext.Current.CancellationToken);
                channel.Close();
                await channel.WhenRemoteCloseAsync(TimeSpan.FromSeconds(3));
            }
        }, TestContext.Current.CancellationToken);

        await Task.WhenAll(serverTask, clientTask);
    }

    [Fact]
    public async Task ConcurrentReadWrite_SameChannel()
    {
        (var client, var server) = FullDuplexStream.CreatePair();
        var dataSize = 1024 * 64;
        var data = new byte[dataSize];
        new Random().NextBytes(data);

        byte[]? serverReceived = null;
        byte[]? clientReceived = null;

        var serverTask = Task.Run(async () =>
        {
            await using var session = new Session(new StreamPeer(server), false);
            session.Start();
            using var channel = await session.AcceptAsync();

            await channel.WriteAsync(data, TestContext.Current.CancellationToken);
            channel.Close();

            var ms = new MemoryStream();
            await channel.Input.CopyToAsync(ms, TestContext.Current.CancellationToken);
            serverReceived = ms.ToArray();

            await channel.WhenRemoteCloseAsync(TimeSpan.FromSeconds(3));
        }, TestContext.Current.CancellationToken);

        var clientTask = Task.Run(async () =>
        {
            await using var session = new Session(new StreamPeer(client), true);
            session.Start();
            using var channel = await session.OpenChannelAsync();

            await channel.WriteAsync(data, TestContext.Current.CancellationToken);
            channel.Close();

            var ms = new MemoryStream();
            await channel.Input.CopyToAsync(ms, TestContext.Current.CancellationToken);
            clientReceived = ms.ToArray();

            await channel.WhenRemoteCloseAsync(TimeSpan.FromSeconds(3));
        }, TestContext.Current.CancellationToken);

        await Task.WhenAll(serverTask, clientTask);
        serverReceived.Should().NotBeNull();
        serverReceived.Should().BeEquivalentTo(data);
        clientReceived.Should().NotBeNull();
        clientReceived.Should().BeEquivalentTo(data);
    }

    [Fact]
    public async Task StreamTest_AsStream_Concurrent()
    {
        (var client, var server) = FullDuplexStream.CreatePair();
        var data = new byte[1024 * 32];
        new Random().NextBytes(data);
        byte[]? result = null;

        var serverTask = Task.Run(async () =>
        {
            await using var session = new Session(new StreamPeer(server), false);
            session.Start();
            using var channel = await session.AcceptAsync();
            using var stream = channel.AsStream();

            using var ms = new MemoryStream(data);
            await ms.CopyToAsync(stream, TestContext.Current.CancellationToken);
            channel.Close();
            await channel.WhenRemoteCloseAsync(TimeSpan.FromSeconds(1));
        }, TestContext.Current.CancellationToken);

        var clientTask = Task.Run(async () =>
        {
            await using var session = new Session(new StreamPeer(client), true);
            session.Start();
            using var channel = await session.OpenChannelAsync();
            using var stream = channel.AsStream();

            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms, TestContext.Current.CancellationToken);
            result = ms.ToArray();
        }, TestContext.Current.CancellationToken);

        await Task.WhenAll(serverTask, clientTask);
        result.Should().NotBeNull();
        result.Should().BeEquivalentTo(data);
    }
}