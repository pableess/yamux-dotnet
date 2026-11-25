using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Running;
using Nerdbank.Streams;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using Yamux;

namespace Yamux.Benchmark;

public static class Program
{
    public static void Main(string[] args)
    {
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
    }
}

public enum NerdbankFlowControlMode
{
    NoBackpressure,
    Window256K,
    Window1M,
}

internal static class Bench
{
    /// <summary>
    /// Application write size. 32 KB keeps the workload comparable to earlier
    /// runs. yamux (with MaxDataFrameSize = 32 KB) and Go put one write in one
    /// frame. Nerdbank.Streams caps frame payloads at 20 KB, so its cable
    /// format splits each 32 KB write into a 20 KB and a 12 KB frame.
    /// </summary>
    public const int ChunkSize = 32 * 1024;

    /// <summary>Matches the yamux protocol initial window and the Go default.</summary>
    public const int ReceiveWindow = 256 * 1024;

    public const int ChunksPerMB = 1024 * 1024 / ChunkSize;

    public static SessionOptions CreateYamuxOptions() => new()
    {
        EnableKeepAlive = false,
        DefaultChannelOptions = new SessionChannelOptions
        {
            MaxDataFrameSize = ChunkSize,
            ReceiveWindowSize = ReceiveWindow,
            ReceiveWindowUpperBound = ReceiveWindow,
            AutoTuneReceiveWindowSize = false,
        },
    };

    public static MultiplexingStream.Options CreateNerdbankOptions(NerdbankFlowControlMode mode) => new()
    {
        // v1 predates backpressure and ignores the receiving window. v3 keeps
        // backpressure but drops the initial handshake round-trip.
        ProtocolMajorVersion = mode == NerdbankFlowControlMode.NoBackpressure ? 1 : 3,
        DefaultChannelReceivingWindowSize = NerdbankWindowBytes(mode),
    };

    public static MultiplexingStream.ChannelOptions CreateNerdbankChannelOptions(NerdbankFlowControlMode mode) => new()
    {
        ChannelReceivingWindowSize = mode == NerdbankFlowControlMode.NoBackpressure ? null : NerdbankWindowBytes(mode),
    };

    private static long NerdbankWindowBytes(NerdbankFlowControlMode mode) => mode switch
    {
        NerdbankFlowControlMode.Window256K => 256 * 1024,
        NerdbankFlowControlMode.Window1M => 1024 * 1024,
        _ => ReceiveWindow,
    };

    public static async Task WriteYamuxAsync(IWriteOnlySessionChannel channel, byte[] buffer, int chunks)
    {
        for (int i = 0; i < chunks; i++)
        {
            await channel.WriteAsync(buffer);
        }
    }

    public static async Task DrainYamuxAsync(IReadOnlySessionChannel channel)
    {
        while (true)
        {
            var result = await channel.Input.ReadAsync();
            channel.Input.AdvanceTo(result.Buffer.End, result.Buffer.End);
            if (result.IsCompleted)
            {
                return;
            }
        }
    }

    public static async Task WriteNerdbankAsync(MultiplexingStream.Channel channel, byte[] buffer, int chunks)
    {
        for (int i = 0; i < chunks; i++)
        {
            await channel.Output.WriteAsync(buffer);
            await channel.Output.FlushAsync();
        }

        channel.Output.Complete();
    }

    public static async Task DrainNerdbankAsync(MultiplexingStream.Channel channel)
    {
        while (true)
        {
            var result = await channel.Input.ReadAsync();
            channel.Input.AdvanceTo(result.Buffer.End, result.Buffer.End);
            if (result.IsCompleted)
            {
                return;
            }
        }
    }
}

internal static class BenchmarkPaths
{
    public static string RepoRoot { get; } = FindRepoRoot();

    public static string GoServer { get; } = Path.Combine(
        RepoRoot,
        "benchmarks",
        "bin",
        OperatingSystem.IsWindows() ? "go-server.exe" : "go-server");

    public static string CSharpServer { get; } = Path.Combine(
        RepoRoot,
        "benchmarks",
        "bin",
        "csharp-server",
        OperatingSystem.IsWindows() ? "Yamux.Benchmark.Server.exe" : "Yamux.Benchmark.Server");

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null
            && !File.Exists(Path.Combine(dir.FullName, "Yamux.slnx"))
            && !File.Exists(Path.Combine(dir.FullName, "Yamux.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Cannot find Yamux.slnx");
    }
}

