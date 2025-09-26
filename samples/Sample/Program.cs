using ByteSizeLib;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Spectre.Console;
using System;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;
using System.Diagnostics;
using System.IO;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Yamux;

namespace Sample
{
    internal class Program
    {
        public static async Task Main(string[] args)
        {
            // Ensure SessionTracer logs go to the console
            //Yamux.Session.SessionTracer.Listeners.Clear();
            //Yamux.Session.SessionTracer.Listeners.Add(new ConsoleTraceListener());
            //Yamux.Session.SessionTracer.Switch.Level = SourceLevels.All;

            var serverOption = new Option<bool>("--server");
            serverOption.Description = "Run as server";
            var streamsOption = new Option<int>("--streams");
                streamsOption.Description = "the number of streams";
                streamsOption.DefaultValueFactory = (res) => 10;

            var rootCommand = new RootCommand("Yamux Sample Application")
            {
                serverOption,
                streamsOption
            };

            rootCommand.SetAction(async (parseResult, cancel) =>
            {   
                int streams = parseResult.GetValue<int>(streamsOption);
                bool isServer = parseResult.GetValue<bool>(serverOption);

                
                return await RunSample(isServer, streams);
            });

            await rootCommand.Parse(args).InvokeAsync();
        }

        private static async Task<int> RunSample(bool isServer, int streams) 
        {
            var host = Host.CreateApplicationBuilder(null);

            try
            {
                if (isServer)
                {
                    host.Services.AddHostedService<YamuxServer>();
                }
                else
                {
                    host.Services.AddHostedService<YamuxClient>(sp => new YamuxClient(streams));
                }

                await host.Build().RunAsync();
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"An error occurred: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
                return -1;
            }
        }

    }

    abstract class YamuxBase : BackgroundService
    {
        static byte[] buffer = new byte[1024 * 1024]; // 16 MiB buffer for each write operation
        public YamuxBase()
        {
            new Random().NextBytes(buffer);
        }

        protected static async Task RunChannelAsync(ISessionChannel channel, LiveDisplayContext ctx, Table table, Dictionary<int, int> channelRows, CancellationToken stoppingToken)
        {
            void UpdateStats(object? obj, EventArgs? args)
            {
                lock (table)
                {
                    if (!channelRows.ContainsKey((int)channel.Id))
                    {
                        table.AddRow(channel.Id.ToString(), channel.Stats?.SendRate.ToString() ?? "0");
                        channelRows[(int)channel.Id] = table.Rows.Count - 1;
                    }
                    else
                    {
                        table.UpdateCell(channelRows[(int)channel.Id], 1, channel.Stats?.SendRate.ToString() ?? "0");
                        table.UpdateCell(channelRows[(int)channel.Id], 2, channel.Stats?.ReceiveRate.ToString() ?? "0");
                    }
                }
            }

            if (channel.Stats != null)
            {
                channel.Stats.Sampled += UpdateStats;
            }

            try
            {
                var writeChannel = channel as IWriteOnlySessionChannel;
                var sendTask = writeChannel == null ? Task.CompletedTask
                    : Task.Run(async () =>
                    {
                        try
                        {
                            while (!stoppingToken.IsCancellationRequested && !writeChannel.IsClosed)
                            {
                                await writeChannel.WriteAsync(buffer, stoppingToken);
                            }
                        }
                        catch (Exception ex)
                        {
                            AnsiConsole.MarkupLine($"[red]Channel {channel.Id} write error: {ex.Message}[/]");
                        }
                    });

                var readChannel = channel as IReadOnlySessionChannel;
                var receiveTask = readChannel == null ? Task.CompletedTask : Task.Run(async () =>
                {
                    while (!stoppingToken.IsCancellationRequested)
                    {
                        var result = await readChannel.Input.ReadAsync(stoppingToken);
                        readChannel.Input.AdvanceTo(result.Buffer.End);
                        if (result.IsCompleted || result.IsCanceled)
                        {
                            break;
                        }
                    }
                });

                await Task.WhenAll(sendTask, receiveTask);
            }
            finally
            {
                if (channel.Stats != null)
                {
                    channel.Stats.Sampled -= UpdateStats;
                }
            }
        }
    }

