using AwesomeAssertions;
using Bogus;
using Nerdbank.Streams;
using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using Yamux.Internal;
using Yamux.Protocol;

namespace Yamux.Tests;

public class SessionTests
{
    [Fact]
    public async Task SingleOneWayTest()
    {
        var faker = new Faker();
        var data = faker.Random.Chars(count: 1024 * 750);
        var buffer = Encoding.UTF8.GetBytes(data).AsMemory();
        var result = new byte[buffer.Length].AsMemory();

        (var client, var server) = FullDuplexStream.CreatePair();

        var serverTask = Task.Run(async () =>
        {
            await using var serverSession = new Session(new StreamPeer(server), false);
            serverSession.Start();

            using var channel = await serverSession.AcceptAsync();

            long index = 0;
            ReadResult res;
            do
            {
                res = await channel.Input.ReadAsync();
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
        });

        var clientTask = Task.Run(async () =>
        {
            await using var clientSession = new Session(new StreamPeer(client), true);
            clientSession.Start();

            using var channel = await clientSession.OpenChannelAsync();

            var size = buffer.Length;
            int current = 0;
            int chunkSize = 1024 * 4;

            while (current < size)
            {
                if (buffer.Length > chunkSize)
                {
                    var end = current + chunkSize;
                    var slice = buffer.Slice(current, end >= buffer.Length ? buffer.Length - current : chunkSize);
                    await channel.WriteAsync(slice, CancellationToken.None);
                }
                current += chunkSize;
            }

            channel.Close();
            await channel.WhenRemoteCloseAsync(TimeSpan.FromSeconds(1));
        });

        await Task.WhenAll(serverTask, clientTask);
        result.ToArray().Should().BeEquivalentTo(buffer.ToArray());
    }

    [Fact]
    public async Task MultipleOneWay()
    {
        var faker = new Faker();

        var data1 = faker.Random.Chars(count: 1024 * 750);
        var buffer1 = Encoding.UTF8.GetBytes(data1).AsMemory();
        var result1 = new byte[buffer1.Length].AsMemory();

        var data2 = faker.Random.Chars(count: 1024 * 925);
        var buffer2 = Encoding.UTF8.GetBytes(data2).AsMemory();
        var result2 = new byte[buffer2.Length].AsMemory();

        (var client, var server) = FullDuplexStream.CreatePair();

        var serverTask = Task.Run(async () =>
        {
            await using var serverSession = new Session(new StreamPeer(server), false);
            serverSession.Start();

            async Task ReadChannelAsync(IReadOnlySessionChannel channel, Memory<byte> result)
            {
                long index = 0;
                ReadResult res;
                do
                {
                    res = await channel.Input.ReadAsync();
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
            }

            using var channel1 = await serverSession.AcceptAsync();
            var taskA = ReadChannelAsync(channel1, result1);
            using var channel2 = await serverSession.AcceptAsync();
            var taskB = ReadChannelAsync(channel2, result2);

            await Task.WhenAll(taskA, taskB);
        });

        var clientTask = Task.Run(async () =>
        {
            await using var clientSession = new Session(new StreamPeer(client), true);
            clientSession.Start();

            async Task SendOnChannelAsync(Memory<byte> buf)
            {
                using var channel = await clientSession.OpenChannelAsync();
                var size = buf.Length;
                int current = 0;
                int chunkSize = 1024 * 4;

                while (current < size)
                {
                    if (buf.Length > chunkSize)
                    {
                        var end = current + chunkSize;
                        await channel.WriteAsync(buf.Slice(current, end >= buf.Length ? buf.Length - current : chunkSize), CancellationToken.None);
                    }
                    current += chunkSize;
                }

                channel.Close();
                await channel.WhenRemoteCloseAsync(TimeSpan.FromSeconds(1));
            }

            var taskA = SendOnChannelAsync(buffer1);
            var taskB = SendOnChannelAsync(buffer2);

            await Task.WhenAll(taskA, taskB);
        });

        await Task.WhenAll(serverTask, clientTask);

        result1.ToArray().Should().BeEquivalentTo(buffer1.ToArray());
        result2.ToArray().Should().BeEquivalentTo(buffer2.ToArray());
    }

    [Fact]
    public async Task SessionKillTest()
    {
        var faker = new Faker();
        (var client, var server) = FullDuplexStream.CreatePair();

        var serverTask = Task.Run(async () =>
        {
            await using var serverSession = new Session(new StreamPeer(server), false);
            serverSession.Start();
            using var channel = await serverSession.AcceptAsync();

            Func<Task> read = async () =>
            {
                try
                {
                    ReadResult res;
                    do
                    {
                        res = await channel.Input.ReadAsync();
                        if (res.Buffer.Length > 0)
                            channel.Input.AdvanceTo(res.Buffer.End, res.Buffer.End);
                    } while (!res.IsCanceled && !res.IsCompleted);
                }
                catch (YamuxException)
                {
                }
            };

            await read();
        });

        var clientTask = Task.Run(async () =>
        {
            await using var clientSession = new Session(new StreamPeer(client), true);
            clientSession.Start();

            using var channel = await clientSession.OpenChannelAsync();

            var data = faker.Random.Chars(count: 1024 * 750);
            var buffer = Encoding.UTF8.GetBytes(data).AsMemory();

            await channel.WriteAsync(buffer, CancellationToken.None);
            await channel.WriteAsync(buffer, CancellationToken.None);
            await channel.WriteAsync(buffer, CancellationToken.None);
            await channel.WriteAsync(buffer, CancellationToken.None);
            await channel.WriteAsync(buffer, CancellationToken.None);

            await clientSession.DisposeAsync();
        });

        await Task.WhenAll(serverTask, clientTask);
    }

    [Fact]
    public async Task StreamTest()
    {
        var faker = new Faker();
        var data = faker.Random.Chars(count: 1024 * 750);
        var buffer = Encoding.UTF8.GetBytes(data);
        byte[]? result = null;

        (var client, var server) = FullDuplexStream.CreatePair();

        var serverTask = Task.Run(async () =>
        {
            await using var serverSession = new Session(new StreamPeer(server), false);
            serverSession.Start();
            using var channel = await serverSession.AcceptAsync();
            using var stream = channel.AsStream();

            using MemoryStream ms = new MemoryStream(buffer);
            await ms.CopyToAsync(stream);

            channel.Close();
            await channel.WhenRemoteCloseAsync(TimeSpan.FromSeconds(1));
            channel.Dispose();
        });

        var clientTask = Task.Run(async () =>
        {
            await using var clientSession = new Session(new StreamPeer(client), true);
            clientSession.Start();

            using var channel = await clientSession.OpenChannelAsync();
            using var stream = channel.AsStream();

            using MemoryStream ms = new MemoryStream();
            await stream.CopyToAsync(ms);

            result = ms.ToArray();
            stream.Close();
        });

        await Task.WhenAll(serverTask, clientTask);

        result.Should().NotBeNull();
        result!.ToArray().Should().BeEquivalentTo(buffer.ToArray());
    }

    [Fact]
    public async Task SessionProtoclErrTest()
    {
        (var client, var server) = FullDuplexStream.CreatePair();

        var serverTask = Task.Run(async () =>
        {
            await using var serverSession = new Session(new StreamPeer(server), false);
            serverSession.Start();
            using var channel = await serverSession.AcceptAsync();

            Func<Task> read = async () =>
            {
                ReadResult res;
                do
                {
                    res = await channel.Input.ReadAsync(default);
                    channel.Input.AdvanceTo(res.Buffer.End, res.Buffer.End);
                } while (!res.IsCanceled && !res.IsCompleted);
            };

            var ex = await Assert.ThrowsAsync<SessionException>(read);
            ex.ErrorCode.Should().Be(SessionErrorCode.InvalidVersion);
        });

        var clientTask = Task.Run(async () =>
        {
            try
            {
                await using var clientSession = new Session(new StreamPeer(client), true);
                clientSession.Start();

                using var channel = await clientSession.OpenChannelAsync();

                var faker = new Faker();
                var data = faker.Random.Chars(count: 1024 * 312);
                var buffer = Encoding.UTF8.GetBytes(data).AsMemory();
                await channel.WriteAsync(buffer, default);

                await client.WriteAsync(new byte[] { 0x01, 0x20, 0x20, 0x20, 0x20, 0x20, 0x20 }.AsMemory(), default);
                await client.FlushAsync();

                await channel.WriteAsync(buffer.Slice(0, 64), default);
                await Task.Delay(200);
            }
            catch
            {
            }
        });

        await Task.WhenAll(serverTask, clientTask);
    }

    [Fact]
    public async Task FinHalfCloseTest()
    {
        (var client, var server) = FullDuplexStream.CreatePair();
        var data = new Faker().Random.Bytes(1024 * 50);

        var serverTask = Task.Run(async () =>
        {
            await using var serverSession = new Session(new StreamPeer(server), false);
            serverSession.Start();
            using var channel = await serverSession.AcceptAsync();

            byte[] received = new byte[data.Length];
            long index = 0;
            ReadResult res;
            do
            {
                res = await channel.Input.ReadAsync();
                if (res.Buffer.Length > 0)
                {
                    res.Buffer.CopyTo(received.AsMemory((int)index, (int)res.Buffer.Length).Span);
                    index += res.Buffer.Length;
                    channel.Input.AdvanceTo(res.Buffer.End, res.Buffer.End);
                }
            } while (!res.IsCanceled && !res.IsCompleted);

            received.Should().BeEquivalentTo(data);

            channel.Close();
            await channel.WhenRemoteCloseAsync(TimeSpan.FromSeconds(1));
            channel.Dispose();
        });

        var clientTask = Task.Run(async () =>
        {
            await using var clientSession = new Session(new StreamPeer(client), true);
            clientSession.Start();
            using var channel = await clientSession.OpenChannelAsync();

            await channel.WriteAsync(data, CancellationToken.None);

            // half-close: close write side
            channel.Close();

            // verify write throws after close
            Func<Task> writeAfterClose = () => channel.WriteAsync(new byte[1], CancellationToken.None).AsTask();
            await writeAfterClose.Should().ThrowAsync<SessionChannelException>();

            await channel.WhenRemoteCloseAsync(TimeSpan.FromSeconds(1));
            channel.Dispose();
        });

        await Task.WhenAll(serverTask, clientTask);
    }

    [Fact]
    public async Task RstOnAbortTest()
    {
        (var client, var server) = FullDuplexStream.CreatePair();
        var data = new Faker().Random.Bytes(1024 * 50);

        var serverTask = Task.Run(async () =>
        {
            await using var serverSession = new Session(new StreamPeer(server), false);
            serverSession.Start();
            using var channel = await serverSession.AcceptAsync();

            try
            {
                ReadResult res;
                do
                {
                    res = await channel.Input.ReadAsync();
                    if (res.Buffer.Length > 0)
                        channel.Input.AdvanceTo(res.Buffer.End, res.Buffer.End);
                } while (!res.IsCanceled && !res.IsCompleted);
            }
            catch (SessionChannelException)
            {
            }

            channel.Dispose();
        });

        var clientTask = Task.Run(async () =>
        {
            await using var clientSession = new Session(new StreamPeer(client), true);
            clientSession.Start();
            using var channel = await clientSession.OpenChannelAsync();

            await channel.WriteAsync(data, CancellationToken.None);

            channel.Abort();

            Func<Task> readAfterAbort = async () =>
            {
                var res = await channel.Input.ReadAsync();
                channel.Input.AdvanceTo(res.Buffer.End, res.Buffer.End);
            };
            (await readAfterAbort.Should().ThrowAsync<Exception>()).Which.Should().BeOfType<SessionChannelException>();
        });

        await Task.WhenAll(serverTask, clientTask);
    }

    [Fact]
    public async Task FlowControlBackpressureTest()
    {
        (var client, var server) = FullDuplexStream.CreatePair();
        var buffer = new byte[1024 * 16];

        var serverTask = Task.Run(async () =>
        {
            await using var serverSession = new Session(new StreamPeer(server), false, options: new SessionOptions
            {
                DefaultChannelOptions = new SessionChannelOptions
                {
                    AutoTuneReceiveWindowSize = false,
                }
            });
            serverSession.Start();
            using var channel = await serverSession.AcceptAsync();

            // don't read - this exhausts the remote window
            await Task.Delay(3000);
            channel.Dispose();
        });

        var clientTask = Task.Run(async () =>
        {
            await using var clientSession = new Session(new StreamPeer(client), true, options: new SessionOptions
            {
                DefaultChannelOptions = new SessionChannelOptions
                {
                    AutoTuneReceiveWindowSize = false,
                }
            });
            clientSession.Start();
            using var channel = await clientSession.OpenChannelAsync();

            // write enough to exhaust the 256KB window
            for (int i = 0; i < 16; i++)
            {
                await channel.WriteAsync(buffer, CancellationToken.None);
            }

            // next write should block because window is exhausted
            var blockedWrite = channel.WriteAsync(buffer, CancellationToken.None).AsTask();
            var timeout = Task.Delay(1000);
            var completed = await Task.WhenAny(blockedWrite, timeout);

            completed.Should().Be(timeout);
        });

        await Task.WhenAll(serverTask, clientTask);
    }

    [Fact]
    public async Task GoAwayRejectsNewChannelsTest()
    {
        (var client, var server) = FullDuplexStream.CreatePair();

        var serverTask = Task.Run(async () =>
        {
            await using var serverSession = new Session(new StreamPeer(server), false);
            serverSession.Start();

            using var channel1 = await serverSession.AcceptAsync();
            await serverSession.GoAwayAsync();

            await channel1.WriteAsync(new byte[64], CancellationToken.None);

            channel1.Close();
            await channel1.WhenRemoteCloseAsync(TimeSpan.FromSeconds(1));
            channel1.Dispose();
        });

        var clientTask = Task.Run(async () =>
        {
            await using var clientSession = new Session(new StreamPeer(client), true);
            clientSession.Start();

            using var channel1 = await clientSession.OpenChannelAsync();
            await Task.Delay(500);

            Func<Task> openAfterGoAway = () => clientSession.OpenChannelAsync(false).AsTask();
            await openAfterGoAway.Should().ThrowAsync<SessionException>();

            ReadResult res;
            do
            {
                res = await channel1.Input.ReadAsync();
                if (res.Buffer.Length > 0)
                    channel1.Input.AdvanceTo(res.Buffer.End, res.Buffer.End);
            } while (!res.IsCanceled && !res.IsCompleted);

            channel1.Close();
            await channel1.WhenRemoteCloseAsync(TimeSpan.FromSeconds(1));
            channel1.Dispose();
        });

        await Task.WhenAll(serverTask, clientTask);
    }

    [Fact]
    public async Task BidirectionalDataTest()
    {
        (var client, var server) = FullDuplexStream.CreatePair();
        var clientToServer = new Faker().Random.Bytes(1024 * 100);
        var serverToClient = new Faker().Random.Bytes(1024 * 100);

        byte[]? serverReceived = null;
        byte[]? clientReceived = null;

        var serverTask = Task.Run(async () =>
        {
            await using var serverSession = new Session(new StreamPeer(server), false);
            serverSession.Start();
            using var channel = await serverSession.AcceptAsync();

            await channel.WriteAsync(serverToClient, CancellationToken.None);
            channel.Close();

            var ms = new MemoryStream();
            await channel.Input.CopyToAsync(ms);
            serverReceived = ms.ToArray();

            await channel.WhenRemoteCloseAsync(TimeSpan.FromSeconds(1));
            channel.Dispose();
        });

        var clientTask = Task.Run(async () =>
        {
            await using var clientSession = new Session(new StreamPeer(client), true);
            clientSession.Start();
            using var channel = await clientSession.OpenChannelAsync();

            await channel.WriteAsync(clientToServer, CancellationToken.None);
            channel.Close();

            var ms = new MemoryStream();
            await channel.Input.CopyToAsync(ms);
            clientReceived = ms.ToArray();

            await channel.WhenRemoteCloseAsync(TimeSpan.FromSeconds(1));
            channel.Dispose();
        });

        await Task.WhenAll(serverTask, clientTask);

        clientReceived.Should().NotBeNull();
        serverReceived.Should().NotBeNull();
        clientReceived.Should().BeEquivalentTo(serverToClient);
        serverReceived.Should().BeEquivalentTo(clientToServer);
    }

    [Fact]
    public async Task OpenChannelWaitForAckTest()
    {
        (var client, var server) = FullDuplexStream.CreatePair();
        using var clientOpened = new ManualResetEventSlim(false);

        var serverTask = Task.Run(async () =>
        {
            await using var serverSession = new Session(new StreamPeer(server), false);
            serverSession.Start();
            using var channel = await serverSession.AcceptAsync();

            clientOpened.Wait(3000);

            byte[] received = new byte[64];
            long index = 0;
            try
            {
                ReadResult res;
                do
                {
                    res = await channel.Input.ReadAsync();
                    if (res.Buffer.Length > 0)
                    {
                        res.Buffer.CopyTo(received.AsMemory((int)index).Span);
                        index += res.Buffer.Length;
                        channel.Input.AdvanceTo(res.Buffer.End, res.Buffer.End);
                    }
                } while (!res.IsCanceled && !res.IsCompleted);
            }
            catch (YamuxException)
            {
            }

            channel.Close();
            await channel.WhenRemoteCloseAsync(TimeSpan.FromSeconds(3));
            channel.Dispose();
        });

        var clientTask = Task.Run(async () =>
        {
            await using var clientSession = new Session(new StreamPeer(client), true, options: new SessionOptions
            {
                StreamOpenTimeout = TimeSpan.Zero
            });
            clientSession.Start();

            using var channel = await clientSession.OpenChannelAsync(waitForAcknowledgement: true);
            clientOpened.Set();

            await channel.WriteAsync(new byte[64], CancellationToken.None);

            channel.Close();
            await channel.WhenRemoteCloseAsync(TimeSpan.FromSeconds(3));

            await Task.Delay(500);
            channel.Dispose();
        });

        await Task.WhenAll(serverTask, clientTask);
    }

    [Fact]
    public async Task ZeroByteWriteTest()
    {
        (var client, var server) = FullDuplexStream.CreatePair();

        var serverTask = Task.Run(async () =>
        {
            await using var serverSession = new Session(new StreamPeer(server), false);
            serverSession.Start();
            using var channel = await serverSession.AcceptAsync();

            await Task.Delay(500);

            channel.Close();
            await channel.WhenRemoteCloseAsync(TimeSpan.FromSeconds(1));
            channel.Dispose();
        });

        var clientTask = Task.Run(async () =>
        {
            await using var clientSession = new Session(new StreamPeer(client), true);
            clientSession.Start();
            using var channel = await clientSession.OpenChannelAsync();

            await channel.WriteAsync(ReadOnlyMemory<byte>.Empty, CancellationToken.None);

            channel.Close();
            await channel.WhenRemoteCloseAsync(TimeSpan.FromSeconds(1));
            channel.Dispose();
        });

        await Task.WhenAll(serverTask, clientTask);
    }

    [Fact]
    public async Task LargePayloadMultiFrameTest()
    {
        var data = new Faker().Random.Bytes(10 * 1024 * 1024);
        (var client, var server) = FullDuplexStream.CreatePair();
        byte[]? result = null;

        var serverTask = Task.Run(async () =>
        {
            await using var serverSession = new Session(new StreamPeer(server), false);
            serverSession.Start();
            using var channel = await serverSession.AcceptAsync();

            var ms = new MemoryStream();
            await channel.Input.CopyToAsync(ms);
            result = ms.ToArray();

            channel.Close();
            await channel.WhenRemoteCloseAsync(TimeSpan.FromSeconds(5));
            channel.Dispose();
        });

        var clientTask = Task.Run(async () =>
        {
            await using var clientSession = new Session(new StreamPeer(client), true);
            clientSession.Start();
            using var channel = await clientSession.OpenChannelAsync();

            await channel.WriteAsync(data, CancellationToken.None);

            channel.Close();
            await channel.WhenRemoteCloseAsync(TimeSpan.FromSeconds(5));
            channel.Dispose();
        });

        await Task.WhenAll(serverTask, clientTask);

        result.Should().NotBeNull();
        result.Should().BeEquivalentTo(data);
    }
}