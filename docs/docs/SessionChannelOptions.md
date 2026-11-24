---
title: SessionChannelOptions
---

# SessionChannelOptions

`SessionChannelOptions` configures flow control, framing, auto-tuning, and
per-channel statistics.

## Properties

| Property | Default | Description |
|---|---|---|
| `uint ReceiveWindowSize` | `256 KB` | Initial receive window. The protocol initial window is fixed at 256 KB; larger values are advertised immediately after the channel opens. Minimum 256 KB. |
| `uint ReceiveWindowUpperBound` | `16 MiB` | Maximum window for auto-tuning. Minimum 256 KB. |
| `uint MaxDataFrameSize` | `16 KiB` | Maximum data payload per frame. Larger writes are split across frames. |
| `bool AutoTuneReceiveWindowSize` | `true` | Grow/shrink the window using RTT and consumption rate, bounded by `ReceiveWindowUpperBound`. |
| `bool EnableStatistics` | `false` | Enable per-channel byte/bandwidth statistics. |
| `int StatisticsSampleInterval` | `1000` | Per-channel statistics sampling interval in milliseconds. |

## Validation

`Validate()` throws `System.ComponentModel.DataAnnotations.ValidationException`
when:

- `ReceiveWindowSize < 256 KB`
- `ReceiveWindowUpperBound < 256 KB`
- `ReceiveWindowSize > ReceiveWindowUpperBound`
- `MaxDataFrameSize == 0`

`OpenChannelAsync(SessionChannelOptions, ...)` validates the options before
creating the channel. `Session.Start()` validates `SessionOptions.DefaultChannelOptions`.

## Example

```csharp
var options = new SessionChannelOptions
{
    ReceiveWindowSize = 512 * 1024,
    ReceiveWindowUpperBound = 8 * 1024 * 1024,
    MaxDataFrameSize = 32 * 1024,
    AutoTuneReceiveWindowSize = true,
};
options.Validate();

using var channel = await session.OpenChannelAsync(options);
```

## Notes

- The receive window can only grow after a channel is created; accepting a
  channel with a smaller `ReceiveWindowSize` than its default is rejected.
- `MaxDataFrameSize` is a sender-side setting. A write is additionally limited
  by the peer's currently available window credit, so frames may be smaller than
  this value.
- For accepted channels, the input-buffer pause threshold is fixed from
  `SessionOptions.DefaultChannelOptions` at channel creation. To change the
  buffering threshold, configure it in the session defaults before accepting.
- Auto-tuning is RTT/consumption based, not a bandwidth estimator. Disable it if
  you need a fixed window.
