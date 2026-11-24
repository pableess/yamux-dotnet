---
title: Statistics
---

# Statistics

`Statistics` tracks payload throughput for a session or channel. It is created
automatically when `SessionOptions.EnableStatistics` or
`SessionChannelOptions.EnableStatistics` is `true`.

## Properties

| Property | Type | Description |
|---|---|---|
| `TotalBytesSent` | `ulong` | Payload bytes sent since creation. Frame headers are not included. |
| `TotalBytesReceived` | `ulong` | Payload bytes received since creation. Frame headers are not included. |
| `SendRate` | `ByteSize` | Send bandwidth over the most recent sample interval. |
| `ReceiveRate` | `ByteSize` | Receive bandwidth over the most recent sample interval. |
| `SampleInterval` | `TimeSpan` | Sampling period. |

## Events

| Event | Description |
|---|---|
| `Sampled` | Raised on the sampling timer thread after each sample. Keep handlers fast; exceptions thrown by handlers are swallowed so they cannot crash the timer thread. |

## Methods

```csharp
public Statistics(int intervalMilliseconds, CancellationToken cancellationToken);
public void UpdateSent(ulong bytesSent);
public void UpdateReceived(ulong bytesReceived);
public void Dispose();
```

- The timer starts in the constructor.
- `UpdateSent`/`UpdateReceived` throw `ObjectDisposedException` after `Dispose`.
- `Dispose` stops the timer; totals remain readable.

## Enabling statistics

```csharp
var options = new SessionOptions
{
    EnableStatistics = true,
    StatisticsSampleInterval = 1000,
    DefaultChannelOptions = new SessionChannelOptions
    {
        EnableStatistics = true,
    },
};

await using var session = socket.AsYamuxSession(
    isClient: true,
    options: options);
session.Start();

var stats = session.Stats;
if (stats is not null)
{
    stats.Sampled += (_, _) =>
        Console.WriteLine($"TX {stats.SendRate}/s  RX {stats.ReceiveRate}/s");
}

using var channel = await session.OpenChannelAsync();
// channel.Stats is also populated when channel statistics are enabled.
```

## Notes

- Session statistics share the session's keep-alive cancellation token and stop
  sampling when the session is closed. Dispose the session to release the timer.
- Channel statistics timers are stopped when the channel is disposed; session
  shutdown disposes its channels.
- On multiplexed connections, only payload is counted, so totals are comparable
  across frame-size configurations.
