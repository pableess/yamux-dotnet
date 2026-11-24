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

    private readonly Lock _receiveWindowLock = new();
    private readonly Lock _stateLock = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private TaskCompletionSource _remoteCloseTask;
    private TaskCompletionSource _remoteAckTask;
    private CancellationTokenSource? _closeTimeoutCts;
    private uint _receiveWindowMax;

    private volatile bool _disposed;
    private bool _writeClosing;
    private bool _closeScheduled;
    private YamuxException? _fault;
    private ValueTask? _pendingPipeFlush;

    private ChannelLocalPhase _localPhase;
    private ChannelRemoteState _remoteState;

    private readonly bool _remoteInitiated;

    private uint _advertisedWindowSize = Constants.Initial_Window_Size;
    private long _receiveWindowRemaining = Constants.Initial_Window_Size;
    private long _lastWindowUpdateTimestamp;

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
                return !_writeClosing
                    && _localPhase < ChannelLocalPhase.WriteClosed
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
        _remoteInitiated = initialRemoteState == ChannelRemoteState.Open;
        _writeTimeout = session.StreamSendTimeout;

        _inputBuffer = new Pipe(new PipeOptions(pauseWriterThreshold: (long)_channelOptions.ReceiveWindowUpperBound + 1));

        _remoteCloseTask = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _remoteAckTask = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        _remoteWindow = new RemoteDataWindow();

        _input = new CountingPipeReader(_inputBuffer.Reader, OnInputBytesConsumed);

        _receiveWindowMax = _channelOptions.ReceiveWindowSize;
        _advertisedWindowSize = Constants.Initial_Window_Size;
        _receiveWindowRemaining = Constants.Initial_Window_Size;

        if (_channelOptions.EnableStatistics)
        {
            this.Stats = new Statistics(_channelOptions.StatisticsSampleInterval, default);
        }
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

        if (_disposed)
        {
            throw new SessionChannelException(ChannelErrorCode.ChannelClosed, "Channel has been closed.");
        }

        CancellationToken writeClosedToken;
        try
        {
            writeClosedToken = _writeClosedCancellation.Token;
        }
        catch (ObjectDisposedException ex)
        {
            throw new SessionChannelException(ChannelErrorCode.ChannelClosed, "Channel has been closed.", ex);
        }

        CancellationTokenSource? linkedCts = null;
        CancellationToken linkedToken;
        if (cancellationToken.CanBeCanceled)
        {
            linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, writeClosedToken);
            linkedToken = linkedCts.Token;
        }
        else
        {
            linkedToken = writeClosedToken;
        }

        bool gateAcquired = false;

        try
        {
            // Serialize writes so that a FIN cannot be enqueued between two data
            // frames of the same logical write. The gate is also used by CloseWrite.
            await _writeGate.WaitAsync(linkedToken).ConfigureAwait(false);
            gateAcquired = true;

            this.ValidateStateForWrite();

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

                RecordSentBytes((ulong)slice.Length);

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
        finally
        {
            if (gateAcquired)
            {
                _writeGate.Release();
            }

            linkedCts?.Dispose();
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

        try { _writeClosedCancellation.Cancel(); }
        catch (ObjectDisposedException) { }

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
        // Closing a channel that was already disposed (for example because the
        // session closed concurrently) is a no-op rather than an error.
        if (_disposed)
        {
            return;
        }

        this.CloseWrite();
    }

    public async Task<bool> WhenRemoteCloseAsync(TimeSpan timeout)
    {
        // Do not throw if the channel was disposed: disposal completes the
        // remote-close task, so the caller can still observe that the channel
        // is closed.
        if (_remoteCloseTask.Task.IsCompleted)
        {
            return true;
        }

        var task = await Task.WhenAny(_remoteCloseTask.Task, Task.Delay(timeout));
        return task == _remoteCloseTask.Task;
    }

    public async Task<bool> WhenRemoteAckAsync(TimeSpan timeout)
    {
        if (_remoteAckTask.Task.IsCompleted)
        {
            return true;
        }

        // A disposed channel that was never acknowledged will never be
        // acknowledged; report that instead of throwing ObjectDisposedException.
        if (_disposed)
        {
            return false;
        }

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

                // A fully and gracefully closed channel (FIN sent and FIN received)
                // does not need a RST on disposal.
                bool graceful = _remoteState >= ChannelRemoteState.ReadClosed
                    && _localPhase >= ChannelLocalPhase.WriteClosed;
                bool sendReset = !graceful && _remoteState != ChannelRemoteState.Reset;

                if (_remoteState < ChannelRemoteState.ReadClosed)
                {
                    _localPhase = ChannelLocalPhase.WriteClosed;
                    _remoteState = ChannelRemoteState.Reset;
                    _inputBuffer.Writer.Complete();
                    _remoteCloseTask.TrySetResult();
                }

                if (sendReset)
                {
                    this.SendWindowUpdate(0, (Flags)Flags.RST);
                }

                this._session.ChannelDisconnect(this);

                _remoteWindow.Dispose();
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
            // A FIN can legitimately arrive before the application accepts the
            // channel (the peer opened and half-closed immediately). Only a reset
            // channel is not acceptable.
            bool canAccept = _localPhase == ChannelLocalPhase.None
                && (_remoteState == ChannelRemoteState.Open || _remoteState == ChannelRemoteState.ReadClosed);

            if (canAccept)
            {
                // Sending the ACK also carries the initial window increase. This is
                // the only place a remotely initiated channel advertises its window;
                // the constructor must not send frames for a stream the peer opened.
                _localPhase = ChannelLocalPhase.Established;
                var delta = _channelOptions.ReceiveWindowSize > _advertisedWindowSize
                    ? _channelOptions.ReceiveWindowSize - _advertisedWindowSize
                    : 0;
                _advertisedWindowSize = _channelOptions.ReceiveWindowSize;
                _receiveWindowMax = _channelOptions.ReceiveWindowSize;
                this.SendWindowUpdate(delta, Flags.ACK);
            }
            else
            {
                throw new SessionChannelException(
                    _remoteState == ChannelRemoteState.Reset
                        ? ChannelErrorCode.ChannelRejected
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

    /// <summary>
    /// Sends the SYN frame for a locally initiated channel, carrying the initial
    /// window increase in the same frame so the peer sees a single SYN.
    /// </summary>
    internal void SendInitialWindowUpdate()
    {
        lock (_stateLock)
        {
            if (_localPhase != ChannelLocalPhase.None)
            {
                return;
            }

            var delta = _channelOptions.ReceiveWindowSize > _advertisedWindowSize
                ? _channelOptions.ReceiveWindowSize - _advertisedWindowSize
                : 0;
            _advertisedWindowSize = _channelOptions.ReceiveWindowSize;
            _receiveWindowMax = _channelOptions.ReceiveWindowSize;

            // GetSendFlags sets SYN and transitions to SynSent.
            this.SendWindowUpdate(delta);
        }
    }

    internal ValueTask ApplyOptionsAsync(SessionChannelOptions options, CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();
        options.Validate();

        if (options.ReceiveWindowSize < _channelOptions.ReceiveWindowSize)
        {
            throw new ValidationException("ReceiveWindowSize can not be specified as a smaller value than what is specified as the Default channel options on the session");
        }

        _channelOptions = options;

        // The receive window can only grow after the channel is created. The input
        // pipe's pause threshold is fixed at construction; changing it after data
        // may already be buffered would require coordinating with the read loop.
        ApplyWindowIncrease(options.ReceiveWindowSize);

        return ValueTask.CompletedTask;
    }

    private void ApplyWindowIncrease(uint desiredWindowSize)
    {
        lock (_receiveWindowLock)
        {
            if (desiredWindowSize <= _advertisedWindowSize)
            {
                return;
            }

            var difference = desiredWindowSize - _advertisedWindowSize;
            _advertisedWindowSize = desiredWindowSize;
            _receiveWindowMax = desiredWindowSize;
            this.SendWindowUpdate(difference);
        }
    }

    internal bool TryConsumeReceiveWindow(uint length)
    {
        if (length == 0)
        {
            return true;
        }

        var remaining = Interlocked.Add(ref _receiveWindowRemaining, -(long)length);
        if (remaining < 0)
        {
            // Roll the reservation back; the caller will treat this as a protocol error.
            Interlocked.Add(ref _receiveWindowRemaining, length);
            return false;
        }

        return true;
    }

    internal bool IsReset
    {
        get
        {
            lock (_stateLock)
            {
                return _remoteState == ChannelRemoteState.Reset;
            }
        }
    }

    internal void RecordSentBytes(ulong bytes)
    {
        var stats = Stats;
        if (stats is null)
        {
            return;
        }

        try
        {
            stats.UpdateSent(bytes);
        }
        catch (ObjectDisposedException)
        {
            // Statistics are best effort; disposal can race with an in-flight write.
        }
    }

    internal void RecordReceivedBytes(ulong bytes)
    {
        var stats = Stats;
        if (stats is null)
        {
            return;
        }

        try
        {
            stats.UpdateReceived(bytes);
        }
        catch (ObjectDisposedException)
        {
            // Statistics are best effort; disposal can race with the read loop.
        }
    }

    internal void CloseWrite()
    {
        lock (_stateLock)
        {
            if (_localPhase >= ChannelLocalPhase.WriteClosed || _writeClosing || _closeScheduled)
            {
                return;
            }

            _writeClosing = true;
            _closeScheduled = true;
        }

        // Wake any write blocked on window credit so the write gate is released
        // and the FIN can be ordered after the data that was already sent.
        try { _writeClosedCancellation.Cancel(); }
        catch (ObjectDisposedException) { }

        _ = SendCloseAsync();
    }

    private async Task SendCloseAsync()
    {
        try
        {
            // Wait for any in-flight write to finish so FIN is never enqueued
            // between two data frames of the same write.
            await _writeGate.WaitAsync().ConfigureAwait(false);
            try
            {
                Flags closeFlags;
                lock (_stateLock)
                {
                    if (_disposed || _localPhase >= ChannelLocalPhase.WriteClosed)
                    {
                        return;
                    }

                    closeFlags = GetSendFlags() | Flags.FIN;
                    _localPhase = ChannelLocalPhase.WriteClosed;
                }

                this.SendWindowUpdate(0, closeFlags);
            }
            finally
            {
                _writeGate.Release();
            }

            StartCloseTimeout();
        }
        catch (Exception ex)
        {
            if (ChannelTracer.Switch.ShouldTrace(TraceEventType.Error))
                ChannelTracer.TraceEvent(TraceEventType.Error, 0, $"[Err] yamux: failed to close channel {Id}: {ex.Message}");
        }
    }

    private void StartCloseTimeout()
    {
        var closeTimeout = _session.StreamCloseTimeout;
        if (closeTimeout <= TimeSpan.Zero)
        {
            return;
        }

        lock (_stateLock)
        {
            if (_disposed || _remoteState >= ChannelRemoteState.ReadClosed || _closeTimeoutCts != null)
            {
                return;
            }

            _closeTimeoutCts = new CancellationTokenSource();
            _ = CloseTimeoutAsync(closeTimeout, _closeTimeoutCts);
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

            _writeClosing = true;
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

    private Flags GetSendFlags()
    {
        lock (_stateLock)
        {
            // FIN is deliberately not derived from the state here: it must be
            // sent exactly once by the close path. Emitting it from every later
            // window update causes the peer to see duplicate FINs.
            if (_localPhase == ChannelLocalPhase.None)
            {
                if (_remoteInitiated)
                {
                    // The peer opened this stream; the first frame we send must
                    // acknowledge it rather than declaring a duplicate SYN.
                    _localPhase = ChannelLocalPhase.Established;
                    return Flags.ACK;
                }

                _localPhase = ChannelLocalPhase.SynSent;
                return Flags.SYN;
            }

            return Flags.None;
        }
    }

    internal void ProcessIncomingFlags(Flags flags)
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
                if (_localPhase == ChannelLocalPhase.SynSent)
                    _localPhase = ChannelLocalPhase.Established;
                if (_remoteState == ChannelRemoteState.None)
                {
                    _remoteState = ChannelRemoteState.Open;
                }
                _session.ChannelAcknowledge(this, true);
                _remoteAckTask.TrySetResult();
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
                _writeClosing = true;
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
        if (_disposed)
        {
            return;
        }

        lock (_receiveWindowLock)
        {
            uint increase = 0;
            var rtt = _session.RTT;
            var now = Stopwatch.GetTimestamp();
            var last = _lastWindowUpdateTimestamp;
            var elapsed = last == 0 ? TimeSpan.MaxValue : Stopwatch.GetElapsedTime(last, now);

            if (_channelOptions.AutoTuneReceiveWindowSize && rtt.HasValue)
            {
                if (elapsed < rtt.Value * 2)
                {
                    var previousMax = _receiveWindowMax;
                    var doubled = Math.Min((ulong)_receiveWindowMax * 2, _channelOptions.ReceiveWindowUpperBound);
                    _receiveWindowMax = (uint)doubled;

                    if (ChannelTracer.Switch.ShouldTrace(TraceEventType.Verbose))
                        ChannelTracer.TraceEvent(TraceEventType.Verbose, 0, $"[Dbg] yamux: Channel {Id} auto-tune window increase max window size to {_receiveWindowMax} ");
                    increase = _receiveWindowMax - previousMax;
                }
                else if (elapsed > rtt.Value * 4)
                {
                    var halved = Math.Max(_receiveWindowMax / 2, _channelOptions.ReceiveWindowSize);
                    if (halved < _receiveWindowMax)
                    {
                        _receiveWindowMax = halved;
                        if (ChannelTracer.Switch.ShouldTrace(TraceEventType.Verbose))
                            ChannelTracer.TraceEvent(TraceEventType.Verbose, 0, $"[Dbg] yamux: Channel {Id} auto-tune window decreased max window size to {_receiveWindowMax} ");
                    }
                }
            }

            if (_input.ConsumedBytes > (_receiveWindowMax / 2))
            {
                _lastWindowUpdateTimestamp = now;
                if (ChannelTracer.Switch.ShouldTrace(TraceEventType.Verbose))
                    ChannelTracer.TraceEvent(TraceEventType.Verbose, 0, $"[Dbg] yamux: Channel {Id} sending window update (consumed: {_input.ConsumedBytes}, increase: {increase})");
                this.SendWindowUpdate(_input.ConsumedBytes + increase);

                _input.Reset();
            }
            else if (increase > 0)
            {
                _lastWindowUpdateTimestamp = now;
                if (ChannelTracer.Switch.ShouldTrace(TraceEventType.Verbose))
                    ChannelTracer.TraceEvent(TraceEventType.Verbose, 0, $"[Dbg] yamux: Channel {Id} sending window update (increase only: {increase})");
                this.SendWindowUpdate(increase);
            }
        }
    }

    internal void SendWindowUpdate(uint incrementWindow, Flags? flags = null)
    {
        if (incrementWindow > 0)
        {
            // Track how much credit we have granted the peer so incoming frames can
            // be validated against the advertised receive window.
            Interlocked.Add(ref _receiveWindowRemaining, incrementWindow);
        }

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
                if (_writeClosing || _localPhase >= ChannelLocalPhase.WriteClosed || _writeClosedCancellation.IsCancellationRequested)
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

    private void ThrowIfDisposed()
    {
        if (this._disposed) throw new ObjectDisposedException("SessionChannel is disposed");
    }
}