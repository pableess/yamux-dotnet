---
title: Transport
---

# Custom Transport

Yamux can run over any reliable, ordered, duplex transport by implementing the `ITransport` interface.

## ITransport Interface

```csharp
public interface ITransport : IDisposable
{
    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancel);
    ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancel);
    void Close();
}
```

| Method | Description |
|--------|-------------|
| `ReadAsync` | Reads data from the underlying transport into the provided buffer. Returns the number of bytes read, or `0` when the remote end has closed the connection. |
| `WriteAsync` | Writes all bytes from the provided buffer to the underlying transport. Must not return partial writes. |
| `Close` | Closes the underlying transport and releases any resources. |

## Requirements

The underlying transport must provide:
- **Reliable delivery** — no data corruption or loss
- **Ordered delivery** — bytes must arrive in the same order they were sent
- **Full-duplex** — concurrent reads and writes must be supported

Yamux does not add its own reliability layer; it depends entirely on the transport for this.

## Built-in Implementations

| Class | Transport |
|-------|-----------|
| `StreamPeer` | Wraps any `System.IO.Stream` (e.g., `NetworkStream`, `SslStream`, `Pipe`) |
| `SocketPeer` | Wraps a `System.Net.Sockets.Socket` directly |

## Custom Transport Example

### Named Pipe Transport

```csharp
public class NamedPipeTransport : ITransport
{
    private readonly NamedPipeClientStream _pipe;

    public NamedPipeTransport(NamedPipeClientStream pipe)
    {
        _pipe = pipe;
    }

    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancel)
        => _pipe.ReadAsync(buffer, cancel);

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancel)
    {
        await _pipe.WriteAsync(data, cancel);
        await _pipe.FlushAsync(cancel);
    }

    public void Close() => _pipe.Close();
    public void Dispose() => _pipe.Dispose();
}
```

### Usage

```csharp
var transport = new NamedPipeTransport(pipeClient);
await using var session = new Session(transport, isClient: true);
session.Start();
```

## Opting into Batching

Yamux supports a batching mode that coalesces multiple small frame writes into a single call to the transport, reducing system call overhead. This is especially beneficial for transports with high per-write overhead (e.g., TCP sockets, named pipes).

### How to opt in

Override `SupportsBatching` to return `true` to enable the batching write path. When batching is enabled, Yamux will call `WriteAsync(ReadOnlySequence<byte>, CancellationToken)` instead of the single-segment `WriteAsync`, passing a sequence of pending frame bytes.

```csharp
public bool SupportsBatching => true;
```

### Batched WriteAsync

When batching is enabled, you must implement the `ReadOnlySequence<byte>` overload of `WriteAsync`. The default implementation simply iterates over segments and calls the single-segment `WriteAsync` for each, which defeats the purpose of batching.

```csharp
public async ValueTask WriteAsync(ReadOnlySequence<byte> data, CancellationToken cancellationToken = default)
{
    // Use a transport-specific coalescing strategy:
    // - Socket: use SocketAsyncEventArgs.BufferList for scatter-write
    // - Pipe: copy segments into a single PipeWriter span then flush once
    // - Stream: copy into a pre-allocated buffer and write once
}
```

### FlushAsync

When batching is enabled, the session may call `FlushAsync` to ensure all buffered data has been written to the underlying transport. Provide a meaningful implementation if your transport maintains internal buffers:

```csharp
public async ValueTask FlushAsync(CancellationToken cancellationToken = default)
{
    await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
}
```

### Session-level override

You can also force batching on or off at the session level via `SessionOptions.WriteSegmentBatchingEnabled`, which overrides the transport's `SupportsBatching` value:

```csharp
var options = new SessionOptions { WriteSegmentBatchingEnabled = true };
await using var session = new Session(transport, isClient: true, options);
```

### Built-in examples

| Transport | SupportsBatching | Strategy |
|-----------|-----------------|----------|
| `SocketPeer` | `true` | Uses `Socket.SendAsync` with `BufferList` (scatter-write) |
| `PipePeer` | `true` | Copies segments into `PipeWriter.GetSpan`/`Advance`, single flush |
| `StreamPeer` | `false` | Does not opt in; writes each segment individually |

## Important Notes

- `ReadAsync` must return `0` when the remote end gracefully closes the connection. Yamux treats a `0` read as a connection close and will terminate the session.
- `WriteAsync` should throw if the underlying transport fails. Yamux propagates the exception and closes the session with an appropriate error code.
- The `Close` method is called during session shutdown and should release transport resources. If you want the transport to outlive the session, set `keepTransportOpenOnClose: true` in the `Session` constructor or extension methods.