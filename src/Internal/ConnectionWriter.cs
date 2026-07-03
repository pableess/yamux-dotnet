using System.Diagnostics;
using System.Threading.Channels;
using Yamux.Protocol;

namespace Yamux.Internal
{
internal class ConnectionWriter
    {
        private readonly ITransport _peer;
        private readonly Channel<(Frame frame, TaskCompletionSource tcs)> _writeQueue;
        private readonly Statistics? _stats;
        private YamuxMetrics? _metrics;
        private Task? _runTask;

        internal void SetMetrics(YamuxMetrics? metrics) => _metrics = metrics;

        public ConnectionWriter(ITransport connection, Statistics? stats)
        {
            _peer = connection ?? throw new ArgumentNullException(nameof(connection));
            _stats = stats;
            _metrics = null;

            _writeQueue = Channel.CreateBounded<(Frame, TaskCompletionSource)>(new BoundedChannelOptions(100)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
            });
        }

        public void Start()
        {
            // start the writer loop
            _runTask = Task.Run(async () =>
            {
                byte[] headerBuffer = new byte[FrameHeader.FrameHeaderSize];

                try
                {
                    while (await _writeQueue.Reader.WaitToReadAsync())
                    {
                        if (_writeQueue.Reader.TryRead(out var item))
                        {
                            using var _ = item.frame; // dispose buffer owner after write

                            try
                            {
                                item.frame.Header.WriteTo(headerBuffer);

                                if (Session.SessionTracer.Switch.ShouldTrace(TraceEventType.Verbose))
                                    Session.SessionTracer.TraceEvent(TraceEventType.Verbose, 0, "[Dbg] yamux: writing frame - {0}, payload size = {1}", item.frame.Header.FrameType, item.frame.Header.Length);
                                await _peer.WriteAsync(headerBuffer, default);
                                _metrics?.FramesSent.Add(1);
                                if (!item.frame.Payload.IsEmpty)
                                {
                                    await _peer.WriteAsync(item.frame.Payload, default);

                                    _stats?.UpdateSent((uint)item.frame.Payload.Length);
                                    _metrics?.BytesSent.Add(item.frame.Payload.Length);

                                }
                                item.tcs.TrySetResult();
                            }
                            catch (OperationCanceledException cancelEx)
                            {
                                if (Session.SessionTracer.Switch.ShouldTrace(TraceEventType.Warning))
                                    Session.SessionTracer.TraceEvent(TraceEventType.Warning, 0, "[Warn] yamux: write operation canceled - {0}", cancelEx.Message);
                                item.tcs.TrySetException(cancelEx);
                            }
                            catch (Exception ex)
                            {
                                if (Session.SessionTracer.Switch.ShouldTrace(TraceEventType.Error))
                                    Session.SessionTracer.TraceEvent(TraceEventType.Error, 0, "[Err] yamux: error writing frame - {0}", ex.Message);
                                item.tcs.TrySetException(ex);
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // ignore cancellation
                }
                catch (Exception ex)
                {
                    if (Session.SessionTracer.Switch.ShouldTrace(TraceEventType.Error))
                        Session.SessionTracer.TraceEvent(TraceEventType.Error, 0, "[Err] yamux: error writing frame - {0}", ex.Message);
                }
            });
        }

        public async ValueTask WriteAsync(Frame frame, CancellationToken cancel)
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            if (Session.SessionTracer.Switch.ShouldTrace(TraceEventType.Verbose))
                Session.SessionTracer.TraceEvent(TraceEventType.Verbose, 0, "[Dbg] yamux: Enqueuing frame for write - {0}, payload size = {1}", frame.Header.FrameType, frame.Header.Length);

            await _writeQueue.Writer.WriteAsync((frame, tcs), cancel);

            if (Session.SessionTracer.Switch.ShouldTrace(TraceEventType.Verbose))
                Session.SessionTracer.TraceEvent(TraceEventType.Verbose, 0, "[Dbg] yamux: Frame enqueued for write - {0}, payload size = {1}", frame.Header.FrameType, frame.Header.Length);

            // wait for the write to complete
            await tcs.Task;
            if (Session.SessionTracer.Switch.ShouldTrace(TraceEventType.Verbose))
                Session.SessionTracer.TraceEvent(TraceEventType.Verbose, 0, "[Dbg] yamux: Write completed for frame - {0}, payload size = {1}", frame.Header.FrameType, frame.Header.Length);
        }

        public void EnqueueFrame(Frame frame)
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            if (_writeQueue.Writer.TryWrite((frame, tcs)))
            {
                return;
            }

            // Channel is full - fire-and-forget the write asynchronously
            _ = Task.Run(async () =>
            {
                try
                {
                    await _writeQueue.Writer.WriteAsync((frame, tcs), CancellationToken.None);
                }
                catch (Exception ex)
                {
                    if (Session.SessionTracer.Switch.ShouldTrace(TraceEventType.Warning))
                        Session.SessionTracer.TraceEvent(TraceEventType.Warning, 0, "[Warn] yamux: EnqueueFrame write failed: {0}", ex.Message);
                }
            });
        }

        public async Task StopAsync()
        {
            if (_runTask != null && _runTask.IsCompleted == false)
            {
                _writeQueue.Writer.TryComplete();

                await _runTask;
            }
        }
    }


}