# SessionChannelOptions

The `SessionChannelOptions` class configures the behavior of a Yamux session channel.

## Properties
- `uint ReceiveWindowSize` — Initial receive window size (default: 256 KB). Yamux spec default is 256 KB.
- `uint ReceiveWindowUpperBound` — Maximum receive window size (default: 16 MiB).
- `uint MaxDataFrameSize` — Maximum data frame payload size (default: 16 KiB).
- `bool AutoTuneReceiveWindowSize` — Automatically increase window size for throughput (default: true).
- `bool EnableStatistics` — Enables statistics collection.
- `int StatisticsSampleInterval` — How often to sample statistics (ms, default: 1000).

## Methods
- `void Validate()` — Validates the options. Throws `ValidationException` if invalid.

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
- `ValidationException` — Thrown if options are invalid.
