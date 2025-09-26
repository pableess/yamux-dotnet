using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Channels;
using System.Threading.Tasks;
using Yamux.Protocol;

namespace Yamux.Internal
{
    internal class ConnectionWriter
    {
        private readonly ITransport _peer;
        // private readonly CancellationTokenSource _stoppingToken;
        private readonly Channel<(Frame frame, ReusableValueTaskSource tcs)> _writeQueue;
        private readonly Statistics? _stats;
        private readonly ReusableValueTaskSourcePool _tcsPool;
        private Task? _runTask;

        public ConnectionWriter(ITransport connection, Statistics? stats)
        {
            _peer = connection ?? throw new ArgumentNullException(nameof(connection));

            _stats = stats;

            //_stoppingToken = new CancellationTokenSource();
            _writeQueue = Channel.CreateBounded<(Frame, ReusableValueTaskSource)>(new BoundedChannelOptions(100)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
            });

            _tcsPool = new ReusableValueTaskSourcePool();
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
                            try
                            {
                                item.frame.Header.WriteTo(headerBuffer);

                                if (Session.SessionTracer.Switch.ShouldTrace(TraceEventType.Verbose))
                                    Session.SessionTracer.TraceEvent(TraceEventType.Verbose, 0, "[Dbg] yamux: writing frame - {0}, payload size = {1}", item.frame.Header.FrameType, item.frame.Header.Length);
                                await _peer.WriteAsync(headerBuffer, default);
                                if (!item.frame.Payload.IsEmpty)
                                {
                                    await _peer.WriteAsync(item.frame.Payload, default);

                                    _stats?.UpdateSent((uint)item.frame.Payload.Length);

                                }
                                item.tcs.SetResult();
                            }
                            catch (OperationCanceledException cancelEx)
                            {
                                if (Session.SessionTracer.Switch.ShouldTrace(TraceEventType.Warning))
                                    Session.SessionTracer.TraceEvent(TraceEventType.Warning, 0, "[Warn] yamux: write operation canceled - {0}", cancelEx.Message);
                                item.tcs.SetException(cancelEx);
                            }
                            catch (Exception ex)
                            {
                                if (Session.SessionTracer.Switch.ShouldTrace(TraceEventType.Error))
                                    Session.SessionTracer.TraceEvent(TraceEventType.Error, 0, "[Err] yamux: error writing frame - {0}", ex.Message);
                                item.tcs.SetException(ex);
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
            var tcs = _tcsPool.Rent();

            if (Session.SessionTracer.Switch.ShouldTrace(TraceEventType.Verbose))
                Session.SessionTracer.TraceEvent(TraceEventType.Verbose, 0, "[Dbg] yamux: Enqueuing frame for write - {0}, payload size = {1}", frame.Header.FrameType, frame.Header.Length);

            while (await _writeQueue.Writer.WaitToWriteAsync(cancel))
            {
                if (_writeQueue.Writer.TryWrite((frame, tcs)))
                {
                    if (Session.SessionTracer.Switch.ShouldTrace(TraceEventType.Verbose))
                        Session.SessionTracer.TraceEvent(TraceEventType.Verbose, 0, "[Dbg] yamux: Frame enqueued for write - {0}, payload size = {1}", frame.Header.FrameType, frame.Header.Length);
                    break;
                }
                else
                {
                    if (Session.SessionTracer.Switch.ShouldTrace(TraceEventType.Warning))
                        Session.SessionTracer.TraceEvent(TraceEventType.Warning, 0, "[Dbg] yamux: Write queue full, waiting to enqueue frame - {0}", frame.Header.FrameType);
                }
            }

            // wait for the write to complete
            await new ValueTask(tcs, tcs.Version);
            if (Session.SessionTracer.Switch.ShouldTrace(TraceEventType.Verbose))
                Session.SessionTracer.TraceEvent(TraceEventType.Verbose, 0, "[Dbg] yamux: Write completed for frame - {0}, payload size = {1}", frame.Header.FrameType, frame.Header.Length);
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
