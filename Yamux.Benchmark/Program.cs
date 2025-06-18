using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;

namespace Yamux.Benchmark
{
    public class Program
    {
        public static void Main(string[] args)
        {
            //var summary = BenchmarkRunner.Run<LockBenchmark>();

            //var config = DefaultConfig.Instance.AddJob(Job.MediumRun.WithLaunchCount(1).WithToolchain(InProcessEmitToolchain.Instance));

            var summary = BenchmarkRunner.Run<Yamux>();
        }

        //public class ThroughputColumn : IColumn
        //{
        //    public string Id => "Throughput";

        //    public string ColumnName => "Throughput";

        //    public bool AlwaysShow => true;

        //    public ColumnCategory Category => ColumnCategory.Metric;

        //    public int PriorityInCategory => 0;

        //    public bool IsNumeric => true;

        //    public UnitType UnitType => UnitType.Size;

        //    public string Legend => "MBs/sec";

        //    public string GetValue(Summary summary, BenchmarkCase benchmarkCase)
        //    {
        //        benchmarkCase.
        //    }

        //    public string GetValue(Summary summary, BenchmarkCase benchmarkCase, SummaryStyle style)
        //    {
        //        throw new NotImplementedException();
        //    }

        //    public bool IsAvailable(Summary summary)
        //    {
        //        throw new NotImplementedException();
        //    }

        //    public bool IsDefault(Summary summary, BenchmarkCase benchmarkCase)
        //    {
        //        throw new NotImplementedException();
        //    }
        //}

        [SimpleJob]
        public class Yamux
        {
            Memory<byte> _buffer;

            static int MBs = 50;

            public Yamux()
            {
                Random r = new Random();
                _buffer = new byte[1024 * 32];
                r.NextBytes(_buffer.Span);
            }

            [Benchmark(Baseline = true)]
            public async Task SocketBaselineAsync()
            {
                using Socket ss = new Socket(SocketType.Stream, ProtocolType.Tcp);
                ss.Bind(new IPEndPoint(IPAddress.Loopback, 5000));
                ss.Listen();

                using Socket cs = new Socket(SocketType.Stream, ProtocolType.Tcp);
                await cs.ConnectAsync(new IPEndPoint(IPAddress.Loopback, 5000));


                var serverTask = Task.Run(async () =>
                {
                    using var ss1 = await ss.AcceptAsync();
                    using Stream server = new NetworkStream(ss1);

                    // every 32 iterations is a MB of data (in 32KB chunks)
                    int iterations = MBs * 32;
                    for (int i = 0; i < iterations; i++)
                    {
                        await server.WriteAsync(_buffer);
                    }

                    server.Close();
                });

                var clientTask = Task.Run(async () =>
                {
                    using Stream client = new NetworkStream(cs);

                    byte[] readBuffer = new byte[1024 * 32];
                    try
                    {
                        while (true)
                            await client.ReadAtLeastAsync(readBuffer, 1024, throwOnEndOfStream: true);
                    }
                    catch (Exception)
                    {
                        // end of stream
                    }
                });

                await Task.WhenAll(serverTask, clientTask);
            }

            [Benchmark]
            public async Task YamuxSingleStreamAsync()
            {
                using Socket ss = new Socket(SocketType.Stream, ProtocolType.Tcp);
                ss.Bind(new IPEndPoint(IPAddress.Loopback, 5001));
                ss.Listen();

                using Socket cs = new Socket(SocketType.Stream, ProtocolType.Tcp);
                await cs.ConnectAsync(new IPEndPoint(IPAddress.Loopback, 5001));


                var serverTask = Task.Run(async () =>
                {
                    using var ss1 = await ss.AcceptAsync();
                    using Stream server = new NetworkStream(ss1);
                    using var session = server.AsYamuxSession(false);
                    session.Start();
                    using var channel = await session.OpenChannelAsync(false);

                    // every 32 iterations is a MB of data (in 32KB chunks)
                    int iterations = MBs * 32;
                    for (int i = 0; i < iterations; i++)
                    {
                        await channel.WriteAsync(_buffer);
                    }

                    await channel.CloseAsync();
                    await session.CloseAsync();
                });

                var clientTask = Task.Run(async () =>
                {
                    using Stream client = new NetworkStream(cs);
                    var opt = new SessionOptions { DefaultChannelOptions = new SessionChannelOptions { 
                        MaxDataFrameSize = 1024 * 64,
                        ReceiveWindowSize = 1024 * 1024 * 51, 
                        ReceiveWindowUpperBound = 1024 * 1024 * 51 } };
                    using var session = client.AsYamuxSession(true, options: opt);
                    session.Start();
                    using var channel = await session.AcceptReadOnlyChannelAsync(null);

                    byte[] readBuffer = new byte[1024 * 32];

                    ReadResult res;

                    do
                    {
                        res = await channel.Input.ReadAtLeastAsync(1024);
                        channel.Input.AdvanceTo(res.Buffer.End, res.Buffer.End);
                    } while (!res.IsCompleted);

                    await channel.CloseAsync();
                    await session.CloseAsync();
                });

                await Task.WhenAll(serverTask, clientTask);
            }
        }


    }

    [SimpleJob]
    public class LockBenchmark
    {
        private readonly SemaphoreSlim _semaphore = new SemaphoreSlim(1);
        private readonly Lock _lock = new Lock();


        [Benchmark]
        public void SemaphoreSlimSync()
        {
            try
            {
                _semaphore.Wait();
            }
            finally
            {
                _semaphore.Release();
            }
        }


        [Benchmark]
        public async Task SemaphoreSlimAsync()
        {
            try
            {
                await _semaphore.WaitAsync();
            }
            finally
            {
                _semaphore.Release();
            }
        }

        [Benchmark]
        public void LockStatement()
        {
            lock (_lock)
            {

            }
        }

        [Benchmark]
        public void LockScope()
        {
            using var l = _lock.EnterScope();
        }
    }
}
