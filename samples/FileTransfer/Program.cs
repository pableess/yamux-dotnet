using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Spectre.Console;
using System.Collections.Concurrent;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Yamux;
using System.Text.Json;
using System.Security.Cryptography;

namespace FileTransfer
{
    internal class Program
    {
        static int Main(string[] args)
        {
            var rootCommand = new RootCommand("File Transfer application");

            var serverDirOption = new Option<DirectoryInfo>(
                "--dir")
            {
                Arity = ArgumentArity.ZeroOrOne
            };
            serverDirOption.Description = "The directory to save uploaded files.";
            var serverCommand = new Command("server", "Runs the server");
            serverCommand.Add(serverDirOption);
            serverCommand.SetAction((parseResult) =>
            {
                var dir = parseResult.GetValue(serverDirOption) ?? new DirectoryInfo("serverFiles");
                
                var host = Host.CreateDefaultBuilder()
                    .ConfigureServices((context, services) =>
                    {
                        services.AddSingleton(dir);
                        services.AddHostedService<ServerService>();
                    })
                    .Build();
                host.Run();
            });

            var clientDirOption = new Option<DirectoryInfo>(
                "--dir")
            {
                Arity = ArgumentArity.ExactlyOne
            };
            clientDirOption.Required = true;
            clientDirOption.Description = "The directory to copy files from.";
            var clientCommand = new Command("client", "Runs the client");
            clientCommand.Add(clientDirOption);
            clientCommand.SetAction((parseResult) =>
            {
                var dir = parseResult.GetRequiredValue(clientDirOption);

                var host = Host.CreateDefaultBuilder()
                    .ConfigureServices((context, services) =>
                    {
                        services.AddSingleton(dir);
                        services.AddHostedService<ClientService>();
                    })
                    .Build();
                host.Run();
            });

            rootCommand.Add(serverCommand);
            rootCommand.Add(clientCommand);

            return rootCommand.Parse(args).Invoke();
        }
    }

    public class ServerService : BackgroundService
    {
        private readonly DirectoryInfo _directory;
        private readonly object _lock = new();
        private readonly ConcurrentDictionary<uint, FileTransferInfo> _activeTransfers = new();
        private readonly ConcurrentDictionary<int, string> _sessionIds = new();
        private int _nextSessionId = 1;

        public ServerService(DirectoryInfo directory)
        {
            _directory = directory;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            Directory.CreateDirectory(_directory.FullName);

            using Socket socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            socket.Bind(new IPEndPoint(IPAddress.Loopback, 5000));
            socket.Listen();

            var tableTask = Task.Run(() => RenderTable(stoppingToken), stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                var clientSocket = await socket.AcceptAsync(stoppingToken);
                _ = RunClient(clientSocket, stoppingToken);
            }
        }

        private async Task RunClient(Socket clientSocket, CancellationToken stoppingToken)
        {
            var link = new NetworkStream(clientSocket);
            var sessionId = GenerateSessionId();
            await using var session = link.AsYamuxSession(false, options: new SessionOptions
            {
                EnableStatistics = true,
                StatisticsSampleInterval = 1000,
                DefaultChannelOptions = new SessionChannelOptions { ReceiveWindowUpperBound = 8 * 1024 * 1024 }
            });
            session.Start();
            while (!stoppingToken.IsCancellationRequested && !session.IsClosed)
            {
                try {
                    var channel = await session.AcceptReadOnlyChannelAsync(stoppingToken);
                    _ = Task.Run(async () => await ReadFileAsync(channel, sessionId, channel.Id, stoppingToken));
                }
                catch (OperationCanceledException) { }
                catch (SessionException ex) { }
            }
        }

