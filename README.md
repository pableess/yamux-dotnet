# Yamux — .NET Multiplexing Protocol

[![NuGet](https://img.shields.io/nuget/v/Yamux.svg)](https://www.nuget.org/packages/Yamux)
[![NuGet Downloads](https://img.shields.io/nuget/dt/Yamux.svg)](https://www.nuget.org/packages/Yamux)
[![License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-9.0%20|%2010.0-512BD4)](https://dotnet.microsoft.com/)
[![AOT Compatible](https://img.shields.io/badge/AOT-compatible-green)](https://learn.microsoft.com/dotnet/core/deploying/native-aot/)

Yamux is a **high-performance .NET library** implementing the [HashiCorp Yamux multiplexing protocol](https://github.com/hashicorp/yamux). It enables multiple reliable, ordered, independent streams (channels) over a **single** underlying connection such as TCP — drastically reducing connection overhead in distributed systems.

```
dotnet add package Yamux
```

## Features

| Feature | Description |
|---------|-------------|
| **Multiplexing** | Run hundreds of logical streams over a single connection |
| **Flow Control** | Configurable receive windows with automatic bandwidth-based tuning |
| **Keep-Alive** | Built-in keep-alive pings with RTT measurement |
| **Metrics** | OpenTelemetry-compatible metrics via `System.Diagnostics.Metrics` |
| **Statistics** | Per-session and per-channel bandwidth tracking with `Sampled` events |
| **Pipelines** | Built on `System.IO.Pipelines` for high-throughput, low-allocation I/O |
| **Pluggable Transports** | Works over `Stream`, `Socket`, `IDuplexPipe`, or custom `ITransport` |
| **AOT Compatible** | Fully compatible with Native AOT publish |
| **Graceful Shutdown** | Drain channels before closing; configurable timeouts |
| **Stream API** | `AsStream()` for compatibility with stream-based code |

## Quick Start

### Server

```csharp
using System.Net.Sockets;
using Yamux;

var listener = new Socket(SocketType.Stream, ProtocolType.Tcp);
listener.Bind(new IPEndPoint(IPAddress.Loopback, 5000));
listener.Listen();
var socket = await listener.AcceptAsync();

await using var session = socket.AsYamuxSession(isClient: false);
session.Start();

// Accept and handle incoming channels
var channel = await session.AcceptAsync();
// read from channel.Input, write via channel.WriteAsync(...)
```

### Client

```csharp
using System.Net.Sockets;
using Yamux;

var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, 5000));

await using var session = socket.AsYamuxSession(isClient: true);
session.Start();

// Open a new multiplexed channel
var channel = await session.OpenChannelAsync();
await channel.WriteAsync("Hello from Yamux!"u8.ToArray());
```

## Working with Channels

### Opening and Accepting

```csharp
// Client: open a channel (optionally wait for remote acknowledgement)
var channel = await session.OpenChannelAsync(waitForAcknowledgement: true);

// Server: accept an incoming channel
var channel = await session.AcceptAsync();
```

### Reading and Writing

```csharp
// Write data
await channel.WriteAsync(myData);

// Read data via System.IO.Pipelines
var result = await channel.Input.ReadAsync();
// process result.Buffer ...
channel.Input.AdvanceTo(result.Buffer.End);

// Or use the stream API
var stream = channel.AsStream();
var bytesRead = await stream.ReadAsync(buffer);
```

### Closing

```csharp
channel.Close();                             // graceful FIN close
await channel.WhenRemoteCloseAsync(TimeSpan.FromSeconds(5));
channel.Dispose();                           // release resources
```

## Configuration

### Session Options

```csharp
var options = new SessionOptions
{
    EnableKeepAlive = true,
    KeepAliveInterval = TimeSpan.FromSeconds(30),
    MaxChannels = 1024,
    EnableStatistics = true,
    EnableMetrics = true,
    DefaultChannelOptions = new SessionChannelOptions
    {
        ReceiveWindowSize = 256 * 1024,
        ReceiveWindowUpperBound = 16 * 1024 * 1024,
        AutoTuneReceiveWindowSize = true,
    }
};
```

### Channel Options

| Property | Default | Description |
|----------|---------|-------------|
| `ReceiveWindowSize` | 256 KB | Initial receive window per channel |
| `ReceiveWindowUpperBound` | 16 MB | Maximum auto-tuned window size |
| `MaxDataFrameSize` | 16 KB | Maximum payload per data frame |
| `AutoTuneReceiveWindowSize` | `true` | Dynamically adjust window based on RTT |

## Transports

Yamux provides built-in transport wrappers:

| Transport | Extension Method | Description |
|-----------|-----------------|-------------|
| `Stream` | `stream.AsYamuxSession(...)` | Wraps any `System.IO.Stream` |
| `Socket` | `socket.AsYamuxSession(...)` | Wraps `System.Net.Sockets.Socket` |
| `IDuplexPipe` | `pipe.AsYamuxSession(...)` | Wraps `System.IO.Pipelines.IDuplexPipe` |

Implement `ITransport` for custom transports (named pipes, Unix sockets, etc.).

## Performance

- **Zero-copy** frame writing via `System.IO.Pipelines`
- **Pooled** `IValueTaskSource` for write completion signaling
- **Lock-free** stream ID generation
- **Async waiter** patterns avoid blocking threads
- See [Performance](docs/Performance.md) for benchmarks vs. Go Yamux and Nerdbank.Streams

## Metrics (OpenTelemetry)

Yamux emits metrics compatible with OpenTelemetry when `EnableMetrics` is `true`:

| Metric | Type | Description |
|--------|------|-------------|
| `yamux.channels.opened` | Counter | Total channels opened |
| `yamux.channels.closed` | Counter | Total channels closed |
| `yamux.channels.active` | Gauge | Currently active channels |
| `yamux.bytes.sent` | Counter | Total bytes sent |
| `yamux.bytes.received` | Counter | Total bytes received |
| `yamux.frames.sent` | Counter | Total frames sent |
| `yamux.frames.received` | Counter | Total frames received |
| `yamux.errors` | Counter | Total session errors |
| `yamux.rtt.ms` | Histogram | Round-trip time in milliseconds |
| `yamux.write_queue.depth` | Gauge | Write queue depth |

## Documentation

| Topic | Link |
|-------|------|
| Session lifecycle | [Session](docs/Session.md) |
| Channels | [Channels](docs/Channels.md) |
| Session options | [SessionOptions](docs/SessionOptions.md) |
| Channel options | [SessionChannelOptions](docs/SessionChannelOptions.md) |
| Statistics | [Statistics](docs/Statistics.md) |
| Custom transports | [Transport](docs/Transport.md) |
| Exceptions | [Exceptions](docs/Exceptions.md) |
| Performance | [Performance](docs/Performance.md) |

## Samples

- **[Sample](samples/Sample/)** — Interactive server/client with live Spectre.Console statistics
- **[FileTransfer](samples/FileTransfer/)** — Multi-file transfer over Yamux channels

## Benchmarks

```bash
dotnet run -c Release --project benchmarks/Yamux.Benchmark
```

## Building

```bash
dotnet build
```

## Testing

```bash
dotnet test
```

## Project Structure

```
src/                  — Yamux library
├── Protocol/         — Wire protocol (frames, constants, enums)
├── Internal/         — Internal implementation
test/                 — xUnit test suite
benchmarks/           — BenchmarkDotNet benchmarks
samples/              — Sample applications
docs/                 — Documentation
```

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md).

## License

MIT — see [LICENSE](LICENSE).