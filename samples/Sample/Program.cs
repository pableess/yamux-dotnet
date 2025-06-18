using ByteSizeLib;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Spectre.Console;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices.Marshalling;
using System.Security.Cryptography;
using System.Threading.Channels;
using Yamux;

namespace Sample
{
    internal class Program
    {
        static async Task Main(string[] args)
        {
            var builder = Host.CreateApplicationBuilder();

            if (args.Length > 0 && args[0] == "yamux")
            {
                if (args.Length == 1)
                {
                    builder.Services.AddHostedService<YamuxServer>();
                }
                else
                {
                    builder.Services.AddHostedService<YamuxClient>();
                }
            }
            else
            {
                if (args.Length == 0)
                {

                    builder.Services.AddHostedService<Server>();
                }
                else
                {
                    builder.Services.AddHostedService<Client>();
                }
            }
            await builder.Build().RunAsync();
        }
    }

    class YamuxClient : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            using Socket socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, 5000));
            
            using var link = new NetworkStream(socket);

            using var session = link.AsYamuxSession(true, options: new SessionOptions { EnableStatistics = true, StatisticsSampleInterval = 1000, DefaultChannelOptions = new SessionChannelOptions { ReceiveWindowUpperBound = 8 *1024 * 1024 } });
            session.Start();

            await AnsiConsole.Status().StartAsync("Writing...", async ctx =>
            {

                if (session.Stats != null)
                {
                    session.Stats.Sampled += (o, a) =>
                    {
                        ctx.Status($"Upload {session.Stats?.SendRate} / sec");
                    };
                }

                var a = WriteChannelAsync(session, stoppingToken); 
                var b = WriteChannelAsync(session, stoppingToken);
                var c = WriteChannelAsync(session, stoppingToken);
                var d = WriteChannelAsync(session, stoppingToken);
                await Task.WhenAll(a, b, c, d);

                ctx.Status($"Done");
            });
        }

        private static Task WriteChannelAsync(Session session, CancellationToken stoppingToken)
        {
            return Task.Run(async () =>
            {
                try
                {
                    var buffer = new byte[1024 * 1024].AsMemory();
                    Random r = new Random();

                    using var channel = await session.OpenChannelAsync();
                    do
                    {
                        // write a random amount of bytes between 950KiB and 1MiB
                        var slice = buffer.Slice(r.Next(1024 * 48, 1024 * 64));
                        await channel.WriteAsync(slice, stoppingToken);

                    } while (!stoppingToken.IsCancellationRequested);
                }
                catch (OperationCanceledException) { }
            });
        }
    }

    class YamuxServer : BackgroundService 
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            using Socket socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            socket.Bind(new IPEndPoint(IPAddress.Loopback, 5000));

            socket.Listen();

            using var clientSocket = await socket.AcceptAsync();
            using var link = new NetworkStream(clientSocket);

            using var session = link.AsYamuxSession(false, options: new SessionOptions { EnableStatistics = true, StatisticsSampleInterval = 1000, DefaultChannelOptions = new SessionChannelOptions { ReceiveWindowUpperBound = 8 * 1024 * 1024 } });
            session.Start();
            await AnsiConsole.Status().StartAsync("Reading...", async ctx =>
            {

                if (session.Stats != null)
                {
                    session.Stats.Sampled += (o, a) =>
                    {
                        ctx.Status($"Download {session.Stats?.ReceiveRate} / sec");
                    };
                }

                var a = ReadChannelAsync(session, stoppingToken);
                var b = ReadChannelAsync(session, stoppingToken);
                var c = ReadChannelAsync(session, stoppingToken);
                var d = ReadChannelAsync(session, stoppingToken);

                await Task.WhenAll(a, b, c, d);

                ctx.Status($"Done");
            });
        }

        private static Task ReadChannelAsync(Session session, CancellationToken stoppingToken)
        {
            return Task.Run(async () => 
            {
                using var channel = await session.AcceptAsync();
                try
                {
                    ReadResult read;
                    do
                    {
                        read = await channel.Input.ReadAsync(stoppingToken);
                        channel.Input.AdvanceTo(read.Buffer.End, read.Buffer.End);

                    } while (!read.IsCanceled && !read.IsCompleted);

                }
                catch (OperationCanceledException) { }
            });
        }
    }


    class Client : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            using Socket socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, 5000));

            using var link = new NetworkStream(socket);

            await AnsiConsole.Status().StartAsync("Writing...", async ctx =>
            {
                long bytesWritten = 0;
                long last = Stopwatch.GetTimestamp();
                try
                {
                    var buffer = new byte[1024 * 1024].AsMemory();
                    Random r = new Random();

                    do
                    {
                        // write a random amount of bytes between 950KiB and 1MiB
                        var slice = buffer.Slice(r.Next(1024 * 950, 1024 * 1024));
                        await link.WriteAsync(slice, stoppingToken);
                        bytesWritten += slice.Length;
                        var current = Stopwatch.GetTimestamp();
                        var dif = current - last;
                        if (dif > TimeSpan.FromSeconds(3).Ticks)
                        {
                            var bytesPerSec = bytesWritten / TimeSpan.FromTicks(dif).TotalSeconds;
                            ctx.Status($"Upload {ByteSize.FromBytes(bytesPerSec)} / sec");
                            bytesWritten = 0;
                            last = current;
                        }

                    } while (!stoppingToken.IsCancellationRequested);
                }
                catch (OperationCanceledException) { }

                ctx.Status($"Done");
            });
        }
    }

    class Server : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            using Socket socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            socket.Bind(new IPEndPoint(IPAddress.Loopback, 5000));

            socket.Listen();

            using var clientSocket = await socket.AcceptAsync();
            using var link = new NetworkStream(clientSocket);

            await AnsiConsole.Status().StartAsync("Reading...", async ctx =>
            {
                long bytesRead = 0;

                try
                {
                    var buffer = new byte[1024 * 1024].AsMemory();
                    do
                    {
                        var read = await link.ReadAsync(buffer, stoppingToken);
                        bytesRead += read;

                        var bytes = ByteSize.FromBytes(bytesRead);
                        ctx.Status($"Downloaded {bytes}");

                    } while (true);

                }
                catch (OperationCanceledException) { }

                ctx.Status($"Done {ByteSize.FromBytes(bytesRead)}");
            });
        }
    }
}
