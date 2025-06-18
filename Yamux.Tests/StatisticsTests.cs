using FluentAssertions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Yamux.Tests
{
    public class StatisticsTests : IDisposable
    {
        private readonly Statistics _statistics;

        public StatisticsTests()
        {
            _statistics = new Statistics(1000, default); // 1 second interval
        }

        [Fact]
        public void TotalBytesSent_ShouldBeZeroInitially()
        {
            _statistics.TotalBytesSent.Should().Be(0);
        }

        [Fact]
        public void TotalBytesReceived_ShouldBeZeroInitially()
        {
            _statistics.TotalBytesReceived.Should().Be(0);
        }

        [Fact]
        public void UpdateSent_ShouldIncreaseTotalBytesSent()
        {
            _statistics.UpdateSent(100);
            _statistics.TotalBytesSent.Should().Be(100);
        }

        [Fact]
        public void UpdateReceived_ShouldIncreaseTotalBytesReceived()
        {
            _statistics.UpdateReceived(200);
            _statistics.TotalBytesReceived.Should().Be(200);
        }

        [Fact]
        public async Task SendRate_ShouldBeCalculatedCorrectly()
        {
            var stats = new Statistics(1000, default);
            stats.UpdateSent(500);
            await Task.Delay(1010); // Wait for the sample interval
            stats.SendRate.Bytes.Should().Be(500);
        }

        [Fact]
        public async Task ReceiveRate_ShouldBeCalculatedCorrectly()
        {
            _statistics.UpdateReceived(2000);
            await Task.Delay(1000); // Wait for the sample interval
            _statistics.ReceiveRate.Bytes.Should().BeApproximately(2000, 1);
        }

        [Fact]
        public void SampleInterval_ShouldBeSetCorrectly()
        {
            _statistics.SampleInterval.Should().Be(TimeSpan.FromMilliseconds(1000));
        }

        [Fact]
        public void Dispose_ShouldDisposeTimer()
        {
            _statistics.Dispose();
            Action act = () => _statistics.UpdateSent(100);
            act.Should().Throw<ObjectDisposedException>();
        }

        public void Dispose()
        {
            _statistics.Dispose();
        }
    }
}
