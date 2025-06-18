using AwesomeAssertions;
using Yamux.Internal;

namespace Yamux.Tests
{
    public class DataWindowTests
    {
        [Fact]
        public void TryConsumeTest_Success()
        {
            var dw = new RemoteDataWindow();
            dw.Available.Should().Be(256 * 1024);

            var result = dw.TryConsume(48 * 1024);
            result.Should().Be(48 * 1024);

            dw.Available.Should().Be((256 * 1024) - (48 * 1024));
        }

        [Fact]
        public void TryConsumeTest_None()
        {
            var dw = new RemoteDataWindow();
            dw.TryConsume(dw.Available);

            var result = dw.TryConsume(1024);
            result.Should().Be(0);
            dw.Available.Should().Be(0);
        }

        [Fact]
        public void TryConsumeTest_Partial()
        {
            var dw = new RemoteDataWindow();
            var available = dw.Available;

            var result = dw.TryConsume(available + (1024 * 16));
            result.Should().Be(available);
            dw.Available.Should().Be(0);
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "xUnit1031:Do not use blocking task operations in test method", Justification = "Thread synchronization testing")]
        [Fact]
        public void WaitConsumeTest_UnBlockedSuccess()
        {
            var dw = new RemoteDataWindow();
            dw.TryConsume(dw.Available);

            uint consumed = 0;

            var task = Task.Run(() =>
            {
                consumed = dw.WaitConsume(48 * 1024, TimeSpan.FromMilliseconds(120));
            });

            Thread.Sleep(5); // Simulate some delay, to allow thread to start

            consumed.Should().Be(0);
            dw.Extend(24 * 1024);
            task.Wait(100);
            consumed.Should().Be(24 * 1024);
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "xUnit1031:Do not use blocking task operations in test method", Justification = "Thread synchronization testing")]
        [Fact]
        public async Task WaitConsumeTest_UnBlockedMultipleSuccess()
        {
            var dw = new RemoteDataWindow();
            dw.TryConsume(dw.Available);

            uint consumed1 = 0;
            uint consumed2 = 0;

            // 2 threads waiting.. make sure they both execute when window is extended big enough for both of them
            var task1 = Task.Run(() =>
            {
                consumed1 = dw.WaitConsume(256, TimeSpan.FromMilliseconds(1000));
            });
            var task2 = Task.Run(() =>
            {
                consumed2 = dw.WaitConsume(1024, TimeSpan.FromMilliseconds(1000));
            });

            Thread.Sleep(5); // Simulate some delay, to allow thread to start

            consumed1.Should().Be(0);
            consumed1.Should().Be(0);
            dw.Extend(24 * 1024);
            await Task.WhenAll(task1, task2);
            consumed1.Should().Be(256);
            consumed2.Should().Be(1024);

            dw.Available.Should().Be((24 * 1024) - (256 + 1024));
        }

        [Fact]
        public async Task WaitConsumeAsyncTest_None()
        {
            var dw = new RemoteDataWindow();

            // consume full window
            dw.TryConsume(dw.Available);

            var consumeTask = dw.WaitConsumeAsync(1024, TimeSpan.FromMilliseconds(100));
            _ = Task.Run(() =>
            {
                dw.Extend(1024 * 16);
            });

            var result = await consumeTask;
            result.Should().Be(1024);
            dw.Available.Should().Be((1024 * 16) - 1024);
        }

        [Fact]
        public async Task WaitConsumeAyncMixTest()
        {
            var dw = new RemoteDataWindow();

            // consume full window
            dw.TryConsume(dw.Available);

            var consumeTask = dw.WaitConsumeAsync(1024, TimeSpan.FromMilliseconds(100));
            _ = Task.Run(() =>
            {
                dw.Extend(1024 * 16);
            });

            // mixing async with blocking wait, not something that users should do, but lets test it anyhow
            var consumed2 = dw.WaitConsume(2048, TimeSpan.FromMilliseconds(100));

            var consumed1 = await consumeTask;
            consumed1.Should().Be(1024);
            consumed2.Should().Be(2048);
            dw.Available.Should().Be((1024 * 16) - (2048 + 1024));
        }
    }
}
