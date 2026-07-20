using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Running;
using Nerdbank.Streams;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace Yamux.Benchmark
{
    public class Program
    {
        public static async Task Main(string[] args)
        {
            var summary = BenchmarkRunner.Run<Yamux>();
        }

        [SimpleJob(RunStrategy.Throughput, launchCount: 1, warmupCount: 1, invocationCount: 2)]
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
            public async Task<double> TcpBaselineAsync()
            {
                var sw = new Stopwatch();
                var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                int readyCount = 0;

                void SignalReady()
                {
                    if (Interlocked.Increment(ref readyCount) == 2)
                    {
                        sw.Start();
                        ready.TrySetResult();
                    }
                }

                int iterations = MBs * 32;
                long totalBytes = (long)iterations * 1024 * 32;

                var serverTask = Task.Run(async () =>
                {
                    var sock = await _serverSock!.AcceptAsync();

                    using Stream serverStream = new NetworkStream(sock, true);

                    SignalReady();
                    await ready.Task;

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

                    SignalReady();
                    await ready.Task;

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
                        throw;
                    }
                });

                await Task.WhenAll(serverTask, clientTask);
                sw.Stop();

                return totalBytes / (1024.0 * 1024.0) / sw.Elapsed.TotalSeconds;
            }

            [Benchmark]
            public async Task<double> TcpYamuxAsync()
            {
                var sw = new Stopwatch();
                var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                int readyCount = 0;

                void SignalReady()
                {
                    if (Interlocked.Increment(ref readyCount) == 2)
                    {
                        sw.Start();
                        ready.TrySetResult();
                    }
                }

                int iterationsPerStream = (MBs * 32) / Streams;
                long totalBytes = (long)iterationsPerStream * Streams * 1024 * 32;

                var serverTask = Task.Run(async () =>
                { 
                    var sock = await _serverSock!.AcceptAsync();
                    var session = sock.AsYamuxSession(false, leaveOpen: false);
                    session.Start();

                    SignalReady();
                    await ready.Task;

                    List<Task> channels = new List<Task>();

                    for (int i = 0; i < Streams; i++)
                    {
                        channels.Add(Task.Run(async () =>
                        {
                            using var channel = await session.OpenChannelAsync(false);

                            for (int i = 0; i < iterationsPerStream; i++)
                            {
                                await channel.WriteAsync(_buffer);
                            }

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
                    var session = sock!.AsYamuxSession(true, options: opt, leaveOpen: false);
                    session.Start();

                    SignalReady();
                    await ready.Task;

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

                    for (int i = 0; i < Streams; i++)
                    {
                        var channel = await session.AcceptReadOnlyChannelAsync();
                        channels.Add(RunChannelAsync(channel));
                    }

                    await Task.WhenAll(channels);

                    sock.Close();
                });

                await Task.WhenAll(serverTask, clientTask);
                sw.Stop();

                return totalBytes / (1024.0 * 1024.0) / sw.Elapsed.TotalSeconds;
            }


            [Benchmark]
            public async Task<double> TcpNerdbankAsync()
            {
                var sw = new Stopwatch();
                var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                int readyCount = 0;

                void SignalReady()
                {
                    if (Interlocked.Increment(ref readyCount) == 2)
                    {
                        sw.Start();
                        ready.TrySetResult();
                    }
                }

                int iterationsPerStream = (MBs * 32) / Streams;
                long totalBytes = (long)iterationsPerStream * Streams * 1024 * 32;

                var serverTask = Task.Run(async () =>
                {
                    var sock = await _serverSock!.AcceptAsync();

                    using Stream stream = new NetworkStream(sock, true);
                    var mux = await MultiplexingStream.CreateAsync(stream, new MultiplexingStream.Options
                    {
                        ProtocolMajorVersion = 1,
                    }, CancellationToken.None);

                    SignalReady();
                    await ready.Task;

                    List<Task> channels = new List<Task>();

                    for (int i = 0; i < Streams; i++)
                    {
                        channels.Add(Task.Run(async () =>
                        {
                            var channel = await mux.OfferChannelAsync("", new MultiplexingStream.ChannelOptions());
                            using var channelStream = channel.AsStream();

                            for (int j = 0; j < iterationsPerStream; j++)
                            {
                                await channelStream.WriteAsync(_buffer);
                            }
                            channelStream.Close();
                        }));
                    }

                    await Task.WhenAll(channels);
                });

                var clientTask = Task.Run(async () =>
                {
                    var sock = new Socket(SocketType.Stream, ProtocolType.Tcp);
                    await sock.ConnectAsync(new IPEndPoint(IPAddress.Loopback, _port));

                    using Stream stream = new NetworkStream(sock, true);
                    var mux = await MultiplexingStream.CreateAsync(stream, new MultiplexingStream.Options
                    {
                        ProtocolMajorVersion = 1,
                    }, CancellationToken.None);

                    SignalReady();
                    await ready.Task;

                    List<Task> channels = new List<Task>();

                    for (int i = 0; i < Streams; i++)
                    {
                        channels.Add(Task.Run(async () =>
                        {
                            var channel = await mux.AcceptChannelAsync("", new MultiplexingStream.ChannelOptions());
                            using var channelStream = channel.AsStream();

                            byte[] readBuffer = new byte[1024 * 32];
                            int totalBytes = iterationsPerStream * 1024 * 32;
                            while (totalBytes > 0)
                            {
                                var read = await channelStream.ReadAsync(readBuffer, 0, readBuffer.Length);
                                if (read == 0) break;
                                totalBytes -= read;
                            }
                        }));
                    }

                    await Task.WhenAll(channels);
                });

                await Task.WhenAll(serverTask, clientTask);
                sw.Stop();

                return totalBytes / (1024.0 * 1024.0) / sw.Elapsed.TotalSeconds;
            }

            [Benchmark]
            public async Task<double> GoIntegrationAsync()
            {
                using var goServer = GoServerProcess.Start(_goServerPath!);

                int iterationsPerStream = (MBs * 32) / Streams;
                long totalBytes = (long)iterationsPerStream * Streams * 1024 * 32;

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
                var session = sock!.AsYamuxSession(true, options: opt, leaveOpen: false);
                session.Start();

                var sw = Stopwatch.StartNew();

                List<Task> channels = new List<Task>();

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

                sw.Stop();
                sock.Close();

                return totalBytes / (1024.0 * 1024.0) / sw.Elapsed.TotalSeconds;
            }


            [Benchmark]
            public async Task<double> MemoryYamuxAsync()
            {
                var clientPipe = new Pipe();
                var serverPipe = new Pipe();
                var clientTransport = new DuplexPipe(serverPipe.Reader, clientPipe.Writer);
                var serverTransport = new DuplexPipe(clientPipe.Reader, serverPipe.Writer);

                var sw = new Stopwatch();
                var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                int readyCount = 0;

                void SignalReady()
                {
                    if (Interlocked.Increment(ref readyCount) == 2)
                    {
                        sw.Start();
                        ready.TrySetResult();
                    }
                }

                int iterationsPerStream = (MBs * 32) / Streams;
                long totalBytes = (long)iterationsPerStream * Streams * 1024 * 32;

                var serverTask = Task.Run(async () =>
                {
                    var session = serverTransport.AsYamuxSession(false);
                    session.Start();

                    SignalReady();
                    await ready.Task;

                    List<Task> channels = new List<Task>();

                    for (int i = 0; i < Streams; i++)
                    {
                        channels.Add(Task.Run(async () =>
                        {
                            using var channel = await session.OpenChannelAsync(false);

                            for (int j = 0; j < iterationsPerStream; j++)
                            {
                                await channel.WriteAsync(_buffer);
                            }

                            channel.Close();
                        }));
                    }

                    await Task.WhenAll(channels);
                });

                var clientTask = Task.Run(async () =>
                {
                    var session = clientTransport.AsYamuxSession(true);
                    session.Start();

                    SignalReady();
                    await ready.Task;

                    List<Task> channels = new List<Task>();

                    for (int i = 0; i < Streams; i++)
                    {
                        channels.Add(Task.Run(async () =>
                        {
                            var channel = await session.AcceptReadOnlyChannelAsync();

                            byte[] readBuffer = new byte[1024 * 32];

                            ReadResult res;
                            do
                            {
                                res = await channel.Input.ReadAtLeastAsync(1024);
                                channel.Input.AdvanceTo(res.Buffer.End, res.Buffer.End);
                            } while (!res.IsCompleted);

                            channel.Close();
                            channel.Dispose();
                        }));
                    }

                    await Task.WhenAll(channels);
                });

                await Task.WhenAll(serverTask, clientTask);
                sw.Stop();

                return totalBytes / (1024.0 * 1024.0) / sw.Elapsed.TotalSeconds;
            }

            [Benchmark]
            public async Task<double> MemoryNerdbankAsync()
            {
                (var stream1, var stream2) = FullDuplexStream.CreatePair();

                var sw = new Stopwatch();
                var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                int readyCount = 0;

                void SignalReady()
                {
                    if (Interlocked.Increment(ref readyCount) == 2)
                    {
                        sw.Start();
                        ready.TrySetResult();
                    }
                }

                int iterationsPerStream = (MBs * 32) / Streams;
                long totalBytes = (long)iterationsPerStream * Streams * 1024 * 32;

                var serverTask = Task.Run(async () =>
                {
                    var mux = await MultiplexingStream.CreateAsync(stream2, new MultiplexingStream.Options
                    {
                        ProtocolMajorVersion = 1,
                    }, CancellationToken.None);

                    SignalReady();
                    await ready.Task;

                    List<Task> channels = new List<Task>();

                    for (int i = 0; i < Streams; i++)
                    {
                        channels.Add(Task.Run(async () =>
                        {
                            var channel = await mux.AcceptChannelAsync("", new MultiplexingStream.ChannelOptions());
                            using var channelStream = channel.AsStream();

                            byte[] readBuffer = new byte[1024 * 32];
                            int totalBytes = iterationsPerStream * 1024 * 32;
                            while (totalBytes > 0)
                            {
                                var read = await channelStream.ReadAsync(readBuffer, 0, readBuffer.Length);
                                if (read == 0) break;
                                totalBytes -= read;
                            }
                        }));
                    }

                    await Task.WhenAll(channels);
                });

                var clientTask = Task.Run(async () =>
                {
                    var mux = await MultiplexingStream.CreateAsync(stream1, new MultiplexingStream.Options
                    {
                        ProtocolMajorVersion = 1,
                    }, CancellationToken.None);

                    SignalReady();
                    await ready.Task;

                    List<Task> channels = new List<Task>();

                    for (int i = 0; i < Streams; i++)
                    {
                        channels.Add(Task.Run(async () =>
                        {
                            var channel = await mux.OfferChannelAsync("", new MultiplexingStream.ChannelOptions());
                            using var channelStream = channel.AsStream();

                            for (int j = 0; j < iterationsPerStream; j++)
                            {
                                await channelStream.WriteAsync(_buffer);
                            }
                            channelStream.Close();
                        }));
                    }

                    await Task.WhenAll(channels);
                });

                await Task.WhenAll(serverTask, clientTask);
                sw.Stop();

                return totalBytes / (1024.0 * 1024.0) / sw.Elapsed.TotalSeconds;
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

        private sealed class DuplexPipe : IDuplexPipe
        {
            public DuplexPipe(PipeReader input, PipeWriter output)
            {
                Input = input;
                Output = output;
            }

            public PipeReader Input { get; }
            public PipeWriter Output { get; }
        }
    }
}
