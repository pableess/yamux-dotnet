using System.Buffers;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using AwesomeAssertions;

namespace Yamux.Interop.Tests;

[Collection("GoInterop")]
public class GoInteropTests
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    private readonly GoInteropFixture _fixture;

    public GoInteropTests(GoInteropFixture fixture)
    {
        _fixture = fixture;
    }

    private string Binary
    {
        get
        {
            if (_fixture.BinaryPath is null)
            {
                Assert.Skip(_fixture.SkipReason ?? "The Go interoperability helper is unavailable.");
            }

            return _fixture.BinaryPath!;
        }
    }

    [Fact]
    public async Task CsharpClient_GoEcho_SingleStream()
    {
        await using var go = GoProcess.StartServer(Binary, "server-echo", streams: 1, megabytes: 1);
        await using var session = await ConnectClientAsync(go.Port);
        using var channel = await session.OpenChannelAsync(cancellationToken: CancellationToken);

        var data = CreatePattern(seed: 0, length: 1024 * 1024);
        var received = await EchoRoundTripAsync(channel, data);

        received.Should().Equal(data);
    }

    [Fact]
    public async Task CsharpClient_GoEcho_ManyStreams()
    {
        const int streamCount = 4;
        const int bytesPerStream = 256 * 1024;

        await using var go = GoProcess.StartServer(Binary, "server-echo", streams: streamCount, megabytes: 1);
        await using var session = await ConnectClientAsync(go.Port);

        var tasks = Enumerable.Range(0, streamCount).Select(async index =>
        {
            using var channel = await session.OpenChannelAsync(cancellationToken: CancellationToken);
            var data = CreatePattern(seed: index, length: bytesPerStream);
            var received = await EchoRoundTripAsync(channel, data);
            received.Should().Equal(data);
        });

        await Task.WhenAll(tasks);
    }

    [Fact]
    public async Task GoClient_CsharpEcho_SingleStream()
    {
        using var listener = CreateListener(out var port);
        await using var go = GoProcess.StartClient(
            Binary, "client-echo", streams: 1, megabytes: 1, address: $"127.0.0.1:{port}");

        using var socket = await listener.AcceptAsync(CancellationToken);
        await using var session = socket.AsYamuxSession(isClient: false, options: NoKeepAlive());
        session.Start();

        var echoTask = Task.Run(async () =>
        {
            using var channel = await session.AcceptAsync(CancellationToken);
            await EchoAsync(channel);
        }, CancellationToken);

        (await go.WaitForLineAsync("DONE:", TimeSpan.FromSeconds(30))).Should().Be("DONE:1048576");
        await go.WaitForExitAsync(TimeSpan.FromSeconds(10));
        await echoTask;
    }

    [Fact]
    public async Task GoClient_CsharpEcho_ManyStreams()
    {
        const int streamCount = 4;
        const int megabytes = 1;

        using var listener = CreateListener(out var port);
        await using var go = GoProcess.StartClient(
            Binary, "client-echo", streamCount, megabytes, address: $"127.0.0.1:{port}");

        using var socket = await listener.AcceptAsync(CancellationToken);
        await using var session = socket.AsYamuxSession(isClient: false, options: NoKeepAlive());
        session.Start();

        var echoTasks = Enumerable.Range(0, streamCount).Select(_ => Task.Run(async () =>
        {
            using var channel = await session.AcceptAsync(CancellationToken);
            await EchoAsync(channel);
        }, CancellationToken)).ToList();

        (await go.WaitForLineAsync("DONE:", TimeSpan.FromSeconds(30))).Should().Be($"DONE:{megabytes * 1024L * 1024L}");
        await go.WaitForExitAsync(TimeSpan.FromSeconds(10));
        await Task.WhenAll(echoTasks);
    }

    [Fact]
    public async Task GoServer_Send_CsharpReceivesAllData()
    {
        await using var go = GoProcess.StartServer(Binary, "server-send", streams: 1, megabytes: 1);
        await using var session = await ConnectClientAsync(go.Port);

        using var channel = await session.OpenChannelAsync(cancellationToken: CancellationToken);
        var received = await ReadExactlyAsync(channel.Input, 1024 * 1024);

        try
        {
            received.Should().Equal(CreatePattern(seed: 1, length: received.Length));
        }
        finally
        {
            channel.Close();
        }
    }

    [Fact]
    public async Task GoClient_Send_CsharpReceivesAllData()
    {
        using var listener = CreateListener(out var port);
        await using var go = GoProcess.StartClient(
            Binary, "client-send", streams: 1, megabytes: 1, address: $"127.0.0.1:{port}");

        using var socket = await listener.AcceptAsync(CancellationToken);
        await using var session = socket.AsYamuxSession(isClient: false, options: NoKeepAlive());
        session.Start();

        var receiveTask = Task.Run(async () =>
        {
            using var channel = await session.AcceptAsync(CancellationToken);
            var received = await ReadExactlyAsync(channel.Input, 1024 * 1024);
            received.Should().Equal(CreatePattern(seed: 0, length: received.Length));
        }, CancellationToken);

        (await go.WaitForLineAsync("DONE:", TimeSpan.FromSeconds(30))).Should().Be("DONE:1048576");
        await go.WaitForExitAsync(TimeSpan.FromSeconds(10));
        await receiveTask;
    }

    [Fact]
    public async Task CsharpPing_GoServer()
    {
        await using var go = GoProcess.StartServer(Binary, "server-ping", streams: 0, megabytes: 0);
        await using var session = await ConnectClientAsync(go.Port);

        var rtt = await session.PingAsync(CancellationToken);

        rtt.Should().BeGreaterThan(TimeSpan.Zero);
        rtt.Should().BeLessThan(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task GoAway_CsharpOpenThrowsRemoteGoAway()
    {
        await using var go = GoProcess.StartServer(Binary, "server-goaway", streams: 0, megabytes: 0);
        await using var session = await ConnectClientAsync(go.Port);

        await go.WaitForLineAsync("DONE:", TimeSpan.FromSeconds(10));

        var refused = await WaitUntilAsync(async () =>
        {
            try
            {
                using var channel = await session.OpenChannelAsync(cancellationToken: CancellationToken);
                return false;
            }
            catch (SessionException ex) when (ex.ErrorCode == SessionErrorCode.RemoteGoAway)
            {
                return true;
            }
        }, TimeSpan.FromSeconds(5));

        refused.Should().BeTrue();
    }

    [Fact]
    public async Task CustomWindow_GoEcho_LargeTransfer()
    {
        // 1 MiB Go receive window, 4 MiB of data. Exercises window updates in
        // both directions across implementations.
        await using var go = GoProcess.StartServer(
            Binary, "server-echo", streams: 1, megabytes: 4, window: 1024 * 1024);
        await using var session = await ConnectClientAsync(go.Port, new SessionChannelOptions
        {
            ReceiveWindowUpperBound = 4 * 1024 * 1024,
        });
        using var channel = await session.OpenChannelAsync(cancellationToken: CancellationToken);

        var data = CreatePattern(seed: 7, length: 4 * 1024 * 1024);
        var received = await EchoRoundTripAsync(channel, data);

        received.Should().Equal(data);
    }

    [Fact]
    public async Task GoServer_CloseWithoutClientFin_ResetsChannel()
    {
        await using var go = GoProcess.StartServer(
            Binary, "server-send-close", streams: 1, megabytes: 1, closeTimeout: TimeSpan.FromMilliseconds(250));
        await using var session = await ConnectClientAsync(go.Port);

        using var channel = await session.OpenChannelAsync(cancellationToken: CancellationToken);
        var received = await ReadExactlyAsync(channel.Input, 1024 * 1024);
        received.Length.Should().Be(1024 * 1024);

        // Go's StreamCloseTimeout sends RST because this side never sends FIN.
        var reset = await WaitUntilAsync(() => Task.FromResult(channel.IsClosed), TimeSpan.FromSeconds(5));
        reset.Should().BeTrue();

        Func<Task> write = async () => await channel.WriteAsync(new byte[1], CancellationToken);
        await write.Should().ThrowAsync<SessionChannelException>();
    }

    private async Task<Session> ConnectClientAsync(int port, SessionChannelOptions? channelOptions = null)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port), CancellationToken);

        var options = NoKeepAlive();
        if (channelOptions != null)
        {
            options.DefaultChannelOptions = channelOptions;
        }

        var session = socket.AsYamuxSession(isClient: true, options: options);
        session.Start();
        return session;
    }

    private static SessionOptions NoKeepAlive() => new() { EnableKeepAlive = false };

    private static Socket CreateListener(out int port)
    {
        var listener = new Socket(SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen();

        port = ((IPEndPoint)listener.LocalEndPoint!).Port;
        return listener;
    }

    private static async Task<byte[]> EchoRoundTripAsync(IDuplexSessionChannel channel, byte[] data)
    {
        var readTask = ReadExactlyAsync(channel.Input, data.Length);
        await channel.WriteAsync(data, CancellationToken);
        channel.Close();
        return await readTask;
    }

    private static async Task EchoAsync(IDuplexSessionChannel channel)
    {
        while (true)
        {
            var result = await channel.Input.ReadAsync(CancellationToken);
            var completed = result.IsCompleted;

            if (!result.Buffer.IsEmpty)
            {
                foreach (var segment in result.Buffer)
                {
                    await channel.WriteAsync(segment, CancellationToken);
                }
            }

            channel.Input.AdvanceTo(result.Buffer.End);

            if (completed)
            {
                break;
            }
        }

        channel.Close();
    }

    private static async Task<byte[]> ReadExactlyAsync(PipeReader reader, int count)
    {
        var buffer = new byte[count];
        var offset = 0;

        while (offset < count)
        {
            var result = await reader.ReadAsync(CancellationToken);
            var available = (int)Math.Min(result.Buffer.Length, count - offset);

            if (available > 0)
            {
                BuffersExtensions.CopyTo(result.Buffer.Slice(0, available), buffer.AsSpan(offset, available));
                offset += available;
            }

            reader.AdvanceTo(result.Buffer.GetPosition(available));

            if (result.IsCompleted && offset < count)
            {
                throw new EndOfStreamException($"Expected {count} bytes but received {offset}.");
            }
        }

        return buffer;
    }

    private static async Task<bool> WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);

        while (!cts.IsCancellationRequested)
        {
            if (await condition())
            {
                return true;
            }

            try { await Task.Delay(50, cts.Token); }
            catch (OperationCanceledException) { break; }
        }

        return false;
    }

    private static byte[] CreatePattern(int seed, int length)
    {
        var data = new byte[length];
        for (var i = 0; i < data.Length; i++)
        {
            data[i] = (byte)((seed * 31 + i) % 251);
        }

        return data;
    }
}

