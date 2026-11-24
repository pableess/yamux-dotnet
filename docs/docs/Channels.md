---
title: Channels
---

# Channel Interfaces

A channel is one independent, ordered byte stream inside a session. Channels are
identified by stream ID and support half-close, reset, flow control, and optional
statistics.

## ISessionChannel

```csharp
public interface ISessionChannel : IDisposable, IAsyncDisposable
{
    uint Id { get; }
    bool IsClosed { get; }
    Statistics? Stats { get; }

    void Abort();
    void Close();
    Task<bool> WhenRemoteCloseAsync(TimeSpan timeout);
    Task<bool> WhenRemoteAckAsync(TimeSpan timeout);
    ValueTask FlushWritesAsync(CancellationToken cancellationToken = default);
}
```

| Member | Behavior |
|---|---|
| `Id` | Stream ID. Client-initiated channels are odd; server-initiated are even. |
| `IsClosed` | `true` when the local write side is FIN-closed and the remote read side is closed, or when the channel was reset. |
| `Stats` | Per-channel statistics when `EnableStatistics` is enabled; otherwise `null`. |
| `Close()` | Sends FIN (graceful write-side half-close). Idempotent; no-op after disposal. |
| `Abort()` | Sends RST, cancels pending writes, and completes the read side with `SessionChannelException(ChannelRejected)`. |
| `WhenRemoteCloseAsync(timeout)` | Waits for the remote FIN/RST (or local disposal). Returns `false` on timeout; tolerates concurrent disposal. |
| `WhenRemoteAckAsync(timeout)` | Waits for the SYN/ACK handshake. Returns `false` on timeout, or if the channel is disposed before the ACK. |
| `FlushWritesAsync` | Flushes queued frames and the transport (when batching is enabled, this is a barrier). |
| `Dispose()` / `DisposeAsync()` | Releases resources. Idempotent. A channel that already completed a graceful FIN/FIN exchange does not emit RST. |

## IWriteOnlySessionChannel

```csharp
public interface IWriteOnlySessionChannel : ISessionChannel
{
    ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default);
}
```

- Writes are serialized per channel; a FIN cannot interleave between frames of
  one logical write.
- Each write waits for remote receive-window credit up to
  `SessionOptions.StreamSendTimeout` and throws `TimeoutException` if no credit
  arrives.
- Writes after `Close()`/`Abort()` or disposal throw
  `SessionChannelException` (`ChannelWriteClosed` or `ChannelClosed`).
- Cancellation of a blocked write releases the channel's write gate; the peer
  may still have received the frames sent before cancellation.

## IReadOnlySessionChannel

```csharp
public interface IReadOnlySessionChannel : ISessionChannel
{
    PipeReader Input { get; }
}
```

- Data is delivered through a `System.IO.Pipelines.PipeReader`.
- Consuming data sends window updates back to the peer.
- Completing the reader signals that the application will not read further data.
  When the receive loop next flushes to the completed reader, the channel's
  write side is half-closed (FIN).
- Reading after a reset throws `SessionChannelException`.

## IDuplexSessionChannel

```csharp
public interface IDuplexSessionChannel :
    ISessionChannel,
    IWriteOnlySessionChannel,
    IReadOnlySessionChannel
{
    Stream AsStream(bool leaveOpen = false);
}
```

`AsStream()` wraps the channel as a duplex `Stream`:

- `leaveOpen: true` leaves the channel open when the stream is disposed.
- `FlushAsync` maps to `FlushWritesAsync`.
- Synchronous `Write`, `Flush`, `Seek`, `SetLength`, and `Position` are not
  supported and throw `NotSupportedException`; synchronous `Read` delegates to
  the input pipe.

## Lifecycle

1. **Open/Accept** — the initiator sends SYN; the acceptor sends ACK.
2. **Established** — data flows in both directions under receive-window limits.
3. **Half-close** — `Close()` sends FIN; the read side remains open.
4. **Remote close** — the peer FIN completes `Input`; `WhenRemoteCloseAsync` returns.
5. **Dispose** — releases timers/statistics and removes the channel from the session.

An RST (`Abort()` or peer reset) skips the graceful path and immediately closes
both directions.

## Thread safety

- Multiple concurrent writers are supported and serialized by an internal write gate.
- Reads from `Input` are not thread-safe; use one reader at a time.
- `Close`, `Abort`, `Dispose`, and the `WhenRemote*Async` methods may be called
  concurrently with each other and with writes.

---

See [Session](Session.md) for opening/accepting and
[SessionChannelOptions](SessionChannelOptions.md) for flow-control settings.
