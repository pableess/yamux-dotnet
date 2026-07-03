using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Threading.Channels;
using Yamux.Protocol;

namespace Yamux.Internal;

internal class ChannelManager
{
    private readonly ConcurrentDictionary<uint, SessionChannel> _channels = new();
    private readonly ConcurrentDictionary<uint, TaskCompletionSource<IDuplexSessionChannel>> _connects = new();
    private readonly Channel<SessionChannel> _acceptQueue;
    private readonly Lock _stateLock = new();
    private readonly IChannelSessionAdapter _adapter;
    private readonly SessionChannelOptions _defaultOptions;
    private YamuxMetrics? _metrics;

    private bool _remoteGoAway;
    private bool _localGoAway;
    private SessionTermination _goAwayError;
    private int _maxChannels;

    public ChannelManager(IChannelSessionAdapter adapter, SessionChannelOptions defaultOptions, int acceptBacklog, YamuxMetrics? metrics, int maxChannels = 1024)
    {
        _adapter = adapter;
        _defaultOptions = defaultOptions;
        _metrics = metrics;
        _maxChannels = maxChannels;
        _acceptQueue = Channel.CreateBounded<SessionChannel>(new BoundedChannelOptions(acceptBacklog)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = true,
        });
    }

    internal void SetMetrics(YamuxMetrics? metrics) => _metrics = metrics;

    public int ActiveChannelCount => _channels.Count;

    public SessionChannel? GetChannel(uint id)
    {
        _channels.TryGetValue(id, out var channel);
        return channel;
    }

    public async ValueTask<SessionChannel?> GetOrCreateAsync(uint id, Flags flags, ConnectionWriter writer, CancellationToken cancel)
    {
        if (_channels.TryGetValue(id, out var channel))
        {
            return channel;
        }

        if (flags.HasFlag(Flags.SYN))
        {
            bool reject = false;
            lock (_stateLock)
            {
                if (_localGoAway)
                {
                    reject = true;
                }
            }

            if (reject)
            {
                await writer.WriteAsync(Frame.CreateWindowUpdateFrame(id, Flags.RST, 0), cancel);
                return null;
            }

            if (_channels.Count >= _maxChannels)
            {
                await writer.WriteAsync(Frame.CreateWindowUpdateFrame(id, Flags.RST, 0), cancel);
                return null;
            }

            var newChannel = new SessionChannel(_adapter, id, _defaultOptions, ChannelRemoteState.Open);
            _channels.TryAdd(id, newChannel);

            await _acceptQueue.Writer.WriteAsync(newChannel, cancel);

            return newChannel;
        }

        return null;
    }

    public SessionChannel AddChannel(uint id, SessionChannel channel)
    {
        _channels.TryAdd(id, channel);
        _metrics?.ChannelsOpened.Add(1);
        return channel;
    }

    public void RemoveChannel(uint id)
    {
        _channels.TryRemove(id, out _);
    }

    public void TrackConnect(uint id, TaskCompletionSource<IDuplexSessionChannel> tcs)
    {
        _connects.TryAdd(id, tcs);
    }

    public bool TryCompleteConnect(uint id, SessionChannel channel)
    {
        if (_connects.TryRemove(id, out var connectTcs))
        {
            connectTcs.TrySetResult(channel);
            return true;
        }
        return false;
    }

    public void FailConnect(uint id, bool accept)
    {
        if (_connects.TryRemove(id, out var connectTcs))
        {
            connectTcs.TrySetException(
                new SessionChannelException(ChannelErrorCode.ChannelRejected, "Session channel was rejected by the remote party"));
            if (!accept)
            {
                _channels.TryRemove(id, out _);
            }
        }
        else if (!accept)
        {
            _channels.TryRemove(id, out _);
        }
    }

    public void FailAllConnects(Exception? ex = null)
    {
        foreach (var kvp in _connects)
        {
            if (ex != null)
                kvp.Value.TrySetException(ex);
            else
                kvp.Value.TrySetCanceled();
        }
        _connects.Clear();
    }

    public async ValueTask<SessionChannel> WaitForAcceptAsync(CancellationToken cancel)
    {
        var channel = await _acceptQueue.Reader.ReadAsync(cancel);
        return channel;
    }

    public void CompleteAccepts()
    {
        _acceptQueue.Writer.TryComplete();
    }

    public void CloseAllChannels(SessionException? err = null)
    {
        CompleteAccepts();

        var currentChannels = _channels.Values;
        _channels.Clear();

        foreach (var c in currentChannels)
        {
            if (err == null)
                c.Close();
            else
                c.Close(err);
            _metrics?.ChannelsClosed.Add(1);
        }
    }

    public async Task<bool> CloseOpenChannelsAsync(TimeSpan timeout)
    {
        SetLocalGoAway();

        var channels = _channels.Values.ToArray();
        if (channels.Length == 0)
            return true;

        var tasks = new Task<bool>[channels.Length];
        for (int i = 0; i < channels.Length; i++)
        {
            channels[i].Close();
            tasks[i] = channels[i].WhenRemoteCloseAsync(timeout);
        }

        var results = await Task.WhenAll(tasks);
        return results.All(r => r);
    }

    public bool CanAcceptNew
    {
        get
        {
            lock (_stateLock)
            {
                return !_localGoAway && !_remoteGoAway;
            }
        }
    }

    public bool IsLocalGoAway
    {
        get { lock (_stateLock) return _localGoAway; }
    }

    public bool IsRemoteGoAway
    {
        get { lock (_stateLock) return _remoteGoAway; }
    }

    public void SetLocalGoAway()
    {
        lock (_stateLock)
        {
            _localGoAway = true;
        }
    }

    public void SetRemoteGoAway(SessionTermination code)
    {
        lock (_stateLock)
        {
            _remoteGoAway = true;
            _goAwayError = code;
        }

        _acceptQueue.Writer.TryComplete();

        if (Session.SessionTracer.Switch.ShouldTrace(TraceEventType.Information))
            Session.SessionTracer.TraceInformation($"[Err] yamux: received go away ({code})");
    }
}