using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO.Pipelines;
using System.Linq;
using System.Text;
using System.Threading.Channels;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Yamux.Internal;
using Yamux.Protocol;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.Design.Serialization;
using System.Drawing;
using System.Buffers;

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
       SYN_Sent,
       SYN_Recieved,
       Open,
       LocalClose,
       RemoteClose,
       Closed,
       Reset
    }

    private readonly static TraceSource ChannelTracer = new TraceSource("Yamux.Channel");

    private readonly IChannelSessionAdapter _sessionAdapter;
    private Pipe _inputBuffer;
    private bool _bufferLockRequired;

    private SessionChannelOptions _channelOptions;

    private TimeSpan _writeTimeout = TimeSpan.FromSeconds(75);

    private SemaphoreSlim _writeLock;
    private SemaphoreSlim _stateLock;
    private SemaphoreSlim _bufferLock;

    private RemoteDataWindow _remoteWindow;

    private Lock _receiveWindowLock = new Lock();
    private uint _receiveWindowMax;


    private volatile bool _disposed;
    private YamuxException? _fault;
    private ConcurrentBag<TaskCompletionSource>? _closeTasks;
    private ConcurrentBag<IDisposable> _disposables;
    private volatile bool _writeClosed;
    private volatile bool _readClosed;

    private long _timeSinceLastUpdate;

    public Statistics? Stats { get; private set; }


    public ChannelState State { get; private set; }

    internal SessionChannel(IChannelSessionAdapter channelWriter, uint id, SessionChannelOptions defaultOptions)
    {
        Id = id;
        _sessionAdapter = channelWriter;
        _channelOptions = defaultOptions;
        _disposables = new ConcurrentBag<IDisposable>();

        // a default pipe is created if one is not provided.  Use default upper limit of 16MB and resume pipe filling once 4KB has been processed
        _inputBuffer = new Pipe(new PipeOptions(pauseWriterThreshold: _channelOptions.ReceiveWindowUpperBound + 1)); // +1 prevents pause when remote window is exactly filled
        
        _writeLock = new SemaphoreSlim(1); // write lock to ensure a single caller can write at a time
        _stateLock = new SemaphoreSlim(1);
        _bufferLock = new SemaphoreSlim(1);
        _bufferLockRequired = true;
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

        // if the pipe needs to be swapped with one with new settings
        if (_channelOptions.ReceiveWindowUpperBound != options.ReceiveWindowUpperBound)
        {
            await _bufferLock.WaitAsync(cancel);
            using var bufferRelease = new SemaphoreReleaser(_bufferLock);

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

        _bufferLockRequired = false;
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

        // ensure only a single writer is currently writing
        var acquired = await _writeLock.WaitAsync(_writeTimeout, cancel ?? default);
        try
        {
            if (acquired)
            {
                try
                {
                    // write each data frame in a chunk
                    for (uint i = 0; i < buffer.Length;)
                    {
                        uint requested = (uint)Math.Min(_channelOptions.MaxDataFrameSize, buffer.Length - i);
                        uint available = await _remoteWindow.WaitConsumeAsync(requested, _writeTimeout, cancel);

                        await _stateLock.WaitAsync(cancel ?? default);
                        using (var lockRelease = new SemaphoreReleaser(_stateLock))
                        {
                            // write the data frame
                            var slice = buffer.Slice((int)i, (int)available);
                            await _sessionAdapter.WriteDataFrameAsync(Id, GetSendFlags(), slice, cancel ?? default);
                            Stats?.UpdateSent((ulong)slice.Length);
                        }

                        // update the slice index
                        i += available;

                    }
                }
                finally
                {
                    _writeLock.Release();
                }
            }
            else
            {
                throw new TimeoutException("Timeout waiting for write access to channel");
            }
        }
        catch (ObjectDisposedException ex)
        {
            throw new SessionChannelException(ChannelErrorCode.ChannelClosed, "Channel has been closed.", ex);
        }
        catch (TimeoutException ex)
        {
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
        this.ThrowIfDisposed();
        return _sessionAdapter.FlushAsync(cancel ?? default);
    }

    /// <inheritdoc />
    public Task CloseAsync(TimeSpan? timeout = null, CancellationToken? cancel = null)
    {
        this.ThrowIfDisposed();

        // depending on our currrent state, we may want to send a close frame
        _stateLock.Wait();

        Task? task = null;

        using (SemaphoreReleaser lockRelease = new SemaphoreReleaser(_stateLock))
        {
            switch (this.State)
            {
                case ChannelState.Init:
                case ChannelState.SYN_Sent:
                case ChannelState.SYN_Recieved:
                case ChannelState.Open:
                    this.State = ChannelState.LocalClose;

                    // SEND CLOSE
                    task = this.SendFinAndWaitForFin(timeout, cancel);


                    break;
                case ChannelState.LocalClose:
                case ChannelState.RemoteClose:

                    // since remote partry has already closed, we can just acknowlege the close
                    this.State = ChannelState.Closed;
                    this.CloseRead();
                    return this.SendFin(cancel ?? CancellationToken.None).AsTask();
                case ChannelState.Closed:
                case ChannelState.Reset:
                    break;
                default:
                    throw new InvalidOperationException("unhandled state");
            }
        }

        // after the state lock is realeased, 
        return task ?? Task.CompletedTask;
    }

    // blocking version not advised, but implmented to support synchronous Stream operations
    internal void Write(ReadOnlyMemory<byte> buffer)
    {
        this.ValidateStateForWrite();

        var acquired = _writeLock.Wait(_writeTimeout);

        try
        {
            if (acquired)
            {
                try
                {
                    // write each data frame in a chunk
                    for (uint i = 0; i < buffer.Length;)
                    {
                        uint requested = Math.Min(_channelOptions.MaxDataFrameSize, (uint)buffer.Length - i);
                        uint available = _remoteWindow.WaitConsume(requested, _writeTimeout);
                        
                        _stateLock.Wait();
                        using (var lockRelease = new SemaphoreReleaser(_stateLock))
                        {
                            // write the data frame
                            var slice = buffer.Slice((int)i, (int)available);
                            _sessionAdapter.WriteDataFrame(Id, GetSendFlags(), slice);

                            Stats?.UpdateSent((ulong)slice.Length);
                        }

                        // update the slice index
                        i += available;
                    }
                }
                finally
                {
                    _writeLock.Release();
                }
            }
            else
            {
                throw new TimeoutException("Timeout waiting for write access to channel");
            }
        }
        catch (ObjectDisposedException ex)
        {
            throw new SessionChannelException(ChannelErrorCode.ChannelClosed, "Channel has been closed.", ex);
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException("Timeout waiting for write access to channel", ex);
        }
    }

    internal void FlushWrites()
    {
        this.ThrowIfDisposed();
        _sessionAdapter.Flush();
    }

    internal void HandleWindowUpdate(uint length, Flags flags)
    {
        if (!_disposed)
        {
            // update the remote window length
            ProcessIncomingFlags(flags);

            _remoteWindow.Extend(length);

            // complete
        }
    }

    internal async ValueTask HandleDataFrameAsync(uint length, Flags flags, CancellationToken cancel)
    {
        if (!_disposed)
        {
            bool locked = false;
            try
            {
                if (_bufferLockRequired)
                {
                    await _bufferLock.WaitAsync(cancel);
                    locked = true;
                }

                ProcessIncomingFlags(flags);
                if (length > 0)
                {
                    // fill the input pipe with the payload data
                    await FillInputPipe(length, cancel);
                }
            }
            finally 
            {
                if (locked)
                    _bufferLock.Release();
            }
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
        var flags = Flags.None;
        switch (this.State)
        {
            case ChannelState.Init:
                flags |= Flags.SYN;
                this.State = ChannelState.SYN_Sent;
                break;
            case ChannelState.SYN_Recieved:
                flags |= Flags.ACK;
                this.State = ChannelState.Open;
                break;
        }
        return flags;
    }



    private async void ProcessIncomingFlags(Flags flags)
    {
        this.ThrowIfDisposed();

        this._stateLock.Wait();
        using SemaphoreReleaser releaser = new SemaphoreReleaser(_stateLock);

        if (flags.HasFlag(Flags.ACK))
        {
            if (this.State == ChannelState.SYN_Sent)
            {
                this.State = ChannelState.Open;

                // notify the session that the channel is accepted
                _sessionAdapter.ChannelAcknowledge(this, true);
            }
        }
        if (flags.HasFlag(Flags.FIN))
        {
            HandleFin();

            switch (this.State)
            {
                case ChannelState.SYN_Sent:
                case ChannelState.SYN_Recieved:
                case ChannelState.Open:
                    this.State = ChannelState.RemoteClose;

                    await this.CloseReadAsync();

                    break;
                case ChannelState.LocalClose:

                    this.CloseWrite();

                    this.State = ChannelState.Closed;
                    break;

                default:
                    // unexpected flag, throw err


                    break;
            }
        }
        if (flags.HasFlag((Flags)Flags.RST))
        {
            this.State = ChannelState.Reset;

            if (this.State == ChannelState.SYN_Sent)
            {
                this.ForceClose(new SessionChannelException(ChannelErrorCode.ChannelRejected, $"Session channel {Id} was rejected by remote peer"));
                _sessionAdapter.ChannelAcknowledge(this, false);
            }
        }
    }

    /// <summary>
    /// Reads fill the input buffer with payload data 
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    private async ValueTask FillInputPipe(uint payloadLength, CancellationToken cancellationToken) 
    {
        int bytesToRead = (int)payloadLength;

        do
        {
            var buffer = _inputBuffer.Writer.GetMemory();

            if (buffer.Length > bytesToRead)
            {
                buffer = buffer.Slice(0, bytesToRead);
            }

            var read = await _sessionAdapter.ReadPayloadDataAsync(buffer, cancellationToken);
            _inputBuffer.Writer.Advance(read);

            bytesToRead -= read;

            var flushResult = await _inputBuffer.Writer.FlushAsync(cancellationToken);

            // reader has indicated that it is complete, we should close the channel as well
            if (flushResult.IsCompleted) 
            {
                await _inputBuffer.Writer.CompleteAsync();

                // close the channel
            }

        } while (bytesToRead > 0);

        Stats?.UpdateReceived(payloadLength);
    }

    private void HandleFin() 
    {
        // handle the FIN flag
        if (_closeTasks != null)
        {
            foreach (var tcs in _closeTasks)
            {
                tcs.TrySetResult();
            }
        }
    }

    private void CloseWrite()
    {
        if (_writeClosed)
            return;

        // close ability to write, we will still allow reading until sender 
        _writeClosed = true;
        // dispose of the write window, this will throw exceptions on blocked writers
        _remoteWindow?.Dispose();
    }

    private void CloseRead()
    { 
        if (_readClosed)
            return;

         // close ability to read, we will still allow writing until sender 
        _readClosed = true;
        // complete the input pipe
        _inputBuffer.Writer.Complete(_fault);
    }

    private ValueTask CloseReadAsync()
    {
        if (_readClosed)
            return ValueTask.CompletedTask;

        // close ability to read, we will still allow writing until sender 
        _readClosed = true;
        // complete the input pipe
        return _inputBuffer.Writer.CompleteAsync(_fault);
    }


    internal async ValueTask ForceCloseAsync()
    {
        await _stateLock.WaitAsync();
        using SemaphoreReleaser lockRelease = new SemaphoreReleaser(_stateLock);

        if (!_writeClosed) 
        {
            await this.SendFin(CancellationToken.None).AsTask().ContinueWith(t =>
            {
                // TODO: LOG ERR
            });
        }

        this.State = ChannelState.Closed;
        await this.CloseReadAsync();
        this.CloseWrite();
    }

    internal void ForceClose(YamuxException? err = null)
    {  
        _stateLock.Wait();
        using SemaphoreReleaser lockRelease = new SemaphoreReleaser(_stateLock);

        // set the fault exception for public methods and fault the read pipe 
        _fault = err;

        if (!_writeClosed)
        {
            SendFin(CancellationToken.None).AsTask().ContinueWith(t =>
            {
                // TODO: LOG ERR
            });
        }

        this.State = ChannelState.Closed;
        this.CloseRead();
        this.CloseWrite();
    }

    public void Dispose()
    {
        if (!this._disposed) 
        {
            _disposed = true;
            this.ForceClose();

            // make sure the channel is removed from the session
            _sessionAdapter.ChannelDisconnect(this);

            _writeLock.Dispose();
            _stateLock.Dispose();
            _bufferLock.Dispose();
            Stats?.Dispose();
            Stats = null;
        }
    }

    private void OnInputBytesConsumed() 
    {
        // default algorithm is when we have processed 1/2 of the receive window, then we can send "more bytes please"
        lock (_receiveWindowLock)
        {
            uint increase = 0;
            var rtt = _sessionAdapter.RTT;

            // auto tune window increase
            if (_channelOptions.AutoTuneReceiveWindowSize
                && rtt.HasValue
                && _timeSinceLastUpdate > 0
                && _timeSinceLastUpdate < rtt.Value.Ticks * 2)
            {
                var previousMax = _receiveWindowMax;
                _receiveWindowMax = Math.Min(_receiveWindowMax * 2, _channelOptions.ReceiveWindowUpperBound);

                ChannelTracer.TraceInformation($"Auto-tune window increase max window size to {_receiveWindowMax} ");
                increase = _receiveWindowMax - previousMax;
            }

            // if we are going to send a window update and increasing the window, we can just send it in a single update
            if (_input.ConsumedBytes > (_receiveWindowMax / 2))
            {
                _timeSinceLastUpdate = Stopwatch.GetTimestamp() - _timeSinceLastUpdate;
                this.SendWindowUpdate(_input.ConsumedBytes + increase);

                _input.Reset();
            }
            else if (increase > 0)
            {
                _timeSinceLastUpdate = Stopwatch.GetTimestamp() - _timeSinceLastUpdate;
                this.SendWindowUpdate(increase);
            }
        }
    }

    internal void SendWindowUpdate(uint incrementWindow)
    {
        // fire and forget a window update
        _ = _sessionAdapter.WriteWindowUpdateFrameAsync(this.Id, GetSendFlags(), incrementWindow, CancellationToken.None);
    }

    private async ValueTask SendFin(CancellationToken cancel)
    {
        try
        {
            // respond with a FIN flag, and close the channel
            // send a window update with the FIN flag
            var flags = GetSendFlags();
            flags |= Flags.FIN;
            await _sessionAdapter.WriteWindowUpdateFrameAsync(this.Id, flags, 0, cancel);
        }

        catch (YamuxException)
        {
            // fault the channel
            this.ForceClose(new SessionChannelException(ChannelErrorCode.SessionClosed, "Failed to send FIN flag"));
        }
    }

    /// <summary>
    /// Sends the FIN flag and returns a task that completes when the remote peer sends a FIN flag
    /// </summary>
    /// <param name="cancel"></param>
    /// <returns></returns>
    private async Task SendFinAndWaitForFin(TimeSpan? timeout = null, CancellationToken? cancel = null)
    {
        cancel?.ThrowIfCancellationRequested();

        _closeTasks ??= new ConcurrentBag<TaskCompletionSource>();

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _closeTasks.Add(tcs);

        if (cancel != null)
        {
            _disposables.Add(cancel.Value.Register(() =>
            {
                tcs?.TrySetCanceled();
            }));
        }

        if (timeout != null)
        {
            _disposables.Add(new Timer((state) =>
            {
                tcs?.TrySetException(new TaskCanceledException("Timed out waiting for FIN response"));
            }, null, timeout.Value, TimeSpan.FromMilliseconds(-1)));
        }

        // send a window update with the FIN flag
        await SendFin(cancel ?? CancellationToken.None);
        await tcs.Task;
    }

    private void ValidateStateForWrite()
    {
        _stateLock.Wait();
        using SemaphoreReleaser lockRelease = new SemaphoreReleaser(_stateLock);

        this.ThrowIfDisposed();

        if (_fault != null)
        {
            throw _fault;
        }
        if (this._writeClosed) 
        {
            throw new SessionChannelException(ChannelErrorCode.ChannelClosed, "SessionChannel is closed");
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
