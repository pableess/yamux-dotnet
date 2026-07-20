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
            using var channel = await session.AcceptAsync();
            
            ReadResult res;
            do
            {
                res = await channel.Input.ReadAsync();
                channel.Input.AdvanceTo(res.Buffer.End, res.Buffer.End);
            } while (!res.IsCanceled && !res.IsCompleted);
        });

await using var session = new Session(new StreamPeer(client), true);
        session.Start();
        using var channel = await session.OpenChannelAsync();
        
        await channel.WriteAsync(new byte[64]);
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
            using var channel = await session.AcceptAsync();

            ReadResult res;
            do
            {
                res = await channel.Input.ReadAsync();
                channel.Input.AdvanceTo(res.Buffer.End, res.Buffer.End);
            } while (!res.IsCanceled && !res.IsCompleted);
        });

        await using var session = new Session(new StreamPeer(client), true);
        session.Start();
        var channel = await session.OpenChannelAsync();

        await channel.WriteAsync(new byte[32]);
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
            using var channel = await session.AcceptAsync();

            await session.GoAwayAsync();

            try
            {
                ReadResult res;
                do
                {
                    res = await channel.Input.ReadAsync();
                    channel.Input.AdvanceTo(res.Buffer.End, res.Buffer.End);
                } while (!res.IsCanceled && !res.IsCompleted);
            }
            catch
            {
            }
        });

        var clientTask = Task.Run(async () =>
        {
            await using var session = new Session(new StreamPeer(client), true);
            session.Start();
            using var channel = await session.OpenChannelAsync();

            await channel.WriteAsync(new byte[64]);
            await Task.Delay(500);

            channel.Close();
            await channel.WhenRemoteCloseAsync(TimeSpan.FromSeconds(1));
        });

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
                using var channel = await session.AcceptAsync();
                ReadResult res;
                do
                {
                    res = await channel.Input.ReadAsync();
                    channel.Input.AdvanceTo(res.Buffer.End, res.Buffer.End);
                } while (!res.IsCanceled && !res.IsCompleted);
            }
            catch
            {
            }
        });

        var clientTask = Task.Run(async () =>
        {
            await using var session = new Session(new StreamPeer(client), true);
            session.Start();

            var channel = await session.OpenChannelAsync();
            var tasks = new List<Task>();
            for (int i = 0; i < 10; i++)
            {
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        await channel.WriteAsync(new byte[1024]);
                    }
                    catch
                    {
                    }
                }));
            }

            await Task.Delay(100);
            channel.Abort();
            await Task.WhenAll(tasks);
        });

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
            using var channel = await session.AcceptAsync();

            try
            {
                ReadResult res;
                do
                {
                    res = await channel.Input.ReadAsync();
                    channel.Input.AdvanceTo(res.Buffer.End, res.Buffer.End);
                } while (!res.IsCanceled && !res.IsCompleted);
            }
            catch (YamuxException)
            {
            }
        });

        var clientTask = Task.Run(async () =>
        {
await using var session = new Session(new StreamPeer(client), true);
            session.Start();
            using var channel = await session.OpenChannelAsync();

            await channel.WriteAsync(new byte[1024]);

            await session.GoAwayAsync(SessionTermination.ProtocolError);

            try
            {
                await channel.WriteAsync(new byte[1024]);
            }
            catch (SessionChannelException)
            {
            }
        });

        await Task.WhenAll(serverTask, clientTask);
    }
}