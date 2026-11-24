# Yamux — .NET Multiplexing Protocol

[![NuGet](https://img.shields.io/nuget/v/Yamux.svg)](https://www.nuget.org/packages/Yamux)
[![NuGet Downloads](https://img.shields.io/nuget/dt/Yamux.svg)](https://www.nuget.org/packages/Yamux)
[![License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-9.0%20|%2010.0%20|%2011.0-512BD4)](https://dotnet.microsoft.com/)
[![AOT Compatible](https://img.shields.io/badge/AOT-annotated-green)](https://learn.microsoft.com/dotnet/core/deploying/native-aot/)

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="docs/img/yamux-dotnet.png">
  <img alt="yamux" src="docs/img/yamux-dotnet.png" width="700">
</picture>

Yamux is a **high-performance .NET library** implementing the [HashiCorp Yamux multiplexing protocol](https://github.com/hashicorp/yamux). It enables multiple reliable, ordered, independent streams (channels) over a **single** underlying connection such as TCP — drastically reducing connection overhead in distributed systems.

```bash
dotnet add package Yamux
```

## Features

| Feature | Description |
|---------|-------------|
| **Multiplexing** | Run hundreds of logical streams over a single connection |
| **Flow Control** | Configurable receive windows with RTT-based auto-tuning and enforced window limits |
| **Keep-Alive** | Built-in keep-alive pings with RTT measurement; a failed ping closes the session |
| **Metrics** | OpenTelemetry-consumable metrics via `System.Diagnostics.Metrics` |
| **Statistics** | Per-session and per-channel bandwidth tracking with `Sampled` events |
| **Pipelines** | Built on `System.IO.Pipelines` for low-allocation receive buffering |
| **Pluggable Transports** | Works over `Stream`, `Socket`, `IDuplexPipe`, or a custom `ITransport` |
| **AOT Annotations** | `IsAotCompatible` is enabled for Native AOT analysis |
| **Graceful Shutdown** | `CloseOpenChannelsAsync` sends GoAway and drains channels; `CloseAsync` force-closes |
| **Stream API** | `AsStream()` for compatibility with stream-based code |
| **Interop Tested** | Cross-implementation suite against Go yamux v0.1.2 |

## Compatibility

- **Target frameworks:** .NET 9, .NET 10, .NET 11
- **Reference implementation:** HashiCorp yamux v0.1.2 (interop tests run in both directions)
- **Transports:** TCP sockets, arbitrary `Stream`s, `IDuplexPipe`, or custom `ITransport`
- **Wire protocol:** yamux protocol version 0 (the only version defined by the spec)

## Quick Start

### Server

```csharp
using System.Net;
using System.Net.Sockets;
using Yamux;

var listener = new Socket(SocketType.Stream, ProtocolType.Tcp);
listener.Bind(new IPEndPoint(IPAddress.Loopback, 5000));
listener.Listen();

var socket = await listener.AcceptAsync();
await using var session = socket.AsYamuxSession(isClient: false);
session.Start();

using var channel = await session.AcceptAsync();

var result = await channel.Input.ReadAsync();
try
{
    // Process result.Buffer ...
}
finally
{
    channel.Input.AdvanceTo(result.Buffer.End);
}

await channel.WriteAsync("Hello from the server"u8.ToArray());
channel.Close();
```

### Client

```csharp
using System.Net;
using System.Net.Sockets;
using Yamux;

var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, 5000));

await using var session = socket.AsYamuxSession(isClient: true);
session.Start();

using var channel = await session.OpenChannelAsync();
await channel.WriteAsync("Hello from Yamux!"u8.ToArray());
channel.Close();
```

## Working with Channels

### Opening and Accepting

```csharp
// Client: open a channel. Set waitForAcknowledgement: true to wait for the
// peer's SYN/ACK before the task completes.
using var channel = await session.OpenChannelAsync(waitForAcknowledgement: true);

// Server: accept an incoming channel. Reset channels are skipped.
using var accepted = await session.AcceptAsync();
```

`OpenChannelAsync` validates the channel options and throws
`System.ComponentModel.DataAnnotations.ValidationException` for invalid
configuration. `AcceptAsync(SessionChannelOptions, CancellationToken)` applies
custom options to the accepted channel.

### Reading and Writing

```csharp
await channel.WriteAsync(myData);

var result = await channel.Input.ReadAsync();
try
{
    foreach (var segment in result.Buffer)
    {
        // Consume segment.Span ...
    }
}
finally
{
    channel.Input.AdvanceTo(result.Buffer.End);
}
```

`WriteAsync` respects the remote receive window and waits up to
`SessionOptions.StreamSendTimeout` (default 75 s). Completing the `Input` reader
signals that the application will not read further data; the next receive-loop
flush observes the completed reader and half-closes the channel's write side.

### Closing and Disposing

```csharp
channel.Close();                              // graceful write-side FIN
await channel.WhenRemoteCloseAsync(TimeSpan.FromSeconds(5));
channel.Dispose();                            // release resources
```

- `Close()` is idempotent and is a no-op after disposal.
- `WhenRemoteCloseAsync` returns `false` on timeout and tolerates concurrent disposal.
- `Abort()` sends RST and completes the read side immediately.
- Writes after close throw `SessionChannelException` with `ChannelWriteClosed`.

## Configuration

### Session Options

```csharp
var options = new SessionOptions
{
    AcceptBacklog = 256,
    WriteQueueDepth = 100,
    EnableKeepAlive = true,
    KeepAliveInterval = TimeSpan.FromSeconds(30),
    StreamOpenTimeout = TimeSpan.FromSeconds(75),
    StreamSendTimeout = TimeSpan.FromSeconds(75),
    StreamCloseTimeout = TimeSpan.FromMinutes(5),
    ConnectionWriteTimeout = TimeSpan.FromSeconds(10),
    SessionCloseTimeout = TimeSpan.FromSeconds(30),
    MaxChannels = 1024,
    MaxIncomingFrameSize = 16 * 1024 * 1024,
    EnableStatistics = false,
    StatisticsSampleInterval = 1000,
    EnableMetrics = true,
    DefaultChannelOptions = new SessionChannelOptions
    {
        ReceiveWindowSize = 256 * 1024,
        ReceiveWindowUpperBound = 16 * 1024 * 1024,
    },
};

await using var session = socket.AsYamuxSession(
    isClient: true,
    leaveOpen: false,
    options: options);
session.Start();
```

| Property | Default | Purpose |
|----------|---------|---------|
| `AcceptBacklog` | `256` | Maximum incoming channels queued for acceptance; excess streams are reset |
| `WriteQueueDepth` | `100` | Outgoing frame queue depth before backpressure |
| `EnableKeepAlive` | `true` | Send periodic pings |
| `KeepAliveInterval` | `30 s` | Delay between keep-alive pings |
| `StreamOpenTimeout` | `75 s` | Max wait for SYN/ACK; a timeout closes the session (`TimeSpan.Zero` disables) |
| `StreamSendTimeout` | `75 s` | Max wait for send-window credit |
| `StreamCloseTimeout` | `5 min` | Max wait for the peer's FIN after a local close, then RST |
| `ConnectionWriteTimeout` | `10 s` | Max wait to enqueue a frame when the write queue is full, and the ping-response timeout |
| `SessionCloseTimeout` | `30 s` | Max wait for the frame reader during shutdown |
| `MaxChannels` | `1024` | Maximum concurrent channels |
| `MaxIncomingFrameSize` | `16 MiB` | Absolute per-frame payload cap |
| `EnableStatistics` | `false` | Session byte/bandwidth statistics |
| `StatisticsSampleInterval` | `1000 ms` | Statistics sampling period |
| `EnableMetrics` | `true` | `System.Diagnostics.Metrics` instruments |
| `WriteSegmentBatchingEnabled` | `null` | Overrides `ITransport.SupportsBatching` |
| `MinWriteBatchSize` | `8192` | Minimum bytes before a batched transport write |

### Channel Options

```csharp
var channelOptions = new SessionChannelOptions
{
    ReceiveWindowSize = 512 * 1024,
    ReceiveWindowUpperBound = 8 * 1024 * 1024,
    MaxDataFrameSize = 32 * 1024,
    AutoTuneReceiveWindowSize = true,
    EnableStatistics = false,
};
channelOptions.Validate();
```

| Property | Default | Description |
|----------|---------|-------------|
| `ReceiveWindowSize` | `256 KB` | Initial receive window; minimum and protocol initial value is 256 KB |
| `ReceiveWindowUpperBound` | `16 MiB` | Maximum auto-tuned window; minimum 256 KB |
| `MaxDataFrameSize` | `16 KiB` | Maximum payload per data frame |
| `AutoTuneReceiveWindowSize` | `true` | Grow/shrink the window using RTT and consumption rate |
| `EnableStatistics` | `false` | Per-channel byte/bandwidth statistics |
| `StatisticsSampleInterval` | `1000 ms` | Per-channel statistics sampling period |

`ReceiveWindowSize` cannot be smaller than 256 KB or larger than
`ReceiveWindowUpperBound`; `MaxDataFrameSize` must be non-zero.

## Transports

Yamux provides built-in transport wrappers:

| Transport | Extension Method | Description |
|-----------|-----------------|-------------|
| `Stream` | `stream.AsYamuxSession(...)` | Wraps any `System.IO.Stream` |
| `Socket` | `socket.AsYamuxSession(...)` | Wraps `System.Net.Sockets.Socket` |
| `IDuplexPipe` | `pipe.AsYamuxSession(...)` | Wraps `System.IO.Pipelines.IDuplexPipe` |

Implement `ITransport` for custom transports (named pipes, Unix sockets, etc.).
All extension methods accept `isClient`, `leaveOpen`, and `SessionOptions? options`.
See [Transport](docs/docs/Transport.md) for the interface and batching guidance.

## Performance

- Low-allocation receive path built on `System.IO.Pipelines`
- Batched scatter/gather writes for `SocketPeer` and `PipePeer`
- Pooled `IValueTaskSource` instances for frame completion
- Reusable batch header buffers and no per-frame `ArrayPool` rent
- Lock-free stream ID generation with exhaustion detection

Run the benchmarks:

```bash
dotnet run -c Release --project benchmarks/Yamux.Benchmark
```

Compare C# and Go implementations:

```bash
# Linux / macOS
bash benchmarks/compare.sh

# Windows
pwsh benchmarks/compare.ps1
```

The benchmark project includes:

| Benchmark | Topology |
|-----------|----------|
| `TcpBaselineAsync` | Raw TCP baseline |
| `TcpYamuxAsync` | C# server → C# client over TCP (server-initiated channels) |
| `CsharpToCsharpAsync` | C# client → C# server over TCP (mirrors the Go benchmark direction) |
| `GoIntegrationAsync` | C# client → Go server |
| `MemoryYamuxAsync` | C# ↔ C# over in-memory pipes |
| `TcpNerdbankAsync` / `MemoryNerdbankAsync` | Nerdbank.Streams comparisons |

The Go baseline (`GoRawTCP`, `GoYamux`) comes from `benchmarks/go-benchmark`.
See [Performance](docs/docs/Performance.md) for methodology and tuning guidance.

## Metrics (OpenTelemetry)

Yamux emits `System.Diagnostics.Metrics` instruments when `EnableMetrics` is
`true` (the default). Each session owns its meter and disposes it with the
session, so instruments are unregistered on shutdown. Measurements carry
`session_id` (`"client"`/`"server"`) and `is_client` tags.

| Metric | Type | Description |
|--------|------|-------------|
| `yamux.channels.opened` | Counter | Total channels opened |
| `yamux.channels.closed` | Counter | Total channels closed |
| `yamux.channels.active` | Observable gauge | Currently active channels |
| `yamux.bytes.sent` | Counter | Payload bytes sent |
| `yamux.bytes.received` | Counter | Payload bytes received |
| `yamux.frames.sent` | Counter | Frames sent |
| `yamux.frames.received` | Counter | Frames received |
| `yamux.errors` | Counter | Session receive-loop errors |
| `yamux.rtt.ms` | Histogram | Keep-alive/ping RTT in milliseconds |
| `yamux.write_queue.depth` | Observable gauge | Current outgoing queue depth |

## Statistics

Enable session statistics with `SessionOptions.EnableStatistics` and channel
statistics with `SessionChannelOptions.EnableStatistics`. Totals count payload
bytes (not frame headers); `Sampled` fires on the sampling timer.

```csharp
var options = new SessionOptions
{
    EnableStatistics = true,
    StatisticsSampleInterval = 1000,
};

await using var session = socket.AsYamuxSession(isClient: true, options: options);
session.Start();

var stats = session.Stats;
if (stats is not null)
{
    stats.Sampled += (_, _) =>
        Console.WriteLine($"TX {stats.SendRate}/s  RX {stats.ReceiveRate}/s");
}
```

## Documentation

| Topic | Link |
|-------|------|
| Getting started | [Getting Started](docs/docs/getting-started.md) |
| Session lifecycle | [Session](docs/docs/Session.md) |
| Channels | [Channels](docs/docs/Channels.md) |
| Session options | [SessionOptions](docs/docs/SessionOptions.md) |
| Channel options | [SessionChannelOptions](docs/docs/SessionChannelOptions.md) |
| Statistics | [Statistics](docs/docs/Statistics.md) |
| Custom transports | [Transport](docs/docs/Transport.md) |
| Exceptions | [Exceptions](docs/docs/Exceptions.md) |
| Performance | [Performance](docs/docs/Performance.md) |

## Samples

- **[Sample](samples/Sample/)** — Interactive server/client with live Spectre.Console statistics
- **[FileTransfer](samples/FileTransfer/)** — Multi-file transfer over Yamux channels

## Building and Testing

```bash
dotnet build
dotnet test
```

Tests run against .NET 9, .NET 10, and .NET 11. The Go interoperability suite
builds `benchmarks/go-interop` and runs when the Go toolchain is installed:

```bash
dotnet test test/Yamux.Interop.Tests
```

It verifies both directions, FIN/RST/GoAway handling, ping, multi-stream
transfers, custom windows, and a 4 MiB transfer against a 1 MiB Go window.

## Project Structure

```
src/                         — Yamux library
├── Protocol/                — Wire protocol (frames, constants, enums)
└── Internal/                — Reader, writer, channel manager, flow control
test/Yamux.Tests/            — Protocol, transport, stress, and robustness tests
test/Yamux.Interop.Tests/    — Go interoperability tests
benchmarks/Yamux.Benchmark/  — BenchmarkDotNet benchmarks
benchmarks/go-interop/       — Go peer used by the interop suite
benchmarks/go-server/        — Go server used by C#→Go benchmarks
benchmarks/go-benchmark/     — Go↔Go benchmark
samples/                     — Sample applications
docs/                        — Documentation site
```

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md).

## License

MIT — see [LICENSE](LICENSE).
