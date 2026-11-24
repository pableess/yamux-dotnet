---
title: SessionOptions
---

# SessionOptions

`SessionOptions` configures a `Session`: channel defaults, timeouts, queue
depths, keep-alive, statistics, metrics, and write batching.

## Properties

| Property | Default | Description |
|---|---|---|
| `int AcceptBacklog` | `256` | Maximum number of incoming channels queued for `AcceptAsync`. When the backlog or `MaxChannels` limit is exceeded, the new stream is reset. |
| `int WriteQueueDepth` | `100` | Maximum outgoing frames buffered before write backpressure is applied. |
| `bool EnableKeepAlive` | `true` | Send keep-alive pings. A failed ping closes the session. |
| `TimeSpan KeepAliveInterval` | `30 s` | Delay between keep-alive pings. |
| `TimeSpan StreamOpenTimeout` | `75 s` | Maximum wait for SYN/ACK when opening with `waitForAcknowledgement: true`. A timeout closes the session. `TimeSpan.Zero` disables the timeout. |
| `TimeSpan StreamSendTimeout` | `75 s` | Maximum wait for remote send-window credit before a write fails with `TimeoutException`. |
| `TimeSpan StreamCloseTimeout` | `5 min` | After sending FIN, wait this long for the peer FIN before forcing a reset. |
| `TimeSpan ConnectionWriteTimeout` | `10 s` | Maximum time to enqueue an outgoing frame when the write queue is full, and the timeout for ping responses. |
| `TimeSpan SessionCloseTimeout` | `30 s` | Maximum wait for the frame reader to finish during `CloseAsync`. |
| `SessionChannelOptions DefaultChannelOptions` | defaults | Options used for newly opened channels and applied to accepted channels. |
| `int MaxChannels` | `1024` | Maximum concurrent channels. New incoming streams over the limit are reset. |
| `uint MaxIncomingFrameSize` | `16 MiB` | Absolute maximum payload size for one incoming data frame. Larger frames are a protocol error. |
| `bool EnableStatistics` | `false` | Enable session byte/bandwidth statistics. |
| `int StatisticsSampleInterval` | `1000` | Statistics sampling interval in milliseconds. |
| `bool EnableMetrics` | `true` | Emit `System.Diagnostics.Metrics` instruments. Instruments are created per session and disposed with it. |
| `bool? WriteSegmentBatchingEnabled` | `null` | `null` uses `ITransport.SupportsBatching`; `true`/`false` overrides it. |
| `int MinWriteBatchSize` | `8192` | Minimum accumulated bytes before a batched transport write. Lower values flush sooner. |

## Example

```csharp
var options = new SessionOptions
{
    EnableKeepAlive = true,
    KeepAliveInterval = TimeSpan.FromSeconds(10),
    StreamOpenTimeout = TimeSpan.FromSeconds(30),
    ConnectionWriteTimeout = TimeSpan.FromSeconds(5),
    SessionCloseTimeout = TimeSpan.FromSeconds(20),
    MaxChannels = 2048,
    MaxIncomingFrameSize = 8 * 1024 * 1024,
    DefaultChannelOptions = new SessionChannelOptions
    {
        ReceiveWindowSize = 512 * 1024,
        ReceiveWindowUpperBound = 8 * 1024 * 1024,
    },
};

await using var session = socket.AsYamuxSession(
    isClient: true,
    leaveOpen: false,
    options: options);
session.Start();
```

## Notes

- `DefaultChannelOptions.Validate()` runs in `Session.Start()`; options passed
  explicitly to `OpenChannelAsync`/`AcceptAsync` are validated when used.
- `SessionCloseTimeout` bounds only the frame-reader shutdown wait. The outgoing
  writer is stopped after the frame reader exits.
- `WriteSegmentBatchingEnabled` has no effect unless the transport implements
  `WriteAsync(ReadOnlySequence<byte>, ...)` efficiently. See
  [Transport](Transport.md).
- There is no session-level read timeout. Liveness is provided by keep-alive;
  set `EnableKeepAlive = false` only when the transport has its own liveness
  detection.

---

See [SessionChannelOptions](SessionChannelOptions.md) for per-channel settings.