internal sealed class GoProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly System.Text.StringBuilder _stderr = new();

    public int Port { get; }

    private GoProcess(Process process, int port)
    {
        _process = process;
        Port = port;

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                lock (_stderr)
                {
                    _stderr.AppendLine(e.Data);
                }
            }
        };
        process.BeginErrorReadLine();
    }

    private string CapturedError
    {
        get
        {
            lock (_stderr)
            {
                return _stderr.ToString();
            }
        }
    }

    public static GoProcess StartServer(
        string binary, string mode, int streams, int megabytes, int? window = null, TimeSpan? closeTimeout = null)
    {
        var process = Start(binary, BuildArguments(mode, streams, megabytes, window, closeTimeout) + " -port=0");

        string? line;
        try
        {
            line = process.StandardOutput.ReadLineAsync()
                .WaitAsync(TimeSpan.FromSeconds(20))
                .GetAwaiter()
                .GetResult();
        }
        catch (TimeoutException)
        {
            TryKill(process);
            throw new TimeoutException($"The Go helper did not report a listening port. {ReadError(process)}");
        }

        if (line is null || !line.StartsWith("LISTENING:", StringComparison.Ordinal))
        {
            TryKill(process);
            var error = ReadError(process);
            throw new InvalidOperationException($"Unexpected Go helper output '{line}'. {error}");
        }

        var port = int.Parse(line.AsSpan("LISTENING:".Length));
        return new GoProcess(process, port);
    }

    public static GoProcess StartClient(
        string binary, string mode, int streams, int megabytes, string address, int? window = null)
    {
        var process = Start(binary, BuildArguments(mode, streams, megabytes, window, null) + $" -addr={address}");
        return new GoProcess(process, 0);
    }

    public async Task<string> WaitForLineAsync(string prefix, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);

        while (true)
        {
            string? line;
            try
            {
                line = await _process.StandardOutput.ReadLineAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException($"Timed out waiting for Go helper output '{prefix}'.");
            }

            if (line is null)
            {
                throw new InvalidOperationException(
                    $"The Go helper exited before producing '{prefix}'. {CapturedError}");
            }

            if (line.StartsWith(prefix, StringComparison.Ordinal))
            {
                return line;
            }
        }
    }

    public async Task WaitForExitAsync(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);

        try
        {
            await _process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException("The Go helper did not exit in time.");
        }

        if (_process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"The Go helper exited with code {_process.ExitCode}. {CapturedError}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_process.HasExited)
        {
            TryKill(_process);

            try { await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
            catch { }
        }

        var error = CapturedError;
        if (!string.IsNullOrWhiteSpace(error))
        {
            Console.Error.WriteLine($"[go-interop] {error}");
        }

        _process.Dispose();
    }

    private static Process Start(string binary, string arguments) =>
        Process.Start(new ProcessStartInfo(binary, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        }) ?? throw new InvalidOperationException("Unable to start the Go interoperability helper.");

    private static string BuildArguments(
        string mode, int streams, int megabytes, int? window, TimeSpan? closeTimeout)
    {
        var arguments = $"-mode={mode} -streams={streams} -mb={megabytes} -chunk=32768";

        if (window.HasValue)
        {
            arguments += $" -window={window.Value}";
        }

        if (closeTimeout.HasValue)
        {
            arguments += $" -close-timeout={closeTimeout.Value.TotalMilliseconds}ms";
        }

        return arguments;
    }

    private static string ReadError(Process process)
    {
        try
        {
            return process.StandardError.ReadToEnd();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
    }
}
