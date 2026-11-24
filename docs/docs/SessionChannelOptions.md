---
title: SessionChannelOptions
---

# SessionChannelOptions

The `SessionChannelOptions` class configures the behavior of a Yamux session channel.

## Properties
- `uint ReceiveWindowSize` - Initial receive window size (default: 256 KB, minimum: 256 KB). Yamux fixes the wire-level initial window at 256 KB; larger values are advertised immediately after the channel opens. Values below 256 KB are rejected.
- `uint ReceiveWindowUpperBound` - Maximum receive window size (default: 16 MiB, minimum: 256 KB).
- `uint MaxDataFrameSize` - Maximum data frame payload size (default: 16 KiB).
- `bool AutoTuneReceiveWindowSize` - Automatically increase window size for throughput (default: true).
- `bool EnableStatistics` - Enables statistics collection.
- `int StatisticsSampleInterval` - How often to sample statistics (ms, default: 1000).

## Methods
- `void Validate()` - Validates the options. Throws `ValidationException` if invalid.

## Example
```csharp
var options = new SessionChannelOptions {
    ReceiveWindowSize = 512 * 1024,
    ReceiveWindowUpperBound = 8 * 1024 * 1024,
    MaxDataFrameSize = 32 * 1024,
    AutoTuneReceiveWindowSize = true
};
options.Validate();
```

## Exceptions
- `ValidationException` - Thrown if options are invalid. `ReceiveWindowSize` and `ReceiveWindowUpperBound` must be at least 256 KB, `ReceiveWindowSize` must not exceed `ReceiveWindowUpperBound`, and `MaxDataFrameSize` must be greater than zero.