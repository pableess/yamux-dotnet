---
title: Transport
---

# Transport

Yamux runs over any reliable, ordered, full-duplex byte transport by implementing
`ITransport`.

## ITransport

```csharp
public interface ITransport : IDisposable
{
    ValueTask<int> ReadAsync(Memory<byte> data, CancellationToken cancellationToken);
    ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken);
    void Close();

    ValueTask FlushAsync(CancellationToken cancellationToken = default); // default: no-op
    bool SupportsBatching => false;
    ValueTask WriteAsync(ReadOnlySequence<byte> data, CancellationToken cancellationToken = default);
}
```

| Member | Contract |
|---|---|
| `ReadAsync` | Fill the provided buffer. May return fewer bytes than requested. Return `0` only when the remote end has closed; Yamux treats `0` as connection close. |
| `WriteAsync(ReadOnlyMemory)` | Write **all** bytes in the buffer. Do not return a partial write. |
| `Close()` | Stop further reads/writes and close the connection. Must be safe to call during shutdown, ideally idempotent. |
| `FlushAsync` | Flush any transport-internal buffers. Called by `FlushWritesAsync` and as a batched-write barrier. Default is a no-op. |
| `SupportsBatching` | Opt into the sequence-based write path. Default `false`. |
| `WriteAsync(ReadOnlySequence)` | Batched/scatter-gather write. The default implementation calls the single-memory overload per segment. |
| `Dispose()` | Release the transport. Called by the session unless `leaveOpen: true`. |

## Requirements

- Reliable, ordered delivery — no data loss, duplication, or reordering.
- Full-duplex operation — one reader and one writer may run concurrently.
- Yamux calls `ReadAsync` from its frame reader and `WriteAsync` from its writer
  loop; implementations may assume one concurrent reader and one concurrent writer.

Yamux adds framing, multiplexing, and flow control, but not reliability; the
transport must provide it.

## Built-in implementations

| Type | Wraps | Batching |
|---|---|---|
| `StreamPeer` | Any `System.IO.Stream` (`NetworkStream`, `SslStream`, etc.) | No |
| `SocketPeer` | `System.Net.Sockets.Socket` | Yes (`SocketAsyncEventArgs.BufferList`) |
| `PipePeer` | `System.IO.Pipelines.IDuplexPipe` | Yes (copies segments into one flush) |

Convenience extensions:

```csharp
await using var session = socket.AsYamuxSession(isClient: true);
await using var session = stream.AsYamuxSession(isClient: true);
await using var session = duplexPipe.AsYamuxSession(isClient: true);
```

All accept `leaveOpen` and `SessionOptions? options`:

```csharp
await using var session = socket.AsYamuxSession(
    isClient: false,
    leaveOpen: false,
    options: new SessionOptions { EnableKeepAlive = false });
```

## Custom transport

```csharp
public sealed class NamedPipeTransport : ITransport
{
    private readonly NamedPipeClientStream _pipe;

    public NamedPipeTransport(NamedPipeClientStream pipe) => _pipe = pipe;

    public ValueTask<int> ReadAsync(Memory<byte> data, CancellationToken cancellationToken)
        => _pipe.ReadAsync(data, cancellationToken);

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        await _pipe.WriteAsync(data, cancellationToken).ConfigureAwait(false);
        await _pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Close() => _pipe.Close();
    public void Dispose() => _pipe.Dispose();
}

await using var session = new Session(new NamedPipeTransport(pipe), isClient: true);
session.Start();
```

## Batching

Batching coalesces queued frames into one transport write, reducing syscalls.

1. Return `true` from `SupportsBatching`.
2. Implement `WriteAsync(ReadOnlySequence<byte>, ...)` efficiently
   (scatter/gather for sockets, or copy into one buffer and write once).
3. Optionally implement `FlushAsync` if the transport has internal buffers.

The default sequence overload is a correctness fallback that writes each segment
separately; it provides no batching benefit.

At the session level:

```csharp
var options = new SessionOptions
{
    WriteSegmentBatchingEnabled = true, // null = use ITransport.SupportsBatching
    MinWriteBatchSize = 8192,
};
```

- `WriteSegmentBatchingEnabled` overrides `SupportsBatching`.
- `MinWriteBatchSize` is the minimum accumulated bytes before a batched write.
  Smaller values flush sooner (lower latency, more syscalls).
- `ISessionChannel.FlushWritesAsync` inserts a flush barrier: queued frames are
  written and `ITransport.FlushAsync` is called before the flush completes.

## Error behavior

- Transport read failures are wrapped in `SessionException` and close the
  session; pending channels observe the failure.
- A transport write failure faults the pending frame write with the transport
  exception. The session is closed when the reader subsequently observes the
  broken connection.
- `Close()` and `Dispose()` failures during shutdown are logged and do not prevent
  the rest of session teardown (best-effort shutdown).
- `WriteAsync` should not be called after `Close()`; behavior is
  implementation-specific.
