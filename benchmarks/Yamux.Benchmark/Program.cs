using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Running;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Linq;
using System.Net;
using System.Net.Sockets;

namespace Yamux.Benchmark
{
    public class Program
    {
        public static async Task Main(string[] args)
        {
            var summary = BenchmarkRunner.Run<Yamux>();

            ////debugging locally
            //Yamux yamux = new Yamux();
            //yamux.Setup();

            //yamux.Streams = 5;
            //yamux.MBs = 50;
            //await yamux.YamuxStreamAsync();

            //yamux.Cleanup();

        }

        [SimpleJob(RunStrategy.Throughput, launchCount: 1, warmupCount: 3, invocationCount: 5)]
        [MemoryDiagnoser]
        public class Yamux
        {
            private Memory<byte> _buffer;
            private string? _goServerPath;

            [Params(1, 50, 500)]
            public int MBs = 50;

            [Params(1, 5, 20)]
            public int Streams = 50;

            private Socket? _serverSock;

            private int _port;

            public Yamux()
            {       
            }

            [GlobalSetup]
            public void Setup()
            {
                Random r = new Random();
                _buffer = new byte[1024 * 32];
                r.NextBytes(_buffer.Span);

                _serverSock = new Socket(SocketType.Stream, ProtocolType.Tcp);
                _serverSock.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                _serverSock.Listen();

                _port = ((IPEndPoint)_serverSock.LocalEndPoint!).Port;

                var dir = new DirectoryInfo(AppContext.BaseDirectory);
                while (dir != null && !dir.EnumerateFiles("Yamux.sln").Any())
                    dir = dir.Parent;
                var repoRoot = dir?.FullName ?? throw new InvalidOperationException("Cannot find Yamux.sln");
                _goServerPath = Path.Combine(repoRoot, "benchmarks", "bin", "go-server.exe");
            }

            [GlobalCleanup]
            public void Cleanup()
            {
                _serverSock?.Close();
                _serverSock?.Dispose();
            }

            [Benchmark(Baseline = true)]
            public async Task SocketBaselineAsync()
            {
                var serverTask = Task.Run(async () =>
                {
                    // accept a connection
                    var sock = await _serverSock!.AcceptAsync();

                    using Stream serverStream = new NetworkStream(sock, true);

                    // every 32 iterations is a MB of data (in 32KB chunks)
                    int iterations = MBs * 32;
                    for (int i = 0; i < iterations; i++)
                    {
                        await serverStream.WriteAsync(_buffer);
                    }

                    serverStream.Close();
                });

                var clientTask = Task.Run(async () =>
                {
                    var sock = new Socket(SocketType.Stream, ProtocolType.Tcp);
                    await sock.ConnectAsync(new IPEndPoint(IPAddress.Loopback, _port));

                    using Stream client = new NetworkStream(sock!, true);

                    byte[] readBuffer = new byte[1024 * 32];
                    try
                    {
                        while (true)
                            await client.ReadAtLeastAsync(readBuffer, 1024, throwOnEndOfStream: true);
                    }
                    catch (EndOfStreamException)
                    {
                    }
                    catch (Exception)
                    {
                        // end of stream
                        throw;
                    }
                });

                await Task.WhenAll(serverTask, clientTask);
            }