internal sealed class ExternalServer : IDisposable
{
    private readonly Process _process;

    private ExternalServer(Process process, int port)
    {
        _process = process;
        Port = port;
    }

    public int Port { get; }

    public static ExternalServer Start(string exePath, params string[] args)
    {
        if (!File.Exists(exePath))
        {
            throw new FileNotFoundException(
                $"External benchmark server not found at '{exePath}'. Run benchmarks/compare.sh or benchmarks/compare.ps1 first.",
                exePath);
        }

        var psi = new ProcessStartInfo(exePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        var process = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {exePath}");
        process.ErrorDataReceived += static (_, _) => { };
        process.BeginErrorReadLine();

        var line = process.StandardOutput.ReadLine();
        if (line is null || !line.StartsWith("LISTENING:", StringComparison.Ordinal))
        {
            try
            {
                process.Kill();
            }
            catch
            {
            }

            process.Dispose();
            throw new InvalidOperationException($"Server '{exePath}' did not report a port. Output: {line}");
        }

        var port = int.Parse(line.AsSpan("LISTENING:".Length));
        return new ExternalServer(process, port);
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

[SimpleJob(RunStrategy.Throughput, launchCount: 1, warmupCount: 3, iterationCount: 8)]
[MemoryDiagnoser]
public class YamuxBenchmarks
{
    private byte[] _buffer = Array.Empty<byte>();
    private Socket? _listener;
    private int _listenerPort;
    private ExternalServer? _csharpServer;
    private ExternalServer? _goServer;

    [Params(1, 50, 500)]
    public int MBs { get; set; } = 50;

    [Params(1, 5, 20)]
    public int Streams { get; set; } = 5;

    private long TotalBytes => (long)MBs * 1024 * 1024;

    private int TotalChunks => MBs * Bench.ChunksPerMB;

    private int ChunksForStream(int stream) =>
        TotalChunks / Streams + (stream < TotalChunks % Streams ? 1 : 0);

    private ExternalServer CSharpServer =>
        _csharpServer ?? throw new InvalidOperationException("C# server was not started in GlobalSetup.");

    private ExternalServer GoServer =>
        _goServer ?? throw new InvalidOperationException("Go server was not started in GlobalSetup.");

    [GlobalSetup]
    public void Setup()
    {
        _buffer = new byte[Bench.ChunkSize];
        new Random(42).NextBytes(_buffer);

        _listener = new Socket(SocketType.Stream, ProtocolType.Tcp);
        _listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        _listener.Listen(128);
        _listenerPort = ((IPEndPoint)_listener.LocalEndPoint!).Port;

        // Start external peers outside the measured method. The Go server is
        // the same binary used by the Go-to-Go baseline; the C# server keeps
        // the C#-to-C# topology identical to C#-to-Go.
        _csharpServer = ExternalServer.Start(BenchmarkPaths.CSharpServer, "--protocol", "yamux");
        _goServer = ExternalServer.Start(BenchmarkPaths.GoServer);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _listener?.Dispose();
        _csharpServer?.Dispose();
        _goServer?.Dispose();
    }

    /// <summary>Raw loopback TCP with the same total payload. One byte stream, no multiplexing.</summary>
    [Benchmark(Baseline = true)]
    public async Task RawTcpAsync()
    {
        var accept = _listener!.AcceptAsync();
        using var client = await ConnectAsync(_listenerPort);

        var serverTask = Task.Run(async () =>
        {
            using var socket = await accept;
            socket.NoDelay = true;
            using var stream = new NetworkStream(socket, ownsSocket: false);
            for (int i = 0; i < TotalChunks; i++)
            {
                await stream.WriteAsync(_buffer);
            }
        });

        using var clientStream = new NetworkStream(client, ownsSocket: false);
        byte[] readBuffer = new byte[Bench.ChunkSize];
        long remaining = TotalBytes;
        while (remaining > 0)
        {
            int read = await clientStream.ReadAsync(readBuffer);
            if (read == 0)
            {
                throw new EndOfStreamException("Raw TCP server closed before all bytes were received.");
            }

            remaining -= read;
        }

        await serverTask;
    }

    /// <summary>C# client to C# server, both peers in the benchmark process.</summary>
    [Benchmark]
    public async Task YamuxInProcAsync()
    {
        var accept = _listener!.AcceptAsync();
        using var client = await ConnectAsync(_listenerPort);

        var serverTask = Task.Run(async () =>
        {
            using var socket = await accept;
            socket.NoDelay = true;
            await RunYamuxServerAsync(socket);
        });

        await RunYamuxClientAsync(client);
        await serverTask;
    }

    /// <summary>C# client to a pre-warmed C# server process.</summary>
    [Benchmark]
    public async Task YamuxSplitAsync()
    {
        using var client = await ConnectAsync(CSharpServer.Port);
        await RunYamuxClientAsync(client);
    }

    /// <summary>C# client to a pre-warmed Go server process.</summary>
    [Benchmark]
    public async Task YamuxToGoAsync()
    {
        using var client = await ConnectAsync(GoServer.Port);
        await RunYamuxClientAsync(client);
    }

    /// <summary>C# client to C# server over in-memory System.IO.Pipelines.</summary>
    [Benchmark]
    public async Task MemoryYamuxAsync()
    {
        var clientPipe = new Pipe();
        var serverPipe = new Pipe();
        var clientTransport = new DuplexPipe(serverPipe.Reader, clientPipe.Writer);
        var serverTransport = new DuplexPipe(clientPipe.Reader, serverPipe.Writer);

        var serverTask = Task.Run(async () =>
        {
            await using var session = serverTransport.AsYamuxSession(isClient: false, options: Bench.CreateYamuxOptions());
            session.Start();
            await RunYamuxServerSessionAsync(session);
        });

        await using (var clientSession = clientTransport.AsYamuxSession(isClient: true, options: Bench.CreateYamuxOptions()))
        {
            clientSession.Start();
            await RunYamuxClientSessionAsync(clientSession);
        }

        await serverTask;
    }

    private async Task RunYamuxClientAsync(Socket socket)
    {
        await using var session = socket.AsYamuxSession(isClient: true, leaveOpen: false, options: Bench.CreateYamuxOptions());
        session.Start();
        await RunYamuxClientSessionAsync(session);
    }

    private async Task RunYamuxClientSessionAsync(Session session)
    {
        var tasks = new Task[Streams];
        for (int i = 0; i < Streams; i++)
        {
            int stream = i;
            tasks[i] = Task.Run(async () =>
            {
                var channel = await session.OpenChannelAsync();
                try
                {
                    int chunks = ChunksForStream(stream);
                    await Bench.WriteYamuxAsync(channel, _buffer, chunks);
                    channel.Close();

                    if (!await channel.WhenRemoteCloseAsync(TimeSpan.FromSeconds(30)))
                    {
                        throw new TimeoutException($"Stream {stream} did not receive the remote FIN.");
                    }
                }
                finally
                {
                    channel.Dispose();
                }
            });
        }

        await Task.WhenAll(tasks);
    }

    private async Task RunYamuxServerAsync(Socket socket)
    {
        await using var session = socket.AsYamuxSession(isClient: false, leaveOpen: false, options: Bench.CreateYamuxOptions());
        session.Start();
        await RunYamuxServerSessionAsync(session);
    }

    private static async Task RunYamuxServerSessionAsync(Session session)
    {
        var drains = new List<Task>();
        try
        {
            while (true)
            {
                var channel = await session.AcceptAsync();
                drains.Add(Task.Run(async () =>
                {
                    try
                    {
                        await Bench.DrainYamuxAsync(channel);
                    }
                    finally
                    {
                        try
                        {
                            channel.Close();
                        }
                        catch
                        {
                        }

                        channel.Dispose();
                    }
                }));
            }
        }
        catch
        {
            // The client closed the session; drain what was accepted and return.
        }

        await Task.WhenAll(drains);
    }

    private static async Task<Socket> ConnectAsync(int port)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        socket.NoDelay = true;
        try
        {
            await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port));
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
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

[SimpleJob(RunStrategy.Throughput, launchCount: 1, warmupCount: 3, iterationCount: 8)]
[MemoryDiagnoser]
public class NerdbankBenchmarks
{
    private byte[] _buffer = Array.Empty<byte>();
    private ExternalServer? _server;

    [Params(1, 50, 500)]
    public int MBs { get; set; } = 50;

    [Params(1, 5, 20)]
    public int Streams { get; set; } = 5;

    /// <summary>
    /// Nerdbank.Streams flow-control modes: protocol v1 has no per-channel
    /// backpressure; v3 adds backpressure. The two v3 windows compare the
    /// default 1 MB window with yamux's 256 KB window.
    /// </summary>
    [Params(
        NerdbankFlowControlMode.NoBackpressure,
        NerdbankFlowControlMode.Window256K,
        NerdbankFlowControlMode.Window1M)]
    public NerdbankFlowControlMode Mode { get; set; }

    private int TotalChunks => MBs * Bench.ChunksPerMB;

    private int ChunksForStream(int stream) =>
        TotalChunks / Streams + (stream < TotalChunks % Streams ? 1 : 0);

    private ExternalServer Server =>
        _server ?? throw new InvalidOperationException("Nerdbank server was not started in GlobalSetup.");

    [GlobalSetup]
    public void Setup()
    {
        _buffer = new byte[Bench.ChunkSize];
        new Random(43).NextBytes(_buffer);

        _server = ExternalServer.Start(
            BenchmarkPaths.CSharpServer,
            "--protocol",
            "nerdbank",
            "--protocol-version",
            Mode == NerdbankFlowControlMode.NoBackpressure ? "1" : "3",
            "--window-kb",
            Mode == NerdbankFlowControlMode.Window1M ? "1024" : "256");
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _server?.Dispose();
    }

    /// <summary>Nerdbank.Streams client to a pre-warmed Nerdbank.Streams server process.</summary>
    [Benchmark]
    public async Task TcpNerdbankSplitAsync()
    {
        using var socket = await ConnectAsync(Server.Port);
        await using var stream = new NetworkStream(socket, ownsSocket: false);
        await using var mux = await MultiplexingStream.CreateAsync(
            stream,
            Bench.CreateNerdbankOptions(Mode),
            CancellationToken.None);

        await RunNerdbankClientAsync(mux);
    }

    /// <summary>Nerdbank.Streams over an in-memory full-duplex stream pair.</summary>
    [Benchmark]
    public async Task MemoryNerdbankAsync()
    {
        var pair = FullDuplexStream.CreatePair();
        using var clientStream = pair.Item1;
        using var serverStream = pair.Item2;
        var options = Bench.CreateNerdbankOptions(Mode);

        // Start both handshakes before awaiting either one. Protocol v1 performs
        // a bidirectional handshake, so creating (and waiting for) one side
        // before the other would deadlock.
        var serverMuxTask = MultiplexingStream.CreateAsync(serverStream, options, CancellationToken.None);
        var clientMuxTask = MultiplexingStream.CreateAsync(clientStream, options, CancellationToken.None);

        await using var serverMux = await serverMuxTask;
        var serverTask = Task.Run(() => RunNerdbankServerAsync(serverMux));

        await using (var clientMux = await clientMuxTask)
        {
            await RunNerdbankClientAsync(clientMux);
        }

        await serverTask;
    }

    private async Task RunNerdbankClientAsync(MultiplexingStream mux)
    {
        var channelOptions = Bench.CreateNerdbankChannelOptions(Mode);
        var tasks = new Task[Streams];
        for (int i = 0; i < Streams; i++)
        {
            int stream = i;
            tasks[i] = Task.Run(async () =>
            {
                var channel = await mux.OfferChannelAsync(string.Empty, channelOptions, CancellationToken.None);
                try
                {
                    await Bench.WriteNerdbankAsync(channel, _buffer, ChunksForStream(stream));

                    // The server disposes the channel after it observes EOF, which
                    // completes our side and guarantees the data left the process.
                    await channel.Completion.WaitAsync(TimeSpan.FromSeconds(30));
                }
                finally
                {
                    channel.Dispose();
                }
            });
        }

        await Task.WhenAll(tasks);
    }

    private async Task RunNerdbankServerAsync(MultiplexingStream mux)
    {
        var channelOptions = Bench.CreateNerdbankChannelOptions(Mode);
        var drains = new List<Task>();
        using var acceptCts = new CancellationTokenSource();
        _ = mux.Completion.ContinueWith(
            _ => acceptCts.Cancel(),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        try
        {
            while (true)
            {
                var channel = await mux.AcceptChannelAsync(string.Empty, channelOptions, acceptCts.Token);
                drains.Add(Task.Run(async () =>
                {
                    try
                    {
                        await Bench.DrainNerdbankAsync(channel);
                    }
                    finally
                    {
                        channel.Dispose();
                    }
                }));
            }
        }
        catch
        {
            // Client closed the multiplexing stream; finish the accepted channels.
        }

        await Task.WhenAll(drains);
    }

    private static async Task<Socket> ConnectAsync(int port)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        socket.NoDelay = true;
        try
        {
            await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port));
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
