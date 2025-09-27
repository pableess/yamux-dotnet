# Channel Interfaces

Yamux defines several interfaces for working with channels:

## ISessionChannel
Represents a logical Yamux channel.

### Properties
- `uint Id` — Channel ID.

### Methods
- `Task CloseAsync(TimeSpan? timeout = null, CancellationToken? cancel = null)` — Closes the channel. Throws `InvalidOperationException` if already closed.
- `Task FlushWritesAsync(CancellationToken? cancel)` — Ensures all written data is flushed.

## IWriteOnlySessionChannel
- `ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken? token = null)` — Writes data to the channel.

## IReadOnlySessionChannel
- `PipeReader Input` — Gets the input pipe reader.

## IDuplexSessionChannel
- `Stream AsStream(bool leaveOpen = false)` — Gets a duplex stream for the channel.

---

See also: [Session](Session.md) for how to open and accept channels.