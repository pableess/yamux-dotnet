using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using AwesomeAssertions;
using Nerdbank.Streams;
using Yamux.Protocol;

namespace Yamux.Tests;

public class ErrorPathTests
{
    [Fact]
    public async Task Session_DoubleDispose_NoOp()
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

        await using var session = new Session(new StreamPeer(client), true);
        session.Start();
        using var channel = await session.OpenChannelAsync(cancellationToken: TestContext.Current.CancellationToken);
        
        await channel.WriteAsync(new byte[64], TestContext.Current.CancellationToken);
        channel.Close();
        await channel.WhenRemoteCloseAsync(TimeSpan.FromSeconds(1));

        await session.DisposeAsync();
        await session.DisposeAsync();

        await Task.WhenAll(serverTask);
    }

    [Fact]
    public async Task Channel_DoubleDispose_NoOp()
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

        await using var session = new Session(new StreamPeer(client), true);
        session.Start();
        var channel = await session.OpenChannelAsync(cancellationToken: TestContext.Current.CancellationToken);

        await channel.WriteAsync(new byte[32], TestContext.Current.CancellationToken);
        channel.Close();
        await channel.WhenRemoteCloseAsync(TimeSpan.FromSeconds(1));

        channel.Dispose();
        channel.Dispose();

        await Task.WhenAll(serverTask);
    }

    [Fact]
    public async Task GoAway_WhileChannelsActive()
    {
        (var client, var server) = FullDuplexStream.CreatePair();

        var serverTask = Task.Run(async () =>
        {
            await using var session = new Session(new StreamPeer(server), false);
            session.Start();
            using var channel = await session.AcceptAsync(TestContext.Current.CancellationToken);

            await session.GoAwayAsync();

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
        }, TestContext.Current.CancellationToken);

        var clientTask = Task.Run(async () =>
        {
            await using var session = new Session(new StreamPeer(client), true);
            session.Start();
            using var channel = await session.OpenChannelAsync(cancellationToken: TestContext.Current.CancellationToken);

            await channel.WriteAsync(new byte[64], TestContext.Current.CancellationToken);
            await Task.Delay(500, TestContext.Current.CancellationToken);

            channel.Close();
            await channel.WhenRemoteCloseAsync(TimeSpan.FromSeconds(1));
        }, TestContext.Current.CancellationToken);

        await Task.WhenAll(serverTask, clientTask);
    }

    [Fact]
    public async Task ConcurrentCloseAndWrite()
    {
        (var client, var server) = FullDuplexStream.CreatePair();

        var serverTask = Task.Run(async () =>
        {
            await using var session = new Session(new StreamPeer(server), false);
            session.Start();

            try
            {
                using var channel = await session.AcceptAsync(TestContext.Current.CancellationToken);
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
        }, TestContext.Current.CancellationToken);

        var clientTask = Task.Run(async () =>
        {
            await using var session = new Session(new StreamPeer(client), true);
            session.Start();

            var channel = await session.OpenChannelAsync(cancellationToken: TestContext.Current.CancellationToken);
            var tasks = new List<Task>();
            for (int i = 0; i < 10; i++)
            {
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        await channel.WriteAsync(new byte[1024], TestContext.Current.CancellationToken);
                    }
                    catch
                    {
                    }
                }, TestContext.Current.CancellationToken));
            }

            await Task.Delay(100, TestContext.Current.CancellationToken);
            channel.Abort();
            await Task.WhenAll(tasks);
        }, TestContext.Current.CancellationToken);

        await Task.WhenAll(serverTask, clientTask);
    }

    [Fact]
    public async Task FaultedSession_PropagatesToAccept()
    {
        (var client, var server) = FullDuplexStream.CreatePair();

        var serverTask = Task.Run(async () =>
        {
            await using var session = new Session(new StreamPeer(server), false);
            session.Start();
            IDuplexSessionChannel? channel = null;
            try
            {
                channel = await session.AcceptAsync(TestContext.Current.CancellationToken);
            }
            catch (SessionException)
            {
                return;
            }

            using (channel)
            {
                try
                {
                    ReadResult res;
                    do
                    {
                        res = await channel.Input.ReadAsync(TestContext.Current.CancellationToken);
                        channel.Input.AdvanceTo(res.Buffer.End, res.Buffer.End);
                    } while (!res.IsCanceled && !res.IsCompleted);
                }
                catch (YamuxException)
                {
                }
            }
        }, TestContext.Current.CancellationToken);

        var clientTask = Task.Run(async () =>
        {
await using var session = new Session(new StreamPeer(client), true);
            session.Start();
            using var channel = await session.OpenChannelAsync(cancellationToken: TestContext.Current.CancellationToken);

            await channel.WriteAsync(new byte[1024], TestContext.Current.CancellationToken);

            await session.GoAwayAsync(SessionTermination.ProtocolError);

            try
            {
                await channel.WriteAsync(new byte[1024], TestContext.Current.CancellationToken);
            }
            catch (SessionChannelException)
            {
            }
        }, TestContext.Current.CancellationToken);

        await Task.WhenAll(serverTask, clientTask);
    }
}