using AwesomeAssertions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Nerdbank.Streams;
using AutoBogus;
using Yamux.Internal;
using Bogus;
using System.IO.Pipelines;
using System.Buffers;
using System.ComponentModel.Design;
using Bogus.DataSets;
using System.Net.Sockets;
using System.Net;
using System.Diagnostics;
using Yamux.Protocol;

namespace Yamux.Tests

{
    public class SessionTests
    {
        [Fact]
        public async Task SingleOneWayTest()
        {
            var faker = new Faker();
            
            var data = faker.Random.Chars(count: 1024 * 750); // 750 KB random string
            var buffer = Encoding.UTF8.GetBytes(data).AsMemory();
            var result = new byte[buffer.Length].AsMemory();

            // full duplex stream (from nerdback is a way to simulate both ends of network connection)
            (var client, var server) = FullDuplexStream.CreatePair();

            // try to open a stream from the client side and 
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

                await channel.CloseAsync();

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
                        // check for range
                        var end = current + chunkSize;
                        var slice = buffer.Slice(current, end >= buffer.Length ? buffer.Length - current : chunkSize);
                        await channel.WriteAsync(slice, CancellationToken.None);
                    }
                    current += chunkSize;
                }


                await channel.CloseAsync();
            });

            await Task.WhenAll(serverTask, clientTask);

            // assert the byte arrays are the same
            result.ToArray().Should().BeEquivalentTo(buffer.ToArray());
        }

        [Fact]
        public async Task MultipleOneWay()
        {
            var faker = new Faker();

            var data1 = faker.Random.Chars(count: 1024 * 750); // 750 KB random string
            var buffer1 = Encoding.UTF8.GetBytes(data1).AsMemory();
            var result1 = new byte[buffer1.Length].AsMemory();

            var data2 = faker.Random.Chars(count: 1024 * 925); // 925 KB random string
            var buffer2 = Encoding.UTF8.GetBytes(data1).AsMemory();
            var result2 = new byte[buffer1.Length].AsMemory();

            // full duplex stream (from nerdback is a way to simulate both ends of network connection)
            (var client, var server) = FullDuplexStream.CreatePair();

            // try to open a stream from the client side and 

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

                async Task SendOnChannelAsync(Memory<byte> buffer)
                {
                    using var channel = await clientSession.OpenChannelAsync();

                    var size = buffer.Length;
                    int current = 0;
                    int chunkSize = 1024 * 4;

                    while (current < size)
                    {
                        if (buffer.Length > chunkSize)
                        {
                            // check for range
                            var end = current + chunkSize;
                            await channel.WriteAsync(buffer.Slice(current, end >= buffer.Length ? buffer.Length - current : chunkSize), CancellationToken.None);
                        }
                        current += chunkSize;
                    }

                    await channel.CloseAsync();
                }

                var taskA = SendOnChannelAsync(buffer1);
                var taskB = SendOnChannelAsync(buffer2);

                await Task.WhenAll(taskA, taskB);
            });

            await Task.WhenAll(serverTask, clientTask);

            // assert the byte arrays are the same
            result1.ToArray().Should().BeEquivalentTo(buffer1.ToArray());
            result2.ToArray().Should().BeEquivalentTo(buffer2.ToArray());
        }

        [Fact]
        public async Task SessionKillTest()
        {
            var faker = new Faker();

            // full duplex stream (from nerdback is a way to simulate both ends of network connection)
            (var client, var server) = FullDuplexStream.CreatePair();

            // try to open a stream from the client side and 

            var serverTask = Task.Run(async () =>
            {
                await using var serverSession = new Session(new StreamPeer(server), false);
                serverSession.Start();
                using var channel = await serverSession.AcceptAsync();

                ReadResult res;

                do
                {
                    res = await channel.Input.ReadAsync();

                    if (res.Buffer.Length > 0)
                    {
                        channel.Input.AdvanceTo(res.Buffer.End, res.Buffer.End);
                    }

                } while (!res.IsCanceled && !res.IsCompleted);

            });

            var clientTask = Task.Run(async () =>
            {
                await using var clientSession = new Session(new StreamPeer(client), true);
                clientSession.Start();

                using var channel = await clientSession.OpenChannelAsync();

                var data = faker.Random.Chars(count: 1024 * 750); // 750 KB random string
                var buffer = Encoding.UTF8.GetBytes(data).AsMemory();

                await channel.WriteAsync(buffer, CancellationToken.None);
                await channel.WriteAsync(buffer, CancellationToken.None);
                await channel.WriteAsync(buffer, CancellationToken.None);
                await channel.WriteAsync(buffer, CancellationToken.None);
                await channel.WriteAsync(buffer, CancellationToken.None);

                await clientSession.DisposeAsync();
            });

            await Task.WhenAll(serverTask, clientTask);

            // assert the byte arrays are the same
        }

        [Fact]
        public async Task StreamTest()
        {
            var faker = new Faker();

            var data = faker.Random.Chars(count: 1024 * 750); // 750 KB random string
            var buffer = Encoding.UTF8.GetBytes(data);
            byte[]? result = null;

            // full duplex stream (from nerdback is a way to simulate both ends of network connection)
            (var client, var server) = FullDuplexStream.CreatePair();

            // try to open a stream from the client side and 

            var serverTask = Task.Run(async () =>
            {
                await using var serverSession = new Session(new StreamPeer(server), false);
                serverSession.Start();
                using var channel = await serverSession.AcceptAsync();
                using var stream = channel.AsStream();

                using MemoryStream ms = new MemoryStream(buffer);
                await ms.CopyToAsync(stream);

                await channel.CloseAsync();

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

            // assert the byte arrays are the same
            result!.ToArray().Should().BeEquivalentTo(buffer.ToArray());
        }

        [Fact]
        public async Task SessionProtoclErrTest() 
        {
            (var client, var server) = FullDuplexStream.CreatePair();

            // try to open a stream from the client side and 

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

                // assert that we get a session protocol exception
                var ex = await Assert.ThrowsAsync<SessionException>(read);
                ex.ErrorCode.Should().Be(SessionErrorCode.InvalidVersion);

            });

            var clientTask = Task.Run(async () =>
            {
                await using var clientSession = new Session(new StreamPeer(client), true);
                clientSession.Start();

                using var channel = await clientSession.OpenChannelAsync();
                
                var faker = new Faker();
                var data = faker.Random.Chars(count: 1024 * 312); // 312 KB random string
                var buffer = Encoding.UTF8.GetBytes(data).AsMemory();
                await channel.WriteAsync(buffer, default);

                // send header with invalid version on the original stream
                await client.WriteAsync(new byte[] { 0x01, 0x20, 0x20, 0x20, 0x20, 0x20, 0x20 }.AsMemory(), default);

                await Task.Delay(10);
                await clientSession.DisposeAsync();
            });

            await Task.WhenAll(serverTask, clientTask);
        }

        [Fact]
        public async Task BenchmarkTestAsync()
        {
            Random r = new Random();
            var _buffer = new byte[1024 * 32].AsMemory();
            r.NextBytes(_buffer.Span);
            using Socket ss = new Socket(SocketType.Stream, ProtocolType.Tcp);
            ss.Bind(new IPEndPoint(IPAddress.Loopback, 5001));
            ss.Listen();

            using Socket cs = new Socket(SocketType.Stream, ProtocolType.Tcp);
            await cs.ConnectAsync(new IPEndPoint(IPAddress.Loopback, 5001));


            var serverTask = Task.Run(async () =>
            {
                using var ss1 = await ss.AcceptAsync();
                using Stream server = new NetworkStream(ss1);
                await using var session = new Session(new StreamPeer(server), false, options: new SessionOptions { DefaultChannelOptions = new SessionChannelOptions { AutoTuneReceiveWindowSize = false, ReceiveWindowSize = uint.MaxValue } });
                session.Start();
                using var channel = await session.OpenChannelAsync(false);

                // every 32 iterations is a MB of data (in 32KB chunks)
                int iterations = 50 * 32;
                for (int i = 0; i < iterations; i++)
                {
                    await channel.WriteAsync(_buffer);
                }

                await channel.CloseAsync();
                await session.CloseAsync();
            });

            var clientTask = Task.Run(async () =>
            {
                using Stream client = new NetworkStream(cs);
                await using var session = new Session(new StreamPeer(client), true, options: new SessionOptions { DefaultChannelOptions = new SessionChannelOptions { AutoTuneReceiveWindowSize = false, ReceiveWindowSize = uint.MaxValue } });
                session.Start();
                using var channel = await session.AcceptReadOnlyChannelAsync(null);

                byte[] readBuffer = new byte[1024 * 32];

                ReadResult res;

                do
                {
                    res = await channel.Input.ReadAtLeastAsync(1024);
                    channel.Input.AdvanceTo(res.Buffer.End, res.Buffer.End);
                } while (!res.IsCompleted);

                await channel.CloseAsync();
                await session.CloseAsync();
            });

            await Task.WhenAll(serverTask, clientTask);
        }
    }
}
