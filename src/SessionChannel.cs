using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.Design.Serialization;
using System.Diagnostics;
using System.Drawing;
using System.IO.Pipelines;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Yamux.Internal;
using Yamux.Protocol;
using static Yamux.Session;

namespace Yamux;

/// <summary>
/// Manages an independent stream of data over the Yamux session
/// All streams are duplex 
/// </summary>
internal class SessionChannel : IDuplexSessionChannel
{
    public enum ChannelState
    {
       Init,
       LocalOpen, // channel is open on this side, but has not been acknowledged. It is permissible to send data even before the channel has been ACKd
       Open,
       LocalClose, // channel is closed on this side, no new data will be sent, but the channel can still read data until is is closed
       Closed,
    }

    private readonly static TraceSource ChannelTracer = new TraceSource("Yamux.Channel");

    private readonly IChannelSessionAdapter _session;
    private Pipe _inputBuffer;

    private SessionChannelOptions _channelOptions;

    private TimeSpan _writeTimeout = TimeSpan.FromSeconds(75);

    // cancel token to signal write has closed while write is waiting for window
    private CancellationTokenSource _writeClosedCancellation = new CancellationTokenSource();

    private RemoteDataWindow _remoteWindow;

    private Lock _receiveWindowLock = new Lock();
    private Lock _stateLock = new Lock();
    private ManualResetEventSlim _remoteCloseEvent;
    private TaskCompletionSource _remoteCloseTask;
    private uint _receiveWindowMax;

    private volatile bool _disposed;
    private YamuxException? _fault;

    private ChannelState _state;

    private long _timeSinceLastUpdate;

    public bool IsClosed => _state == ChannelState.Closed;

    public Statistics? Stats { get; private set; }


    internal SessionChannel(IChannelSessionAdapter session, uint id, SessionChannelOptions defaultOptions)
    {
        Id = id;
        _session = session;
        _channelOptions = defaultOptions;

        // a default pipe is created if one is not provided.  Use default upper limit of 16MB and resume pipe filling once 4KB has been processed
        _inputBuffer = new Pipe(new PipeOptions(pauseWriterThreshold: _channelOptions.ReceiveWindowUpperBound + 1)); // +1 prevents pause when remote window is exactly filled

        _remoteCloseEvent = new ManualResetEventSlim(false);
        _remoteCloseTask = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        _remoteWindow = new RemoteDataWindow();

         _input = new CountingPipeReader(_inputBuffer.Reader, OnInputBytesConsumed);

        _receiveWindowMax = _channelOptions.ReceiveWindowSize;

        if (_channelOptions.EnableStatistics)
        {
            this.Stats = new Statistics(_channelOptions.StatisticsSampleInterval, default);
        }

        // if the channel options specifies a non default value for initial window size, that needs to be conmmunicated with the remote peer
        ApplyWindowSizeChange(Constants.Initial_Window_Size);
    }
    
    public uint Id { get; }

    private CountingPipeReader _input;
    /// <summary>
    /// The input reader for the channel
    /// </summary>
    public PipeReader Input => _input;

    private ChannelStream? _stream = null;

    /// <summary>
    /// Creates a new stream for the channel over the Input and Output readers. 
    /// </summary>
    /// <param name="leaveOpen">Indicates that the pipe readers will not complete on closing of the stream</param>
    /// <returns></returns>
    public Stream AsStream(bool leaveOpen = false)
    {
        if (_stream == null)
        {
            // create a writable stream over this instance

            _stream = new ChannelStream(this, leaveOpen);
        }
        else if (leaveOpen)
        {
            _stream.LeaveOpen = leaveOpen;
        }

        return _stream;
    }