    class YamuxClient : YamuxBase
    {
        private readonly int _streamCount;

        public YamuxClient(int streamCount) : base()
        {
            _streamCount = streamCount;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            using Socket socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, 5000));

            using var link = new NetworkStream(socket);
            await using var session = link.AsYamuxSession(true, options: new SessionOptions
            {
                EnableStatistics = true,
                StatisticsSampleInterval = 1000,
                DefaultChannelOptions = new SessionChannelOptions
                {
                    ReceiveWindowSize = 256 * 1024, // 256 KiB
                    ReceiveWindowUpperBound = 16 * 1024 * 1024, // 16 MiB
                    MaxDataFrameSize = 16 * 1024, // 16 KiB
                    AutoTuneReceiveWindowSize = true,
                    EnableStatistics = true,
                    StatisticsSampleInterval = 1000
                }
            });
            session.Start();

            var table = new Table();
            table.AddColumn("Channel");
            table.AddColumn("Upload Rate (bytes/sec)");
            table.AddColumn("Download Rate (bytes/sec)");

            var channelRows = new Dictionary<int, int>();


            await AnsiConsole.Live(table).StartAsync(async ctx =>
            {
                if (session.Stats != null)
                {
                    session.Stats.Sampled += (o, a) =>
                    {
                        lock (table)
                        {
                            table.Caption($"Down {session.Stats?.ReceiveRate}/sec  Up {session.Stats?.SendRate}/sec  Total Download {new ByteSize(session.Stats!.TotalBytesReceived).ToString()} Total Upload {new ByteSize(session.Stats!.TotalBytesSent).ToString()} ");
                        }
                        ctx.Refresh();
                    };
                }

                // run concurrently streas for _streamCount number of streams

                // run concurrently streams for _streamCount number of streams
                var tasks = new List<Task>();
                for (int i = 0; i < _streamCount; i++)
                {
                    var channel = await session.OpenChannelAsync(false, stoppingToken);
                    tasks.Add(RunChannelAsync(channel, ctx, table, channelRows, stoppingToken));
                }
                await Task.WhenAll(tasks);
            });
        }

    }

    class YamuxServer : YamuxBase
    {
        public YamuxServer() : base()
        {
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            using Socket socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            socket.Bind(new IPEndPoint(IPAddress.Loopback, 5000));
            socket.Listen();

            using var clientSocket = await socket.AcceptAsync(stoppingToken);
            using var link = new NetworkStream(clientSocket);
            await using var session = link.AsYamuxSession(false, options: new SessionOptions
            {
                EnableStatistics = true,
                StatisticsSampleInterval = 1000,
                DefaultChannelOptions = new SessionChannelOptions
                {
                    ReceiveWindowSize = 256 * 1024, // 256 KiB
                    ReceiveWindowUpperBound = 16 * 1024 * 1024, // 16 MiB
                    MaxDataFrameSize = 16 * 1024, // 16 KiB
                    AutoTuneReceiveWindowSize = true,
                    EnableStatistics = true,
                    StatisticsSampleInterval = 1000
                }
            });
            session.Start();

            var table = new Table();
            table.AddColumn("Channel");
            table.AddColumn("Download Rate (bytes/sec)");
            table.AddColumn("Upload Rate (bytes/sec)");

            var channelRows = new Dictionary<int, int>();

            await AnsiConsole.Live(table).StartAsync( async ctx =>
            {
                if (session.Stats != null)
                {
                    session.Stats.Sampled += (o, a) =>
                    {
                        lock (table)
                        {
                            table.Caption($"Down {session.Stats?.ReceiveRate}/sec  Up {session.Stats?.SendRate}/sec ");
                        }

                        ctx.Refresh();
                    };
                }

                while (!stoppingToken.IsCancellationRequested)
                {
                    var channel = session.AcceptAsync(stoppingToken).Result;
                    _ = RunChannelAsync(channel, ctx, table, channelRows, stoppingToken);;
                }

                await session.DisposeAsync();
            });
        }
    }
}
