---
title: Exceptions
---

# Exceptions

## YamuxException

`YamuxException` is the abstract base class for all Yamux-defined exceptions.

```csharp
public abstract class YamuxException : Exception
```

## SessionException

Thrown for session-level failures (protocol errors, shutdown, GoAway).

```csharp
public class SessionException : YamuxException
{
    public SessionErrorCode ErrorCode { get; set; }
    public SessionTermination? GoAwayCode { get; set; }
}
```

### SessionErrorCode

| Value | Meaning |
|---|---|
| `InvalidVersion` | A frame used an unsupported protocol version |
| `InvalidMsgType` | A frame used an unknown frame type |
| `SessionShutdown` | The session is closed or closing |
| `RemoteGoAway` | The peer sent GoAway; new channels are refused |
| `LocalGoAway` | This side sent GoAway; new channels are refused |
| `StreamsExhausted` | The stream ID space is exhausted |
| `RecvWindowExceeded` | The peer exceeded the advertised receive window or an incoming frame exceeded `MaxIncomingFrameSize` |
| `StreamError` | Underlying transport error |
| `StreamClosed` | Underlying transport closed, including keep-alive failure |

`SessionException` is thrown by `OpenChannelAsync`, `AcceptAsync`, `PingAsync`,
and channel reads when the session fails.

## SessionChannelException

Thrown for channel-specific failures.

```csharp
public class SessionChannelException : YamuxException
{
    public ChannelErrorCode ErrorCode;
}
```

### ChannelErrorCode

| Value | Meaning |
|---|---|
| `ChannelRejected` | The peer reset the channel (RST), or an accepted/opened channel was rejected |
| `ChannelWriteClosed` | The write side is closed (local FIN, reset, or session closing) |
| `ChannelClosed` | The channel is fully closed |
| `SessionClosed` | The session closed before an open/accept completed |
| `InvalidChannelState` | The operation is not valid in the channel's current state |

Typical cases:

- `WriteAsync` after `Close()`/`Abort()`/`Dispose()` → `ChannelWriteClosed` or `ChannelClosed`.
- Reading after an RST → `ChannelRejected`.
- `OpenChannelAsync(waitForAcknowledgement: true)` rejected by the peer → `ChannelRejected`.
- `AcceptAsync` or wait-for-ACK completing because the session shut down → `SessionClosed`/`SessionShutdown`.

## ValidationException

`System.ComponentModel.DataAnnotations.ValidationException` is thrown for invalid
`SessionChannelOptions`:

- receive window below 256 KB,
- receive window above the upper bound,
- `MaxDataFrameSize == 0`,
- reducing `ReceiveWindowSize` when accepting a channel.

## TimeoutException

Thrown when:

- a write cannot obtain send-window credit within `StreamSendTimeout`,
- a ping receives no response within `ConnectionWriteTimeout`,
- `OpenChannelAsync(waitForAcknowledgement: true)` exceeds `StreamOpenTimeout`
  (the session is then closed).

## OperationCanceledException

Thrown when a caller's `CancellationToken` is canceled. A canceled
`OpenChannelAsync(waitForAcknowledgement: true)` resets the opened stream.

## ObjectDisposedException

Most channel/session operations tolerate disposal. Exceptions are limited to
areas where the object has no meaningful state left, for example calling
`Statistics.UpdateSent`/`UpdateReceived` after `Statistics.Dispose`.

## Protocol errors

Protocol violations (invalid version/type, receive-window overrun, oversized
frames) cause the session to send GoAway with `ProtocolError` where possible and
then close. Pending channel operations fail with `SessionException` or
`SessionChannelException` depending on the call site.

---

See [Session](Session.md) and [Channels](Channels.md) for when each operation
throws.
