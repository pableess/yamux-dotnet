using System.Buffers;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.IO.Pipelines;
using Yamux.Internal;
using Yamux.Protocol;

namespace Yamux;

internal enum ChannelLocalPhase : byte
{
    None,           // No SYN sent yet
    SynSent,        // SYN sent, waiting to send ACK
    Established,    // Both SYN and ACK sent, normal operation
    WriteClosed,    // FIN sent, write side closed
}

internal enum ChannelRemoteState : byte
{
    None,       // No response from remote yet
    Open,       // Remote has opened (SYN/ACK received)
    ReadClosed, // Remote FIN received, no more data incoming
    Reset,      // RST received, hard close
}

internal class SessionChannel : IDuplexSessionChannel
{
    private readonly static TraceSource ChannelTracer = new TraceSource("Yamux.Channel");

    private readonly IChannelSessionAdapter _session;
    private Pipe _inputBuffer;

    private SessionChannelOptions _channelOptions;

    private TimeSpan _writeTimeout = TimeSpan.FromSeconds(75);

    private CancellationTokenSource _writeClosedCancellation = new CancellationTokenSource();

    private RemoteDataWindow _remoteWindow;

    private Lock _receiveWindowLock = new Lock();
    private Lock _stateLock = new Lock();
    private TaskCompletionSource _remoteCloseTask;
    private TaskCompletionSource _remoteAckTask;
    private CancellationTokenSource? _closeTimeoutCts;
    private uint _receiveWindowMax;

    private volatile bool _disposed;
    private YamuxException? _fault;
    private ValueTask? _pendingPipeFlush;

    private ChannelLocalPhase _localPhase;
    private ChannelRemoteState _remoteState;

    private long _timeSinceLastUpdate = Stopwatch.GetTimestamp();

    public bool IsClosed
    {
        get
        {
            lock (_stateLock)
            {
                return (_localPhase == ChannelLocalPhase.WriteClosed && _remoteState >= ChannelRemoteState.ReadClosed)
                    || _remoteState == ChannelRemoteState.Reset;
            }
        }
    }

    private bool CanRead
    {
        get
        {
            lock (_stateLock)
            {
                return _remoteState >= ChannelRemoteState.Open
                    && _remoteState < ChannelRemoteState.ReadClosed;
            }
        }
    }

    private bool CanWrite
    {
        get
        {
            lock (_stateLock)
            {
                return _localPhase < ChannelLocalPhase.WriteClosed
                    && _remoteState != ChannelRemoteState.Reset;
            }
        }
    }

    public Statistics? Stats { get; private set; }


    internal SessionChannel(IChannelSessionAdapter session, uint id, SessionChannelOptions defaultOptions, ChannelRemoteState initialRemoteState = ChannelRemoteState.None)
    {
        Id = id;
        _session = session;
        _channelOptions = defaultOptions;
        _remoteState = initialRemoteState;
        _writeTimeout = session.StreamSendTimeout;

        _inputBuffer = new Pipe(new PipeOptions(pauseWriterThreshold: _channelOptions.ReceiveWindowUpperBound + 1));

        _remoteCloseTask = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _remoteAckTask = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        _remoteWindow = new RemoteDataWindow();

        _input = new CountingPipeReader(_inputBuffer.Reader, OnInputBytesConsumed);

        _receiveWindowMax = _channelOptions.ReceiveWindowSize;

        if (_channelOptions.EnableStatistics)
        {
            this.Stats = new Statistics(_channelOptions.StatisticsSampleInterval, default);
        }

        ApplyWindowSizeChange(Constants.Initial_Window_Size);
    }

    internal void Close(SessionException ex)
    {
        _fault = ex;
        this.Close();
        this.CompleteRead(ex);
    }

    public uint Id { get; }

    internal uint ReceiveWindowUpperBound => _channelOptions.ReceiveWindowUpperBound;

    private CountingPipeReader _input;
    public PipeReader Input => _input;

    private ChannelStream? _stream = null;

    public Stream AsStream(bool leaveOpen = false)
    {
        if (_stream == null)
        {
            _stream = new ChannelStream(this, leaveOpen);
        }
        else if (leaveOpen)
        {
            _stream.LeaveOpen = leaveOpen;
        }

        return _stream;
    }


