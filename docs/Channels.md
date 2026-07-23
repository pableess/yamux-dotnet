---
title: Channels
---

# Channel Interfaces

Yamux defines several interfaces for working with channels:

## ISessionChannel
Represents a logical Yamux channel.

### Properties
- `uint Id`  Channel ID.
- `bool IsClosed`  Indicates if the channel has been fully closed.
- `Statistics? Stats`  Bandwidth and byte statistics if enabled.

### Methods
- `void Close()`  Closes the channel for writing; data may still be read until the remote peer acknowledges the close.
- `Task FlushWritesAsync(CancellationToken? cancel)`  Ensures all written data is flushed.
- `bool WaitForRemoteClose(TimeSpan timeout)`  Waits for the channel close acknowledgement from the remote side.
- `Task<bool> WhenRemoteCloseAsync(TimeSpan timeout)`  Waits asynchronously for the channel close acknowledgement from the remote side.
- `bool WaitForRemoteAck(TimeSpan timeout)`  Waits for the remote peer to acknowledge the channel open.
- `Task<bool> WhenRemoteAckAsync(TimeSpan timeout)`  Waits asynchronously for the remote peer to acknowledge the channel open.
- `void Abort()`  Aborts the channel immediately, sending a RST to the remote peer if the channel is not already closed.

## IWriteOnlySessionChannel (extends ISessionChannel)
- `ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken? token = null)`  Writes data to the channel.

## IReadOnlySessionChannel (extends ISessionChannel)
- `PipeReader Input`  Gets the input pipe reader.

## IDuplexSessionChannel (extends IWriteOnlySessionChannel, IReadOnlySessionChannel)
- `Stream AsStream(bool leaveOpen = false)`  Gets a duplex stream for the channel.

---

See also: [Session](Session.md) for how to open and accept channels.