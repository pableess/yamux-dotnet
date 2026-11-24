---
title: Session
---

# Session

The `Session` class multiplexes independent channels over one reliable,
ordered, full-duplex `ITransport`. It implements `IAsyncDisposable`.

## Construction

```csharp
public Session(
    ITransport transport,
    bool isClient,
    bool leaveOpen = false,
    SessionOptions? options = null)
```

- `isClient` — the client uses odd stream IDs and the server uses even IDs.
- `leaveOpen` — when `true`, the transport is not closed or disposed by the session.
- `options` — session configuration; defaults are used when `null`.

Convenience extensions create a session from a socket, stream, or duplex pipe:

```csharp
await using var session = socket.AsYamuxSession(
    isClient: true,
    leaveOpen: false,
    options: options);
session.Start();
```

`Start()` must be called before opening or accepting channels. It is idempotent
and safe to call concurrently.

## Properties

| Member | Description |
|---|---|
| `TimeSpan? RTT` | Most recent ping/keep-alive round-trip time, or `null` before the first successful ping |
| `Statistics? Stats` | Session statistics when `SessionOptions.EnableStatistics` is enabled |
| `bool IsClosed` | `true` after session shutdown completes |

## Methods

### Opening channels

```csharp
ValueTask<IDuplexSessionChannel> OpenChannelAsync(
    SessionChannelOptions options,
    bool waitForAcknowledgement = false,
    CancellationToken cancellationToken = default);

ValueTask<IDuplexSessionChannel> OpenChannelAsync(
    bool waitForAcknowledgement = false,
    CancellationToken cancellationToken = default);
```

- Validates the channel options and throws `ValidationException` if they are invalid.
- Sends a SYN (and the initial window increase) in one frame. The SYN write
  completes before the method returns, so data frames cannot overtake stream setup.
- With `waitForAcknowledgement: true`, completes after the peer ACK. If
  `StreamOpenTimeout` expires, the session is closed. Cancellation resets the
  opened stream and completes the operation with `OperationCanceledException`.
- Throws `SessionException` with `RemoteGoAway` or `LocalGoAway` when the session
  has sent/received GoAway.

### Accepting channels

```csharp
ValueTask<IDuplexSessionChannel> AcceptAsync(
    CancellationToken cancellationToken = default);

ValueTask<IDuplexSessionChannel> AcceptAsync(
    SessionChannelOptions channelOptions,
    CancellationToken cancellationToken);

ValueTask<IReadOnlySessionChannel> AcceptReadOnlyChannelAsync(
    CancellationToken cancellationToken = default);
```

- Waits for an incoming channel from the accept backlog.
- Channels the peer reset before acceptance are skipped.
- The accepted channel is ACKed and the supplied `channelOptions` are applied.
- Throws `SessionException` with `SessionShutdown` when the session closes.

### Keep-alive and ping

```csharp
ValueTask<TimeSpan> PingAsync(CancellationToken cancellationToken);
```

Sends a ping and returns the measured RTT. A ping that is not answered within
`ConnectionWriteTimeout` throws `TimeoutException`. The same timeout applies to
the keep-alive loop; a failed keep-alive closes the session.

### Shutdown

```csharp
Task CloseAsync();
Task<bool> CloseOpenChannelsAsync(TimeSpan timeout);
Task GoAwayAsync(
    SessionTermination sessionTermination = SessionTermination.Normal,
    CancellationToken cancellationToken = default);
ValueTask DisposeAsync();
```

- `GoAwayAsync` sends a GoAway frame and prevents this side from opening new
  channels. Existing channels continue.
- `CloseOpenChannelsAsync` sends GoAway, closes each channel's write side, and
  waits up to `timeout` for the peer FIN. Returns `true` when all channels closed
  in time.
- `CloseAsync` is idempotent and force-closes all channels; it does not wait for
  channels to drain. It is best-effort: a failing transport/close step does not
  leave the session stuck in a closing state. `SessionCloseTimeout` bounds the
  wait for the frame reader.
- `DisposeAsync` calls `CloseAsync` and disposes statistics and metrics.

## Session termination

`SessionTermination` values sent in GoAway frames:

| Value | Meaning |
|---|---|
| `Normal` | Normal shutdown |
| `ProtocolError` | A protocol violation was detected |
| `InternalError` | An internal failure occurred |

When a remote GoAway is received, new `OpenChannelAsync` calls throw
`SessionException` with `ErrorCode = RemoteGoAway`; existing channels continue.

## Concurrency

- `Start()` is idempotent and safe to call concurrently.
- `OpenChannelAsync` and `AcceptAsync` may be called concurrently; stream IDs are
  allocated atomically and the accept queue supports multiple readers.
- `PingAsync`, `GoAwayAsync`, and `CloseAsync` may be called while channels are active.
- A `Session` instance is intended to be shared by the application's channel open/accept loops.

## Exceptions

- `SessionException` — session/protocol errors. See `SessionErrorCode`.
- `SessionChannelException` — channel-specific errors. See `ChannelErrorCode`.
- `ValidationException` — invalid `SessionChannelOptions`.
- `TimeoutException` — ping or send-window timeouts.
- `OperationCanceledException` — caller cancellation.

See [Exceptions](Exceptions.md) for the full error-code list.

---

See [SessionOptions](SessionOptions.md) and
[SessionChannelOptions](SessionChannelOptions.md) for configuration details.