    public async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var writeClosedToken = _writeClosedCancellation.Token;

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, writeClosedToken);
        var linkedToken = linkedCts.Token;

        this.ValidateStateForWrite();

        try
        {
            for (uint i = 0; i < buffer.Length;)
            {
                uint requested = (uint)Math.Min(_channelOptions.MaxDataFrameSize, buffer.Length - i);
                if (Session.SessionTracer.Switch.ShouldTrace(TraceEventType.Verbose))
                    Session.SessionTracer.TraceEvent(TraceEventType.Verbose, 0, $"[Dbg] yamux: Channel {Id} waiting for remote window (requested: {requested})");

                uint available = await _remoteWindow.WaitConsumeAsync(requested, _writeTimeout, linkedToken);
                if (Session.SessionTracer.Switch.ShouldTrace(TraceEventType.Verbose))
                    Session.SessionTracer.TraceEvent(TraceEventType.Verbose, 0, $"[Dbg] yamux: Channel {Id} remote window available (granted: {available})");

                var slice = buffer.Slice((int)i, (int)available);

                this.ValidateStateForWrite();

                var dataFrame = Frame.CreateDataFrame(Id, GetSendFlags(), slice);
                if (Session.SessionTracer.Switch.ShouldTrace(TraceEventType.Verbose))
                    Session.SessionTracer.TraceEvent(TraceEventType.Verbose, 0, $"[Dbg] yamux: Channel {Id} sending data frame (size: {slice.Length})");
                await _session.SendFrameAsync(dataFrame, linkedToken);

                Stats?.UpdateSent((ulong)slice.Length);

                i += available;
            }

        }
        catch (OperationCanceledException ex)
        {
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

    public ValueTask FlushWritesAsync(CancellationToken cancellationToken = default)
    {
        return _session.FlushWritesAsync(cancellationToken);
    }

    public void Abort()
    {
        if (_disposed)
            return;

        _writeClosedCancellation.Cancel();

        lock (_stateLock)
        {
            if (_localPhase < ChannelLocalPhase.WriteClosed && _remoteState != ChannelRemoteState.Reset)
            {
                _localPhase = ChannelLocalPhase.WriteClosed;
                this.SendWindowUpdate(0, (Flags)Flags.RST);
            }
        }

        this.CompleteRead(_fault ?? new SessionChannelException(ChannelErrorCode.ChannelRejected, "Channel aborted"));
    }

    public void Close()
    {
        this.ThrowIfDisposed();
        this.CloseWrite();
    }

    public async Task<bool> WhenRemoteCloseAsync(TimeSpan timeout)
    {
        this.ThrowIfDisposed();

        var task = await Task.WhenAny(_remoteCloseTask.Task, Task.Delay(timeout));
        return task == _remoteCloseTask.Task;
    }

    public async Task<bool> WhenRemoteAckAsync(TimeSpan timeout)
    {
        this.ThrowIfDisposed();
        var task = await Task.WhenAny(_remoteAckTask.Task, Task.Delay(timeout));
        return task == _remoteAckTask.Task;
    }

    public void Dispose()
    {
        lock (this._stateLock)
        {
            if (!this._disposed)
            {
                _disposed = true;

                _writeClosedCancellation.Cancel();

                CancelCloseTimeout();

                if (_remoteState < ChannelRemoteState.ReadClosed)
                {
                    _localPhase = ChannelLocalPhase.WriteClosed;
                    _remoteState = ChannelRemoteState.Reset;
                    _inputBuffer.Writer.Complete();
                    _remoteCloseTask.TrySetResult();
                }

                this.SendWindowUpdate(0, (Flags)Flags.RST);

                this._session.ChannelDisconnect(this);

                _writeClosedCancellation.Dispose();
                Stats?.Dispose();
                Stats = null;
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    internal void Accept()
    {
        lock (_stateLock)
        {
            if (_localPhase == ChannelLocalPhase.None && _remoteState >= ChannelRemoteState.Open)
            {
                _localPhase = ChannelLocalPhase.SynSent;
                this.SendWindowUpdate(0);
            }
            else
            {
                throw new SessionChannelException(
                    _localPhase >= ChannelLocalPhase.WriteClosed || _remoteState >= ChannelRemoteState.ReadClosed
                        ? ChannelErrorCode.ChannelClosed
                        : ChannelErrorCode.InvalidChannelState,
                    $"Channel {Id} cannot be accepted in local={_localPhase} remote={_remoteState} state");
            }
        }
    }

    private void CancelCloseTimeout()
    {
        if (_closeTimeoutCts != null)
        {
            _closeTimeoutCts.Cancel();
            _closeTimeoutCts.Dispose();
            _closeTimeoutCts = null;
        }
    }

    internal void RemoteAckReceived()
    {
        lock (_stateLock)
        {
            if (_remoteState == ChannelRemoteState.None)
            {
                _remoteState = ChannelRemoteState.Open;
                _remoteAckTask.TrySetResult();
            }
        }
    }

    internal async Task ApplyOptionsAsync(SessionChannelOptions options, CancellationToken cancel)
    {
        options.Validate();

        if (options.ReceiveWindowSize < _channelOptions.ReceiveWindowSize)
        {
            throw new ValidationException("ReceiveWindowSize can not be specified as a smaller value than what is specified as the Default channel options on the session");
        }

        _channelOptions = options;

        if (_channelOptions.ReceiveWindowUpperBound != options.ReceiveWindowUpperBound)
        {
            var oldPipe = _inputBuffer;

            _inputBuffer = new Pipe(new PipeOptions(pauseWriterThreshold: _channelOptions.ReceiveWindowUpperBound + 1));

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
            if (_localPhase >= ChannelLocalPhase.WriteClosed)
            {
                return;
            }

            _localPhase = ChannelLocalPhase.WriteClosed;
            this.SendWindowUpdate(0);
        }

        var closeTimeout = _session.StreamCloseTimeout;
        if (closeTimeout > TimeSpan.Zero)
        {
            lock (_stateLock)
            {
                if (_remoteState < ChannelRemoteState.ReadClosed)
                {
                    _closeTimeoutCts = new CancellationTokenSource();
                    var cts = _closeTimeoutCts;
                    _ = CloseTimeoutAsync(closeTimeout, cts);
                }
            }
        }
    }

    private async Task CloseTimeoutAsync(TimeSpan timeout, CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(timeout, cts.Token).ConfigureAwait(false);
            ForceCloseTimeout();
        }
        catch (OperationCanceledException) { }
        finally { cts.Dispose(); }
    }

    private void ForceCloseTimeout()
    {
        lock (_stateLock)
        {
            if (_disposed || _remoteState >= ChannelRemoteState.ReadClosed)
                return;

            _localPhase = ChannelLocalPhase.WriteClosed;
            _remoteState = ChannelRemoteState.Reset;

            _writeClosedCancellation.Cancel();
            _inputBuffer.Writer.Complete(_fault ?? new SessionChannelException(ChannelErrorCode.ChannelClosed, "Stream close timeout exceeded"));
            _remoteCloseTask.TrySetResult();
        }

        this.SendWindowUpdate(0, (Flags)Flags.RST);
        _session.ChannelDisconnect(this);
    }

    internal void UpdateRemoteWindow(uint length, Flags flags)
    {
        if (!_disposed)
        {
            if (Session.SessionTracer.Switch.ShouldTrace(TraceEventType.Verbose))
                Session.SessionTracer.TraceEvent(TraceEventType.Verbose, 0, $"[Dbg] yamux: Channel {Id} updating remote window (increment: {length}, flags: {flags})");
            ProcessIncomingFlags(flags);
            _remoteWindow.Extend(length);
        }
    }

    private void ApplyWindowSizeChange(uint previousWindowSize)
    {
        if (_channelOptions.ReceiveWindowSize != previousWindowSize)
        {
            lock (_receiveWindowLock)
            {
                var difference = _channelOptions.ReceiveWindowSize - previousWindowSize;

                _receiveWindowMax = _channelOptions.ReceiveWindowSize;

                this.SendWindowUpdate(difference);
            }
        }
    }

    private Flags GetSendFlags()
    {
        lock (_stateLock)
        {
            var flags = Flags.None;
            switch (_localPhase)
            {
                case ChannelLocalPhase.None:
                    flags |= Flags.SYN;
                    _localPhase = ChannelLocalPhase.SynSent;
                    break;
                case ChannelLocalPhase.SynSent:
                    flags |= Flags.ACK;
                    _localPhase = ChannelLocalPhase.Established;
                    break;
                case ChannelLocalPhase.WriteClosed:
                    flags |= Flags.FIN;
                    break;
            }
            return flags;
        }
    }

    private void ProcessIncomingFlags(Flags flags)
    {
        lock (_stateLock)
        {
            if (_disposed)
                return;
            if (flags.HasFlag(Flags.SYN))
            {
                if (_remoteState == ChannelRemoteState.None)
                {
                    _remoteState = ChannelRemoteState.Open;
                }
            }

            if (flags.HasFlag(Flags.ACK))
            {
                if (_remoteState == ChannelRemoteState.None)
                {
                    _remoteState = ChannelRemoteState.Open;
                    _session.ChannelAcknowledge(this, true);
                    _remoteAckTask.TrySetResult();
                }
            }

            if (flags.HasFlag(Flags.FIN))
            {
                if (_remoteState < ChannelRemoteState.ReadClosed)
                {
                    CancelCloseTimeout();
                    _remoteState = ChannelRemoteState.ReadClosed;
                    _inputBuffer.Writer.Complete(_fault);
                    _remoteCloseTask.TrySetResult();
                }
            }

            if (flags.HasFlag((Flags)Flags.RST))
            {
                CancelCloseTimeout();
                _fault = new SessionChannelException(ChannelErrorCode.ChannelRejected, "Channel was rejected or forcibly closed by the remote peer");
                _remoteState = ChannelRemoteState.Reset;
                _localPhase = ChannelLocalPhase.WriteClosed;
                _writeClosedCancellation.Cancel();
                _inputBuffer.Writer.Complete(_fault);
                _remoteCloseTask.TrySetResult();
                _session.ChannelDisconnect(this);
            }
        }
    }

    internal PipeWriter GetPipeWriter() => _inputBuffer.Writer;

    internal async ValueTask WaitForPendingFlushAsync()
    {
        if (_pendingPipeFlush.HasValue)
        {
            await _pendingPipeFlush.Value.ConfigureAwait(false);
            _pendingPipeFlush = null;
        }
    }

    internal void OffloadPipeFlush(ValueTask<FlushResult> flushTask, PipeWriter writer)
    {
        _pendingPipeFlush = HandleOffloadedPipeFlushAsync(flushTask, writer);
    }

    private async ValueTask HandleOffloadedPipeFlushAsync(ValueTask<FlushResult> flushTask, PipeWriter pipeWriter)
    {
        try
        {
            var result = await flushTask.ConfigureAwait(false);
            if (result.IsCompleted)
            {
                CloseWrite();
                await pipeWriter.CompleteAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            if (ChannelTracer.Switch.ShouldTrace(TraceEventType.Error))
                ChannelTracer.TraceEvent(TraceEventType.Error, 0, $"[Err] yamux: offloaded pipe flush failed for channel {Id}: {ex.Message}");
        }
    }

    private void CompleteRead(YamuxException? fault = null)
    {
        lock (_stateLock)
        {
            if (_remoteState < ChannelRemoteState.ReadClosed)
            {
                CancelCloseTimeout();
                _remoteState = ChannelRemoteState.ReadClosed;
                _inputBuffer.Writer.Complete(fault);
                _remoteCloseTask.TrySetResult();
            }
        }
    }

    private void OnInputBytesConsumed()
    {
        lock (_receiveWindowLock)
        {
            uint increase = 0;
            var rtt = _session.RTT;

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
            else if (_channelOptions.AutoTuneReceiveWindowSize
                && rtt.HasValue
                && _timeSinceLastUpdate > rtt.Value.Ticks * 4)
            {
                var halved = Math.Max(_receiveWindowMax / 2, _channelOptions.ReceiveWindowSize);
                if (halved < _receiveWindowMax)
                {
                    _receiveWindowMax = halved;
                    if (ChannelTracer.Switch.ShouldTrace(TraceEventType.Verbose))
                        ChannelTracer.TraceEvent(TraceEventType.Verbose, 0, $"[Dbg] yamux: Channel {Id} auto-tune window decreased max window size to {_receiveWindowMax} ");
                }
            }

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

    internal void SendWindowUpdate(uint incrementWindow, Flags? flags = null)
    {
        if (Session.SessionTracer.Switch.ShouldTrace(TraceEventType.Verbose))
            Session.SessionTracer.TraceEvent(TraceEventType.Verbose, 0, $"[Dbg] yamux: Channel {Id} sending window update (increment: {incrementWindow})");
        var update = Frame.CreateWindowUpdateFrame(this.Id, flags ?? GetSendFlags(), incrementWindow);
        _session.EnqueueFrame(update);
    }

    private void ValidateStateForWrite()
    {
        this.ThrowIfDisposed();

        if (!CanWrite)
        {
            lock (_stateLock)
            {
                if (_localPhase >= ChannelLocalPhase.WriteClosed || _writeClosedCancellation.IsCancellationRequested)
                {
                    throw new SessionChannelException(ChannelErrorCode.ChannelWriteClosed, "SessionChannel half closed and can no longer send data");
                }
                throw new SessionChannelException(ChannelErrorCode.ChannelClosed, "SessionChannel is closed");
            }
        }

        if (_fault != null)
        {
            throw _fault;
        }
    }

    private async ValueTask<ulong> CopyToAsync(PipeReader source, PipeWriter destination, CancellationToken cancellationToken = default)
    {
        ulong totalBytesCopied = 0;

        while (true)
        {
            ReadResult result = await source.ReadAsync(cancellationToken).ConfigureAwait(false);
            ReadOnlySequence<byte> buffer = result.Buffer;

            if (buffer.Length > 0)
            {
                foreach (var segment in buffer)
                {
                    await destination.WriteAsync(segment, cancellationToken).ConfigureAwait(false);
                    totalBytesCopied += (ulong)segment.Length;
                }
            }

            source.AdvanceTo(buffer.End);

            if (result.IsCompleted)
            {
                break;
            }
        }
        await source.CompleteAsync().ConfigureAwait(false);

        return totalBytesCopied;
    }

    private void ThrowIfDisposed()
    {
        if (this._disposed) throw new ObjectDisposedException("SessionChannel is disposed");
    }
}