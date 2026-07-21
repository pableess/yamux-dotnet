using AwesomeAssertions;
using Bogus;
using System.Buffers;
using System.IO.Pipelines;
using System.Text;

namespace Yamux.Tests;

public class PipeTransportTests
{
    private static (IDuplexPipe Client, IDuplexPipe Server) CreatePipePair()
    {
        var clientPipe = new Pipe();
        var serverPipe = new Pipe();

        var client = new DuplexPipe(serverPipe.Reader, clientPipe.Writer);
        var server = new DuplexPipe(clientPipe.Reader, serverPipe.Writer);

        return (client, server);
    }

    [Fact]
    public async Task SingleOneWayTest()
    {
        var faker = new Faker();
        var data = faker.Random.Chars(count: 1024 * 750);
        var buffer = Encoding.UTF8.GetBytes(data).AsMemory();
        var result = new byte[buffer.Length].AsMemory();

        (var client, var server) = CreatePipePair();

        var serverTask = Task.Run(async () =>
        {
            await using var serverSession = server.AsYamuxSession(false);
            serverSession.Start();

            using var channel = await serverSession.AcceptAsync();

            long index = 0;
            ReadResult res;
            do
            {
                res = await channel.Input.ReadAsync(TestContext.Current.CancellationToken);
                if (res.Buffer.Length > 0)
                {
                    res.Buffer.CopyTo(result.Slice((int)index, (int)res.Buffer.Length).Span);
                    index += res.Buffer.Length;
                    channel.Input.AdvanceTo(res.Buffer.End, res.Buffer.End);
                }
            } while (!res.IsCanceled && !res.IsCompleted);

            channel.Close();
            await channel.WhenRemoteCloseAsync(TimeSpan.FromSeconds(1));
            channel.Dispose();
        }, TestContext.Current.CancellationToken);

        var clientTask = Task.Run(async () =>
        {
            await using var clientSession = client.AsYamuxSession(true);
            clientSession.Start();

            using var channel = await clientSession.OpenChannelAsync();

            var size = buffer.Length;
            int current = 0;
            int chunkSize = 1024 * 4;

            while (current < size)
            {
                if (buffer.Length > chunkSize)
                {
                    var end = current + chunkSize;
                    var slice = buffer.Slice(current, end >= buffer.Length ? buffer.Length - current : chunkSize);
                    await channel.WriteAsync(slice, TestContext.Current.CancellationToken);
                }
                current += chunkSize;
            }

            channel.Close();
            await channel.WhenRemoteCloseAsync(TimeSpan.FromSeconds(1));
        }, TestContext.Current.CancellationToken);

        await Task.WhenAll(serverTask, clientTask);
        result.ToArray().Should().BeEquivalentTo(buffer.ToArray());
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
