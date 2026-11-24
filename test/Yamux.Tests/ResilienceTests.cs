using System.Buffers;
using System.Diagnostics.Metrics;
using System.IO.Pipelines;
using AwesomeAssertions;
using Nerdbank.Streams;
using Yamux.Internal;
using Yamux.Protocol;

namespace Yamux.Tests;

public class ResilienceTests
{
    private static readonly CancellationToken CancellationToken = TestContext.Current.CancellationToken;

    [Fact]
    public async Task CanceledWindowWaiter_DoesNotFaultOnExtend()
    {
        var window = new RemoteDataWindow();
        window.TryConsume(window.Available).Should().Be(Constants.Initial_Window_Size);

        using var cts = new CancellationTokenSource();
        var wait = window.WaitConsumeAsync(1024, TimeSpan.FromSeconds(5), cts.Token);

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await wait);

        // Extending used to call SetResult on the already-canceled waiter and
        // throw, which faulted the whole session from the read loop.
        window.Extend(2048);
        window.Available.Should().Be(2048);

        (await window.WaitConsumeAsync(1024)).Should().Be(1024);
    }

    [Fact]
    public async Task TimedOutWindowWaiter_DoesNotFaultOnExtend()
    {
        var window = new RemoteDataWindow();
        window.TryConsume(window.Available);

        await Assert.ThrowsAsync<TimeoutException>(async () =>
            await window.WaitConsumeAsync(1024, TimeSpan.FromMilliseconds(50)));

        window.Extend(2048);
        window.Available.Should().Be(2048);

        (await window.WaitConsumeAsync(1024)).Should().Be(1024);
    }

    [Fact]
    public async Task ChannelStatistics_CountEachFrameOnce()
    {
        var data = new byte[64 * 1024];
        new Random(42).NextBytes(data);

        (var client, var server) = FullDuplexStream.CreatePair();
        var options = new SessionOptions
        {
            EnableKeepAlive = false,
            DefaultChannelOptions = new SessionChannelOptions { EnableStatistics = true },
        };

        var serverTask = Task.Run(async () =>
        {
            await using var session = new Session(new StreamPeer(server), false, options: options);
            session.Start();
            using var channel = await session.AcceptAsync(CancellationToken);

            var received = 0;
            while (received < data.Length)
            {
                var result = await channel.Input.ReadAsync(CancellationToken);
                received += (int)result.Buffer.Length;
                channel.Input.AdvanceTo(result.Buffer.End);

                if (result.IsCompleted)
                {
                    break;
                }
            }

            return (received, channel.Stats?.TotalBytesReceived ?? 0, channel.Stats?.TotalBytesSent ?? 0);
        }, CancellationToken);

        await using var clientSession = new Session(new StreamPeer(client), true, options: options);
        clientSession.Start();
        using var clientChannel = await clientSession.OpenChannelAsync(cancellationToken: CancellationToken);

        await clientChannel.WriteAsync(data, CancellationToken);

        // The server stops as soon as it has read the expected number of bytes;
        // do not race its session teardown with a local graceful close.
        var (received, bytesReceived, _) = await serverTask.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken);

        received.Should().Be(data.Length, $"received={received}");
        // 64 KiB is sent as four 16 KiB frames. The old code counted the full
        // frame length on every underlying read, inflating this by 4x or more.
        bytesReceived.Should().Be((ulong)data.Length, $"received={received}, bytesReceived={bytesReceived}");
    }

    [Fact]
    public async Task BatchedWrites_HonorTransportFlush()
    {
        var transport = new RecordingTransport();
        await using var session = new Session(transport, isClient: true, options: new SessionOptions
        {
            EnableKeepAlive = false,
            WriteSegmentBatchingEnabled = true,
        });
        session.Start();

        using var channel = await session.OpenChannelAsync(cancellationToken: CancellationToken);
        await channel.FlushWritesAsync(CancellationToken);

        Volatile.Read(ref transport.FlushCount).Should().BeGreaterThanOrEqualTo(1);    }

    [Fact]
    public async Task DisposeAsync_WhenTransportThrows_StillClosesSession()
    {
        var session = new Session(new ThrowingCloseTransport(), isClient: true, options: new SessionOptions
        {
            EnableKeepAlive = false,
        });
        session.Start();

        Func<Task> dispose = async () => await session.DisposeAsync();

        await dispose.Should().NotThrowAsync();
        session.IsClosed.Should().BeTrue();
    }

    [Fact]
    public async Task Metrics_RecordFramesSent()
    {
        long framesSent = 0;

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "Yamux")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
        {
            if (instrument.Name == "yamux.frames.sent")
            {
                Interlocked.Add(ref framesSent, value);
            }
        });
        listener.Start();

        (var client, var server) = FullDuplexStream.CreatePair();

        var serverTask = Task.Run(async () =>
        {
            await using var session = new Session(new StreamPeer(server), false, options: new SessionOptions { EnableKeepAlive = false });
            session.Start();
            using var channel = await session.AcceptAsync(CancellationToken);
            var result = await channel.Input.ReadAsync(CancellationToken);
            channel.Input.AdvanceTo(result.Buffer.End);
        }, CancellationToken);

        await using var clientSession = new Session(new StreamPeer(client), true, options: new SessionOptions { EnableKeepAlive = false });
        clientSession.Start();
        using var clientChannel = await clientSession.OpenChannelAsync(cancellationToken: CancellationToken);
        await clientChannel.WriteAsync(new byte[64], CancellationToken);
        await clientChannel.FlushWritesAsync(CancellationToken);

        await serverTask.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken);

        Volatile.Read(ref framesSent).Should().BeGreaterThan(0);
    }

    private sealed class ThrowingCloseTransport : ITransport
    {
        private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<int> ReadAsync(Memory<byte> data, CancellationToken cancellationToken)
        {
            await _closed.Task.ConfigureAwait(false);
            return 0;
        }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public void Close()
        {
            _closed.TrySetResult();
            throw new InvalidOperationException("transport close failed");
        }

        public void Dispose() => throw new InvalidOperationException("transport dispose failed");
    }

    private sealed class RecordingTransport : ITransport
    {
        private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int FlushCount;
        public bool SupportsBatching => true;

        public async ValueTask<int> ReadAsync(Memory<byte> data, CancellationToken cancellationToken)
        {
            await _closed.Task.ConfigureAwait(false);
            return 0;
        }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask WriteAsync(ReadOnlySequence<byte> data, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask FlushAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref FlushCount);
            return ValueTask.CompletedTask;
        }

        public void Close() => _closed.TrySetResult();

        public void Dispose() => _closed.TrySetResult();
    }
}