            [Benchmark]
            public async Task YamuxStreamAsync()
            {
                var serverTask = Task.Run(async () =>
                { 
                    // accept a connection
                    var sock = await _serverSock!.AcceptAsync();
                    var session = sock.AsYamuxSession(false, keepOpen: false);
                    session.Start();

                    List<Task> channels = new List<Task>();
                    int iterationsPerStream = (MBs * 32) / Streams;

                    for (int i = 0; i < Streams; i++)
                    {
                        channels.Add(Task.Run(async () =>
                        {
                            using var channel = await session.OpenChannelAsync(false);

                            // every 32 iterations is a MB of data (in 32KB chunks)
                            for (int i = 0; i < iterationsPerStream; i++)
                            {
                                await channel.WriteAsync(_buffer);
                            }

                            // since we didn't wait for an ack before writing data, we need to make sure the remote party acknowledged before we send a close
                            var timeout = (await channel.WhenRemoteAckAsync(TimeSpan.FromSeconds(3)) == false) ;
                            if (timeout)
                            {
                                throw new TimeoutException("Timed out waiting for remote ack");
                            }

                            channel.Close();
                        }));
                    }

                    await Task.WhenAll(channels);
                });

                var clientTask = Task.Run(async () =>
                {
                    var sock = new Socket(SocketType.Stream, ProtocolType.Tcp);
                    await sock.ConnectAsync(new IPEndPoint(IPAddress.Loopback, _port));

                    var opt = new SessionOptions
                    {
                        DefaultChannelOptions = new SessionChannelOptions
                        {
                            MaxDataFrameSize = 1024 * 64,
                        }
                    };
                    var session = sock!.AsYamuxSession(true, options: opt, keepOpen: false);
                    session.Start();

                    List<Task> channels = new List<Task>();

                    Task RunChannelAsync(IReadOnlySessionChannel channel)
                    {
                        return Task.Run(async () =>
                        {
                            byte[] readBuffer = new byte[1024 * 32];

                            ReadResult res;

                            do
                            {
                                res = await channel.Input.ReadAtLeastAsync(1024);
                                channel.Input.AdvanceTo(res.Buffer.End, res.Buffer.End);
                            } while (!res.IsCompleted);

                            channel.Close();
                            channel.Dispose();
                        });
                    }

                    // accept streams and read until complete until the session is closed
                    for (int i = 0; i < Streams; i++)
                    {
                        var channel = await session.AcceptReadOnlyChannelAsync(null);
                        channels.Add(RunChannelAsync(channel));
                    }

                    await Task.WhenAll(channels);

                    sock.Close();
                });

                await Task.WhenAll(serverTask, clientTask);
            }

            [Benchmark]
            public async Task CsharpToGoAsync()
            {
                using var goServer = GoServerProcess.Start(_goServerPath!);

                var clientTask = Task.Run(async () =>
                {
                    var sock = new Socket(SocketType.Stream, ProtocolType.Tcp);
                    await sock.ConnectAsync(new IPEndPoint(IPAddress.Loopback, goServer.Port));

                    var opt = new SessionOptions
                    {
                        EnableKeepAlive = false,
                        DefaultChannelOptions = new SessionChannelOptions
                        {
                            MaxDataFrameSize = 1024 * 64,
                        }
                    };
                    var session = sock!.AsYamuxSession(true, options: opt, keepOpen: false);
                    session.Start();

                    List<Task> channels = new List<Task>();
                    int iterationsPerStream = (MBs * 32) / Streams;

                    for (int i = 0; i < Streams; i++)
                    {
                        channels.Add(Task.Run(async () =>
                        {
                            using var channel = await session.OpenChannelAsync(false);

                            for (int j = 0; j < iterationsPerStream; j++)
                            {
                                await channel.WriteAsync(_buffer);
                            }

                            var timeout = (await channel.WhenRemoteAckAsync(TimeSpan.FromSeconds(3)) == false);
                            if (timeout)
                            {
                                throw new TimeoutException("Timed out waiting for remote ack");
                            }

                            channel.Close();
                        }));
                    }

                    await Task.WhenAll(channels);

                    sock.Close();
                });

                await clientTask;
            }
        }

        internal sealed class GoServerProcess : IDisposable
        {
            private readonly Process _process;
            public int Port { get; }

            public static GoServerProcess Start(string exePath)
            {
                var psi = new ProcessStartInfo(exePath)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start Go server");

                var line = proc.StandardOutput.ReadLine() ?? "";
                if (!line.StartsWith("LISTENING:"))
                {
                    proc.Kill();
                    throw new InvalidOperationException($"Unexpected output: {line}");
                }
                var port = int.Parse(line.AsSpan("LISTENING:".Length));
                return new GoServerProcess(proc, port);
            }

            private GoServerProcess(Process process, int port)
            {
                _process = process;
                Port = port;
            }

            public void Dispose()
            {
                if (!_process.HasExited)
                {
                    _process.Kill();
                    _process.WaitForExit(2000);
                }
                _process.Dispose();
            }
        }
    }
}