        private async Task ReadFileAsync(IReadOnlySessionChannel channel, string sessionId, uint channelId, CancellationToken stoppingToken)
        {
            FileTransferInfo info = null;
            try
            {
                // Read metadata
                var metadataBuffer = new byte[1024];
                var bytesRead = await channel.Input.AsStream().ReadAsync(metadataBuffer, stoppingToken);
                var metadataJson = System.Text.Encoding.UTF8.GetString(metadataBuffer, 0, bytesRead);
                var metadata = JsonSerializer.Deserialize<FileMetadata>(metadataJson);
                var filePath = Path.Combine(_directory.FullName, metadata.Name);
                info = new FileTransferInfo
                {
                    SessionId = sessionId,
                    ChannelId = channelId,
                    FileName = metadata.Name,
                    FileSize = metadata.Length,
                    BytesReceived = 0
                };
                _activeTransfers[channelId] = info;
                using var fs = File.Create(filePath);
                var buffer = new byte[81920];
                int read;
                var stream = channel.Input.AsStream();
                while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, stoppingToken)) > 0)
                {
                    await fs.WriteAsync(buffer, 0, read, stoppingToken);
                    info.BytesReceived += read;
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception) { }
            finally
            {
                if (info != null)
                    _activeTransfers.TryRemove(channelId, out _);
                channel.Dispose();
            }
        }

        private async Task RenderTable(CancellationToken stoppingToken)
        {
            var table = new Table()
                .AddColumn("Session ID")
                .AddColumn("Channel ID")
                .AddColumn("File Name")
                .AddColumn("Progress")
                .Border(TableBorder.Rounded)
                .Title("[yellow]Active File Downloads[/]");
            await AnsiConsole.Live(table)
                .StartAsync(async ctx =>
                {
                    while (!stoppingToken.IsCancellationRequested)
                    {
                        table.Rows.Clear();
                        foreach (var kv in _activeTransfers.Values)
                        {
                            var percent = kv.FileSize > 0 ? (double)kv.BytesReceived / kv.FileSize * 100 : 0;
                            table.AddRow(
                                kv.SessionId,
                                kv.ChannelId.ToString(),
                                kv.FileName,
                                $"{kv.BytesReceived}/{kv.FileSize} ({percent:F1}%)"
                            );
                        }
                        ctx.Refresh();
                        await Task.Delay(200, stoppingToken);
                    }
                });
        }

        private string GenerateSessionId()
        {
            // 8-char random hex
            var bytes = RandomNumberGenerator.GetBytes(4);
            return BitConverter.ToString(bytes).Replace("-", "");
        }

        private class FileMetadata
        {
            public string Name { get; set; }
            public long Length { get; set; }
        }

        private class FileTransferInfo
        {
            public string SessionId { get; set; }
            public uint ChannelId { get; set; }
            public string FileName { get; set; }
            public long FileSize { get; set; }
            public long BytesReceived { get; set; }
        }
    }


    public class ClientService : BackgroundService
    {
        private readonly DirectoryInfo _directory;
        private readonly ConcurrentDictionary<uint, FileTransferInfo> _activeTransfers = new();
        private readonly string _sessionId;

        public ClientService(DirectoryInfo directory)
        {
            _directory = directory;
            _sessionId = GenerateSessionId();
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            using Socket socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, 5000), stoppingToken);

            using var link = new NetworkStream(socket);

            var session = link.AsYamuxSession(true, options: new SessionOptions
            {
                EnableStatistics = true,
                StatisticsSampleInterval = 1000,
                DefaultChannelOptions = new SessionChannelOptions { ReceiveWindowUpperBound = 8 * 1024 * 1024 }
            });
            session.Start();

            var files = _directory.GetFiles();

            var tableTask = Task.Run(() => RenderTable(stoppingToken), stoppingToken);

            var tasks = files.Select(file => Task.Run(async () =>
            {
                FileTransferInfo info = null;
                uint channelId = 0;
                try
                {
                    var channel = await session.OpenChannelAsync();
                    channelId = channel.Id;
                    info = new FileTransferInfo
                    {
                        SessionId = _sessionId,
                        ChannelId = channelId,
                        FileName = file.Name,
                        FileSize = file.Length,
                        BytesSent = 0
                    };
                    _activeTransfers[channelId] = info;

                    // Send metadata
                    var metadata = new { file.Name, file.Length };
                    var metadataJson = JsonSerializer.Serialize(metadata);
                    var metadataBytes = System.Text.Encoding.UTF8.GetBytes(metadataJson);
                    await channel.AsStream().WriteAsync(metadataBytes, 0, metadataBytes.Length, stoppingToken);

                    // Send file contents
                    using var fs = file.OpenRead();
                    var buffer = new byte[81920];
                    int read;
                    var stream = channel.AsStream();
                    while ((read = await fs.ReadAsync(buffer, 0, buffer.Length, stoppingToken)) > 0)
                    {
                        await stream.WriteAsync(buffer, 0, read, stoppingToken);
                        info.BytesSent += read;
                    }
                    await channel.CloseAsync();
                }
                catch (Exception) { }
                finally
                {
                    if (info != null)
                        _activeTransfers.TryRemove(channelId, out _);
                }
            }, stoppingToken)).ToArray();

            await Task.WhenAll(tasks);
            await session.DisposeAsync();
        }

        private async Task RenderTable(CancellationToken stoppingToken)
        {
            var table = new Table()
                .AddColumn("Session ID")
                .AddColumn("Channel ID")
                .AddColumn("File Name")
                .AddColumn("Progress")
                .Border(TableBorder.Rounded)
                .Title("[yellow]Active File Uploads[/]");
            await AnsiConsole.Live(table)
                .StartAsync(async ctx =>
                {
                    while (!stoppingToken.IsCancellationRequested)
                    {
                        table.Rows.Clear();
                        foreach (var kv in _activeTransfers.Values)
                        {
                            var percent = kv.FileSize > 0 ? (double)kv.BytesSent / kv.FileSize * 100 : 0;
                            table.AddRow(
                                kv.SessionId,
                                kv.ChannelId.ToString(),
                                kv.FileName,
                                $"{kv.BytesSent}/{kv.FileSize} ({percent:F1}%)"
                            );
                        }
                        ctx.Refresh();
                        await Task.Delay(200, stoppingToken);
                    }
                });
        }

        private static string GenerateSessionId()
        {
            var bytes = RandomNumberGenerator.GetBytes(4);
            return BitConverter.ToString(bytes).Replace("-", "");
        }

        private class FileTransferInfo
        {
            public required string SessionId { get; set; }
            public required uint ChannelId { get; set; }
            public required string FileName { get; set; }
            public required long FileSize { get; set; }
            public required long BytesSent { get; set; }
        }
    }
}
