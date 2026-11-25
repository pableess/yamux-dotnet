using System.Net;
using System.Net.Sockets;
using Nerdbank.Streams;
using Yamux;

namespace Yamux.Benchmark.Server;

/// <summary>
/// Long-lived peer used by the benchmark process. Every connection is one
/// session; the server drains every channel and then waits for the next
/// connection. Starting this process outside the measured benchmark method
/// keeps process startup and JIT out of the comparison.
/// </summary>
internal static class Program
{
    private const int ChunkSize = 32 * 1024;
    private const int ReceiveWindow = 256 * 1024;

    private static int Main(string[] args)
    {
        string protocol = "yamux";
        int protocolVersion = 1;
        int windowKb = 1024;

        for (int i = 0; i + 1 < args.Length; i += 2)
        {
            switch (args[i])
            {
                case "--protocol":
                    protocol = args[i + 1];
                    break;
                case "--protocol-version":
                    protocolVersion = int.Parse(args[i + 1]);
                    break;
                case "--window-kb":
                    windowKb = int.Parse(args[i + 1]);
                    break;
                default:
                    Console.Error.WriteLine($"Unknown argument: {args[i]}");
                    return 2;
            }
        }

        using var listener = new Socket(SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(128);

        Console.WriteLine($"LISTENING:{((IPEndPoint)listener.LocalEndPoint!).Port}");
        Console.Out.Flush();

        while (true)
        {
            Socket socket;
            try
            {
                socket = listener.Accept();
            }
            catch (SocketException)
            {
                break;
            }

            socket.NoDelay = true;

            try
            {
                if (protocol == "nerdbank")
                {
                    RunNerdbankAsync(socket, protocolVersion, windowKb).GetAwaiter().GetResult();
                }
                else
                {
                    RunYamuxAsync(socket).GetAwaiter().GetResult();
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"connection error: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                socket.Dispose();
            }
        }

        return 0;
    }

    private static async Task RunYamuxAsync(Socket socket)
    {
        var options = new SessionOptions
        {
            EnableKeepAlive = false,
            DefaultChannelOptions = new SessionChannelOptions
            {
                MaxDataFrameSize = ChunkSize,
                AutoTuneReceiveWindowSize = false,
                ReceiveWindowSize = ReceiveWindow,
                ReceiveWindowUpperBound = ReceiveWindow,
            },
        };

        await using var session = socket.AsYamuxSession(isClient: false, leaveOpen: false, options: options);
        session.Start();

        var drains = new List<Task>();
        try
        {
            while (true)
            {
                var channel = await session.AcceptAsync();
                drains.Add(Task.Run(() => DrainYamuxAsync(channel)));
            }
        }
        catch
        {
            // The client closed the session (or it faulted). Either way, finish
            // draining and wait for the next connection.
        }

        await Task.WhenAll(drains);
    }

    private static async Task DrainYamuxAsync(IDuplexSessionChannel channel)
    {
        try
        {
            while (true)
            {
                var result = await channel.Input.ReadAsync();
                channel.Input.AdvanceTo(result.Buffer.End, result.Buffer.End);
                if (result.IsCompleted)
                {
                    break;
                }
            }
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
    }

    private static async Task RunNerdbankAsync(Socket socket, int protocolVersion, int windowKb)
    {
        using var stream = new NetworkStream(socket, ownsSocket: false);
        long window = (long)windowKb * 1024;
        var options = new MultiplexingStream.Options
        {
            ProtocolMajorVersion = protocolVersion,
            DefaultChannelReceivingWindowSize = window,
        };

        await using var mux = await MultiplexingStream.CreateAsync(stream, options, CancellationToken.None);
        var channelOptions = new MultiplexingStream.ChannelOptions
        {
            ChannelReceivingWindowSize = protocolVersion == 1 ? null : window,
        };

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
                drains.Add(Task.Run(() => DrainNerdbankAsync(channel)));
            }
        }
        catch
        {
            // Client closed the multiplexing stream.
        }

        await Task.WhenAll(drains);
    }

    private static async Task DrainNerdbankAsync(MultiplexingStream.Channel channel)
    {
        try
        {
            while (true)
            {
                var result = await channel.Input.ReadAsync();
                channel.Input.AdvanceTo(result.Buffer.End, result.Buffer.End);
                if (result.IsCompleted)
                {
                    break;
                }
            }
        }
        finally
        {
            channel.Dispose();
        }
    }
}
