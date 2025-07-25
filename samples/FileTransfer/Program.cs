﻿using System.CommandLine;
using System.IO;
using System.Net.Sockets;
using System.Net;
using Yamux;
using Spectre.Console;
using System.IO.Pipelines;
using System.CommandLine.Parsing;
using System.Runtime.CompilerServices;

namespace FileTransfer
{
    internal class Program
    {
        static async Task Main(string[] args)
        {
            var rootCommand = new RootCommand("File Transfer application");

            var serverCommand = new Command("server", "Runs the server");
            serverCommand.SetHandler(async () => 
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

                    Directory.CreateDirectory("serverFiles");

                    while (true) 
                    {
                        await ReadFile(ctx, session);
                    }

                    static async Task ReadFile(StatusContext ctx, Session session)
                    {
                        using var channel = await session.AcceptReadOnlyChannelAsync(default);


                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                using var fs = File.Create($"serverFiles/{Path.GetRandomFileName()}");
                                await channel.Input.AsStream().CopyToAsync(fs);

                                ctx.Status($"File '{fs.Name}' done");
                            }
                            catch (OperationCanceledException) { }
                            catch (Exception ex)
                            {
                                Console.WriteLine(ex.ToString());
                            }
                        });
                    }
                });


            });

            var clientCommand = new Command("client", "Runs the command");
            var dirOpt = new Option<DirectoryInfo?>(
                   name: "--dir",
                   description: "The directory to copy files from.");
            dirOpt.IsRequired = true;
            clientCommand.AddOption(dirOpt);


            clientCommand.SetHandler(async (dir) =>
            {
                using Socket socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, 5000));

                using var link = new NetworkStream(socket);

                using var session = link.AsYamuxSession(true, options: new SessionOptions { EnableStatistics = true, StatisticsSampleInterval = 1000, DefaultChannelOptions = new SessionChannelOptions { ReceiveWindowUpperBound = 8 * 1024 * 1024 } });
                session.Start();

                await AnsiConsole.Status().StartAsync("Writing...", async ctx =>
                {
                    if (session.Stats != null)
                    {
                        session.Stats.Sampled += (o, a) =>
                        {
                            ctx.Status($"Upload {session.Stats?.ReceiveRate} / sec");
                        };
                    }

                    var files = dir?.GetFiles();

                    var tasks = new List<Task>();

                    if (files != null)
                    {
                        foreach (var file in files)
                        {
                            var channel = await session.OpenChannelAsync();

                            tasks.Add(Task.Run(async () =>
                            {
                                try
                                {

                                    await file.OpenRead().CopyToAsync(channel.AsStream());
                                    await channel.CloseAsync();

                                }
                                catch (Exception ex)
                                {
                                    Console.WriteLine(ex);
                                }
                            }));
                        }

                        await Task.WhenAll(tasks);
                    }
                });
            }, dirOpt);

            rootCommand.AddCommand(serverCommand);
            rootCommand.AddCommand(clientCommand);

            await rootCommand.InvokeAsync(args);

        }
    }
}