    /// <summary>
    /// Writes to the remote channel, waits for available window space before writing and is thread safe
    /// </summary>
    /// <param name="buffer"></param>
    /// <param name="cancel"></param>
    /// <returns></returns>
    public async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken? cancel = null)
    {
        cancel?.ThrowIfCancellationRequested();
        this.ValidateStateForWrite();

        var writeClosedToken = _writeClosedCancellation.Token;

        try
        {
            // write each data frame in a chunk
            for (uint i = 0; i < buffer.Length;)
            {
                uint requested = (uint)Math.Min(_channelOptions.MaxDataFrameSize, buffer.Length - i);
                if (Session.SessionTracer.Switch.ShouldTrace(TraceEventType.Verbose))
                    Session.SessionTracer.TraceEvent(TraceEventType.Verbose, 0, $"[Dbg] yamux: Channel {Id} waiting for remote window (requested: {requested})");

                var linkedCancel = CancellationTokenSource.CreateLinkedTokenSource(cancel ?? CancellationToken.None, writeClosedToken).Token;

                uint available = await _remoteWindow.WaitConsumeAsync(requested, _writeTimeout, linkedCancel);
                if (Session.SessionTracer.Switch.ShouldTrace(TraceEventType.Verbose))
                    Session.SessionTracer.TraceEvent(TraceEventType.Verbose, 0, $"[Dbg] yamux: Channel {Id} remote window available (granted: {available})");

                // write the data frame
                var slice = buffer.Slice((int)i, (int)available);

                this.ValidateStateForWrite();

                var dataFrame = Frame.CreateDataFrame(Id, GetSendFlags(), slice);
                if (Session.SessionTracer.Switch.ShouldTrace(TraceEventType.Verbose))
                    Session.SessionTracer.TraceEvent(TraceEventType.Verbose, 0, $"[Dbg] yamux: Channel {Id} sending data frame (size: {slice.Length})");
                await _session.Writer.WriteAsync(dataFrame, cancel ?? default);

                Stats?.UpdateSent((ulong)slice.Length);

                // update the slice index
                i += available;
            }

        }
        catch (OperationCanceledException ex) 
        {
            // the cancellation source here should be ok even if the source has been disposed
            if (writeClosedToken.IsCancellationRequested)
            {
                if (Session.SessionTracer.Switch.ShouldTrace(TraceEventType.Warning))
                    Session.SessionTracer.TraceEvent(TraceEventType.Warning, 0, $"[Warn] yamux: Channel {Id} write canceled - channel closed");
                throw new SessionChannelException(ChannelErrorCode.ChannelWriteClosed, "Channel has been closed for writing.", ex);
            }
            else
            {
                if (Session.SessionTracer.Switch.ShouldTrace(TraceEventType.Warning))
                    Session.SessionTracer.TraceEvent(TraceEventType.Warning, 0, $"[Warn] yamux: Channel {Id} write canceled - operation canceled: {ex.Message}");
                throw;
            }
        }
        catch (ObjectDisposedException ex)
        {
            if (Session.SessionTracer.Switch.ShouldTrace(TraceEventType.Error))
                Session.SessionTracer.TraceEvent(TraceEventType.Error, 0, $"[Err] yamux: Channel {Id} write failed - channel closed: {ex.Message}");
            throw new SessionChannelException(ChannelErrorCode.ChannelClosed, "Channel has been closed.", ex);
        }
        catch (TimeoutException ex)
        {
            if (Session.SessionTracer.Switch.ShouldTrace(TraceEventType.Warning))
                Session.SessionTracer.TraceEvent(TraceEventType.Warning, 0, $"[Warn] yamux: Channel {Id} write timeout: {ex.Message}");
            throw new TimeoutException("Timeout waiting for write access to channel", ex);
        }
    }

    /// <summary>
    /// Flushes all writes on the channel, this flushes the underlying session stream
    /// </summary>
    /// <param name="cancel"></param>
    /// <returns></returns>
    public Task FlushWritesAsync(CancellationToken? cancel = null)
    {
        // TODO: implement connection writer drain/flush
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Abort()
    {
        _writeClosedCancellation.Cancel();

        lock (_stateLock)
        {
            // hard close... fire off a RST to the remote peer
            if (!(_state == ChannelState.Closed || _state == ChannelState.LocalClose))
            {
                this.SendWindowUpdate(0, (Flags)Flags.RST);
            }
        }

        // also make sure everything is closed
        this.CloseRead();
    }


    /// <inheritdoc/>
    public void Close()
    {
        this.ThrowIfDisposed();
        lock (_stateLock)
        {
            if (this._state == ChannelState.Closed || this._state == ChannelState.LocalClose)
            {
                // already closed
                return;
            }
        }
        this.CloseWrite();
    }

    /// <inheritdoc />
    public async Task<bool> WhenRemoteCloseAsync(TimeSpan timeout)
    {
        this.ThrowIfDisposed();

        var task = await Task.WhenAny(_remoteCloseTask.Task, Task.Delay(timeout));
        return task != _remoteCloseTask.Task;
    }

    /// <inheritdoc />
    public bool WaitForRemoteClose(TimeSpan timeout)
    {
        this.ThrowIfDisposed();

        return this._remoteCloseEvent.Wait(timeout);
    }

    /// <summary>
    /// Disposes of the channel and all associated resources
    /// If the channel has not been gracefully closed, data from the peer may be lost
    /// If you would like to gracefully close the channel, call Close() and continue reading data from the channel until it is complete, or call waitForRemoteClose()
    /// </summary>
    public void Dispose()
    {
        if (!this._disposed)
        {
            _disposed = true;

            _writeClosedCancellation.Cancel();
            this.CloseRead();

            // make sure we are disconnected from the session
            this._session.ChannelDisconnect(this);

            _writeClosedCancellation.Dispose();
            Stats?.Dispose();
            Stats = null;
        }
    }

    /// <summary>
    /// Applies a new set of options to the channel.  This is not thread safe and is only intended to be called before the channel has been exposed for reading or writing
    /// </summary>
    /// <param name="options"></param>
    /// <param name="cancel"></param>
    /// <returns></returns>
    /// <exception cref="ValidationException"></exception>
    internal async Task ApplyOptionsAsync(SessionChannelOptions options, CancellationToken cancel)
    {
        options.Validate();

        if (options.ReceiveWindowSize < _channelOptions.ReceiveWindowSize)
        {
            throw new ValidationException("RecevieWindowSize can not be specified as a smaller value than what is specified as the Default channel options on the session");
        }

        _channelOptions = options;

        // if the pipe needs to be swapped with one with new settings, 
        if (_channelOptions.ReceiveWindowUpperBound != options.ReceiveWindowUpperBound)
        {
            // how to coordinate the pipe swap?
            var oldPipe = _inputBuffer;

            // a default pipe is created if one is not provided.  Use default upper limit of 16MB and resume pipe filling once 4KB has been processed
            _inputBuffer = new Pipe(new PipeOptions(pauseWriterThreshold: _channelOptions.ReceiveWindowUpperBound + 1)); // +1 prevents pause when remote window is exactly filled

            // copy anything currently in the buffer
            var copied = await CopyToAsync(oldPipe.Reader, _inputBuffer.Writer, cancel);

            _input = new CountingPipeReader(_inputBuffer.Reader, OnInputBytesConsumed);

            if (Stats == null && _channelOptions.EnableStatistics)
            {
                Stats = new Statistics(_channelOptions.StatisticsSampleInterval, default);
                Stats.UpdateReceived(copied);
            }
        }

        ApplyWindowSizeChange(Constants.Initial_Window_Size);
    }

    internal void CloseWrite()
    {
        lock (_stateLock)
        {
            if (_state == ChannelState.Open) 
            {
                // mark the channel as locally closed
                _state = ChannelState.LocalClose;
                // send a window update with the FIN flag to the remote peer indicating that we have closed our side and not more frames will be sent
                this.SendWindowUpdate(0);

                // FIN has been sent, we should eventually receive a FIN from the remote peer
            }
        }
    }

    internal void UpdateRemoteWindow(uint length, Flags flags)
    {
        if (!_disposed)
        {
            if (Session.SessionTracer.Switch.ShouldTrace(TraceEventType.Verbose))
                Session.SessionTracer.TraceEvent(TraceEventType.Verbose, 0, $"[Dbg] yamux: Channel {Id} updating remote window (increment: {length}, flags: {flags})");
            // update the remote window length
            ProcessIncomingFlags(flags);
            _remoteWindow.Extend(length);
            // complete
        }
    }

    private void ApplyWindowSizeChange(uint previousWindowSize)
    {
        // if an immediate window asjustment should be made then do that
        if (_channelOptions.ReceiveWindowSize != previousWindowSize)
        {
            // send a window update to increase our window size
            lock (_receiveWindowLock)
            {
                var difference = _channelOptions.ReceiveWindowSize - previousWindowSize;

                _receiveWindowMax = _channelOptions.ReceiveWindowSize;

                this.SendWindowUpdate(difference);
            }
        }
    }

    // gets flags to be sent 
    // state should be locked when called
    private Flags GetSendFlags()
    {
        lock (_stateLock)
        {
            var flags = Flags.None;
            switch (this._state)
            {
                case ChannelState.Init:
                    flags |= Flags.SYN;
                    this._state = ChannelState.LocalOpen;
                    break;
                case ChannelState.LocalOpen:
                    flags |= Flags.ACK;
                    this._state = ChannelState.Open;
                    break;
                case ChannelState.LocalClose:
                    flags |= Flags.FIN;
                    break;
            }
            return flags;
        }
    }

    private void ProcessIncomingFlags(Flags flags)
    {
        this.ThrowIfDisposed();

        lock (_stateLock) 
        {
            if (flags.HasFlag(Flags.ACK))
            {
                if (this._state == ChannelState.LocalOpen)
                {
                    this._state = ChannelState.Open;

                    // notify the session that the channel is accepted, by the remote peer
                    _session.ChannelAcknowledge(this, true);
                }
            }
            if (flags.HasFlag(Flags.FIN))
            {
                this.CloseWrite();
                this.CloseRead();
            }
            if (flags.HasFlag((Flags)Flags.RST))
            {
                // rst is either a force close or the remote rejected the channel
                // in this case we can just close the channel
                _fault = new SessionChannelException(ChannelErrorCode.ChannelRejected, "Channel was rejected or foribly closed by the remote peer");
                this.CloseRead();
            }
        }
    }

    internal PipeWriter GetPipeWriter() => _inputBuffer.Writer;

    /// <summary>
    /// Disposes of the channel indicating a session level fault
    /// </summary>
    /// <param name="ex"></param>
    internal void Dispose(SessionException ex)
    {
        _fault = ex;
        this.Dispose();
    } 

    private void CloseRead()
    {
        lock (_stateLock)
        {
            if (this._state != ChannelState.Closed)
            {
                // complete the input pipe
                _inputBuffer.Writer.Complete(_fault);
                // handle the FIN flag
                // signal the remote peer has closed the channel, if anyone is waiting
                _remoteCloseEvent.Set();
                _remoteCloseTask.SetResult();

                _state = ChannelState.Closed;
            }
        }
    }

    private void OnInputBytesConsumed() 
    {
        // default algorithm is when we have processed 1/2 of the receive window, then we can send "more bytes please"
        lock (_receiveWindowLock)
        {
            uint increase = 0;
            var rtt = _session.RTT;

            // auto tune window increase
            if (_channelOptions.AutoTuneReceiveWindowSize
                && rtt.HasValue
                && _timeSinceLastUpdate > 0
                && _timeSinceLastUpdate < rtt.Value.Ticks * 2)
            {
                var previousMax = _receiveWindowMax;
                _receiveWindowMax = Math.Min(_receiveWindowMax * 2, _channelOptions.ReceiveWindowUpperBound);

                if (ChannelTracer.Switch.ShouldTrace(TraceEventType.Verbose))
                    ChannelTracer.TraceEvent(TraceEventType.Verbose, 0, $"[Dbg] yamux: Channel {Id} auto-tune window increase max window size to {_receiveWindowMax} ");
                increase = _receiveWindowMax - previousMax;
            }

            // if we are going to send a window update and increasing the window, we can just send it in a single update
            if (_input.ConsumedBytes > (_receiveWindowMax / 2))
            {
                _timeSinceLastUpdate = Stopwatch.GetTimestamp() - _timeSinceLastUpdate;
                if (ChannelTracer.Switch.ShouldTrace(TraceEventType.Verbose))
                    ChannelTracer.TraceEvent(TraceEventType.Verbose, 0, $"[Dbg] yamux: Channel {Id} sending window update (consumed: {_input.ConsumedBytes}, increase: {increase})");
                this.SendWindowUpdate(_input.ConsumedBytes + increase);

                _input.Reset();
            }
            else if (increase > 0)
            {
                _timeSinceLastUpdate = Stopwatch.GetTimestamp() - _timeSinceLastUpdate;
                if (ChannelTracer.Switch.ShouldTrace(TraceEventType.Verbose))
                    ChannelTracer.TraceEvent(TraceEventType.Verbose, 0, $"[Dbg] yamux: Channel {Id} sending window update (increase only: {increase})");
                this.SendWindowUpdate(increase);
            }
        }
    }

    /// <summary>
    /// Sends a window update to the remote peer
    /// If no flags are specified, the appropriate flags will be added based on the channel state
    /// </summary>
    /// <param name="incrementWindow"></param>
    /// <param name="flags"></param>
    internal void SendWindowUpdate(uint incrementWindow, Flags? flags = null)
    {
        if (Session.SessionTracer.Switch.ShouldTrace(TraceEventType.Verbose))
            Session.SessionTracer.TraceEvent(TraceEventType.Verbose, 0, $"[Dbg] yamux: Channel {Id} sending window update (increment: {incrementWindow})");
        // fire and forget a window update
        var update = Frame.CreateWindowUpdateFrame(this.Id, flags ?? GetSendFlags(), incrementWindow);
        _ = _session.Writer.WriteAsync(update, CancellationToken.None);
    }

    private void ValidateStateForWrite()
    {
        this.ThrowIfDisposed();

        if (_fault != null)
        {
            throw _fault;
        }

        lock (_stateLock)
        {
            if (this._state == ChannelState.Closed)
            {
                throw new SessionChannelException(ChannelErrorCode.ChannelClosed, "SessionChannel is closed");
            }
            if (this._state == ChannelState.LocalClose || _writeClosedCancellation.IsCancellationRequested)
            {
                throw new SessionChannelException(ChannelErrorCode.ChannelWriteClosed, "SessionChannel half closed and can no longer send data");
            }
        }
    }

    private static async Task<ulong> CopyToAsync(PipeReader source, PipeWriter destination, CancellationToken cancellationToken = default)
    {
        ulong totalBytesCopied = 0;

        while (true)
        {
            ReadResult result = await source.ReadAsync(cancellationToken);
            ReadOnlySequence<byte> buffer = result.Buffer;

            if (buffer.Length > 0)
            {
                foreach (var segment in buffer)
                {
                    await destination.WriteAsync(segment, cancellationToken);
                    totalBytesCopied += (ulong)segment.Length;
                }
            }

            source.AdvanceTo(buffer.End);

            if (result.IsCompleted)
            {
                break;
            }
        }
        await source.CompleteAsync();

        return totalBytesCopied;
    }

    private void ThrowIfDisposed() 
    {
        if (this._disposed) throw new ObjectDisposedException("SessionChannel is disposed");
    }
}
