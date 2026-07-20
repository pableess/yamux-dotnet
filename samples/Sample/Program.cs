using ByteSizeLib;
using Spectre.Console;
using System.CommandLine;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Yamux;

namespace Sample;

internal class Program
{
    public static async Task Main(string[] args)
    {
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
        try
        {
            if (isServer)
            {
                await RunServerAsync();
            }
            else
            {
                await RunClientAsync(streams);
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"An error occurred: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
            return -1;
        }
    }

    private static async Task RunServerAsync()
    {
        using var listener = new Socket(SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 5000));
        listener.Listen();
        AnsiConsole.MarkupLine("[green]Server listening on port 5000[/]");

        var table = new Table();
        table.AddColumn("Channel");
        table.AddColumn("Download Rate (bytes/sec)");
        table.AddColumn("Upload Rate (bytes/sec)");

        while (true)
        {
            AnsiConsole.MarkupLine("[yellow]Waiting for client connection...[/]");

            using var clientSocket = await listener.AcceptAsync();
            AnsiConsole.MarkupLine("[green]Client connected[/]");

            await using var session = clientSocket.AsYamuxSession(false, options: new SessionOptions
            {
                EnableStatistics = true,
                StatisticsSampleInterval = 1000,
                DefaultChannelOptions = new SessionChannelOptions
                {
                    ReceiveWindowSize = 256 * 1024,
                    ReceiveWindowUpperBound = 16 * 1024 * 1024,
                    MaxDataFrameSize = 16 * 1024,
                    AutoTuneReceiveWindowSize = true,
                    EnableStatistics = true,
                    StatisticsSampleInterval = 1000
                }
            });
            session.Start();

            var channelRows = new Dictionary<int, int>();

            await AnsiConsole.Live(table).StartAsync(async ctx =>
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

                var channelTasks = new List<Task>();

                try
                {
                    while (true)
                    {
                        var channel = await session.AcceptAsync();
                        var task = RunChannelAsync(channel, ctx, table, channelRows, default);
                        channelTasks.Add(task);
                    }
                }
                catch (SessionException e) when (e.ErrorCode is SessionErrorCode.SessionShutdown or SessionErrorCode.StreamClosed or SessionErrorCode.StreamError)
                {
                    AnsiConsole.MarkupLine("[yellow]Client disconnected[/]");
                }
                finally
                {
                    lock (table)
                    {
                        table.Rows.Clear();
                        channelRows.Clear();
                    }

                    try { await Task.WhenAll(channelTasks); } catch { }
                    await session.DisposeAsync();
                }
            });
        }
    }

    private static async Task RunClientAsync(int streamCount)
    {
        using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, 5000));

        await using var session = socket.AsYamuxSession(true, options: new SessionOptions
        {
            EnableStatistics = true,
            StatisticsSampleInterval = 1000,
            DefaultChannelOptions = new SessionChannelOptions
            {
                ReceiveWindowSize = 256 * 1024,
                ReceiveWindowUpperBound = 16 * 1024 * 1024,
                MaxDataFrameSize = 16 * 1024,
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

            var channelTasks = new List<Task>();
            for (int i = 0; i < streamCount; i++)
            {
                var channel = await session.OpenChannelAsync(false);
                var task = RunChannelAsync(channel, ctx, table, channelRows, default);
                channelTasks.Add(task);
            }

            AnsiConsole.MarkupLine("[yellow]Press any key to disconnect...[/]");
            Console.ReadKey(true);

            await Task.WhenAll(channelTasks);
            await session.CloseOpenChannelsAsync(TimeSpan.FromSeconds(5));
            await session.DisposeAsync();
        });
    }

    private static readonly byte[] Buffer = new byte[1024 * 1024];

    static Program()
    {
        new Random().NextBytes(Buffer);
    }

    private static async Task RunChannelAsync(ISessionChannel channel, LiveDisplayContext ctx, Table table, Dictionary<int, int> channelRows, CancellationToken cancellation)
    {
        void UpdateStats(object? obj, EventArgs? args)
        {
            lock (table)
            {
                if (!channelRows.ContainsKey((int)channel.Id))
                {
                    table.AddRow(channel.Id.ToString(), channel.Stats?.SendRate.ToString() ?? "0", channel.Stats?.ReceiveRate.ToString() ?? "0");
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
                    while (!channel.IsClosed)
                    {
                        try
                        {
                            await writeChannel.WriteAsync(Buffer, cancellation);
                        }
                        catch (Exception)
                        {
                            break;
                        }
                    }
                });

            var readChannel = channel as IReadOnlySessionChannel;
            var receiveTask = readChannel == null ? Task.CompletedTask : Task.Run(async () =>
            {
                while (!channel.IsClosed)
                {
                    try
                    {
                        var result = await readChannel.Input.ReadAsync(cancellation);
                        readChannel.Input.AdvanceTo(result.Buffer.End);
                        if (result.IsCompleted || result.IsCanceled)
                        {
                            break;
                        }
                    }
                    catch (Exception)
                    {
                        break;
                    }
                }
            });

            await Task.WhenAll(sendTask, receiveTask);

            channel.Close();
            await channel.WhenRemoteCloseAsync(TimeSpan.FromSeconds(5));
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (channel.Stats != null)
            {
                channel.Stats.Sampled -= UpdateStats;
            }

            lock (table)
            {
                if (channelRows.TryGetValue((int)channel.Id, out var rowIndex))
                {
                    table.RemoveRow(rowIndex);
                    channelRows.Remove((int)channel.Id);
                }
            }

            channel.Dispose();
        }
    }
}