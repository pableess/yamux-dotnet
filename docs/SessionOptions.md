# SessionOptions

The `SessionOptions` class configures a Yamux session.

## Properties
- `int AcceptBacklog` — Maximum number of pending accepted channels (default: 256).
- `bool EnableKeepAlive` — Enables keep-alive pings (default: true).
- `TimeSpan KeepAliveInterval` — Interval for keep-alive pings (default: 30s).
- `TimeSpan StreamCloseTimeout` — Timeout for closing streams (default: 5 min).
- `TimeSpan StreamSendTimout` — Timeout for sending data (default: 75s).
- `SessionChannelOptions DefaultChannelOptions` — Default options for new channels.
- `bool EnableStatistics` — Enables statistics collection.
- `int StatisticsSampleInterval` — How often to sample statistics (ms, default: 1000).

## Example
```csharp
var options = new SessionOptions {
    EnableKeepAlive = true,
    KeepAliveInterval = TimeSpan.FromSeconds(10),
    DefaultChannelOptions = new SessionChannelOptions { ReceiveWindowUpperBound = 4 * 1024 * 1024 }
};
```
