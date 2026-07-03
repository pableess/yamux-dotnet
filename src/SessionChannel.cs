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
    private ManualResetEventSlim _remoteCloseEvent;
    private TaskCompletionSource _remoteCloseTask;
    private ManualResetEventSlim _remoteAckEvent;
    private TaskCompletionSource _remoteAckTask;
    private uint _receiveWindowMax;

    private volatile bool _disposed;
    private YamuxException? _fault;

    private ChannelLocalPhase _localPhase;
    private ChannelRemoteState _remoteState;

    private long _timeSinceLastUpdate;

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

        _inputBuffer = new Pipe(new PipeOptions(pauseWriterThreshold: _channelOptions.ReceiveWindowUpperBound + 1));

        _remoteCloseEvent = new ManualResetEventSlim(false);
        _remoteCloseTask = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _remoteAckEvent = new ManualResetEventSlim(false);
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


    public async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken? cancel = null)
    {
        cancel?.ThrowIfCancellationRequested();
        this.ValidateStateForWrite();

        var writeClosedToken = _writeClosedCancellation.Token;

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancel ?? CancellationToken.None, writeClosedToken);
        var linkedToken = linkedCts.Token;

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

    public Task FlushWritesAsync(CancellationToken? cancel = null)
    {
        return Task.CompletedTask;
    }

    public void Abort()
    {
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

    public bool WaitForRemoteClose(TimeSpan timeout)
    {
        this.ThrowIfDisposed();
        return this._remoteCloseEvent.Wait(timeout);
    }

    public bool WaitForRemoteAck(TimeSpan timeout)
    {
        this.ThrowIfDisposed();
        return this._remoteAckEvent.Wait(timeout);
    }

    public void Dispose()
    {
        lock (this._stateLock)
        {
            if (!this._disposed)
            {
                _disposed = true;

                _writeClosedCancellation.Cancel();

                if (_remoteState < ChannelRemoteState.ReadClosed)
                {
                    _remoteState = ChannelRemoteState.Reset;
                    _inputBuffer.Writer.Complete();
                    _remoteCloseEvent.Set();
                    _remoteCloseTask.TrySetResult();
                }

                this._session.ChannelDisconnect(this);

                _writeClosedCancellation.Dispose();
                Stats?.Dispose();
                Stats = null;
            }
        }
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

    internal void RemoteAckReceived()
    {
        lock (_stateLock)
        {
            if (_remoteState == ChannelRemoteState.None)
            {
                _remoteState = ChannelRemoteState.Open;
                _remoteAckEvent.Set();
                _remoteAckTask.TrySetResult();
            }
        }
    }

    internal async Task ApplyOptionsAsync(SessionChannelOptions options, CancellationToken cancel)
    {
        options.Validate();

        if (options.ReceiveWindowSize < _channelOptions.ReceiveWindowSize)
        {
            throw new ValidationException("RecevieWindowSize can not be specified as a smaller value than what is specified as the Default channel options on the session");
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
        this.ThrowIfDisposed();

        lock (_stateLock)
        {
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
                    _remoteAckEvent.Set();
                    _remoteAckTask.TrySetResult();
                }
            }

            if (flags.HasFlag(Flags.FIN))
            {
                if (_remoteState < ChannelRemoteState.ReadClosed)
                {
                    _remoteState = ChannelRemoteState.ReadClosed;
                    _inputBuffer.Writer.Complete(_fault);
                    _remoteCloseEvent.Set();
                    _remoteCloseTask.TrySetResult();
                }
            }

            if (flags.HasFlag((Flags)Flags.RST))
            {
                _fault = new SessionChannelException(ChannelErrorCode.ChannelRejected, "Channel was rejected or forcibly closed by the remote peer");
                _remoteState = ChannelRemoteState.Reset;
                _localPhase = ChannelLocalPhase.WriteClosed;
                _writeClosedCancellation.Cancel();
                _inputBuffer.Writer.Complete(_fault);
                _remoteCloseEvent.Set();
                _remoteCloseTask.TrySetResult();
            }
        }
    }

    internal PipeWriter GetPipeWriter() => _inputBuffer.Writer;

    private void CompleteRead(YamuxException? fault = null)
    {
        lock (_stateLock)
        {
            if (_remoteState < ChannelRemoteState.ReadClosed)
            {
                _remoteState = ChannelRemoteState.ReadClosed;
                _inputBuffer.Writer.Complete(fault);
                _remoteCloseEvent.Set();
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
        _session.SendFrameAsync(update, CancellationToken.None).AsTask().ContinueWith(t =>
        {
            if (t.IsFaulted)
            {
                Session.SessionTracer.TraceEvent(TraceEventType.Warning, 0, $"[Warn] yamux: Channel {Id} window update failed: {t.Exception?.InnerException?.Message}");
            }
        }, TaskContinuationOptions.OnlyOnFaulted);
    }

    private void ValidateStateForWrite()
    {
        this.ThrowIfDisposed();

        if (_fault != null)
        {
            throw _fault;
        }

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