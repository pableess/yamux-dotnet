# Yamux (dotnet)

Yamux (dotnet) is a .NET 9 library implementing the [Yamux multiplexing protocol](https://github.com/hashicorp/yamux/blob/master/spec.md), enabling multiple reliable, ordered, and independent streams (channels) over a single underlying connection (such as TCP). This is useful for building high-performance network applications, tunneling, or protocols that require multiplexed communication.

## Features
- Full-duplex, multiplexed streams over a single connection
- Channel-based abstraction (`IDuplexSessionChannel`, `IReadOnlySessionChannel`, `IWriteOnlySessionChannel`)
- Configurable flow control with automatic window tuning
- Keep-alive and round-trip time (RTT) measurement
- Bandwidth and statistics tracking
- OpenTelemetry-compatible metrics via `System.Diagnostics.Metrics`
- Low allocations and high-performance design (uses `System.IO.Pipelines` to reduce buffer copies)
- AOT-compatible
- Graceful session shutdown with channel drain
- Pluggable transport layer (`Stream`, `Socket`, `IDuplexPipe`)
- .NET 9, async/await friendly

## Getting Started

### Install

```
dotnet add package Yamux --prerelease
```

### Basic Usage
```csharp
// Create a Yamux session over a stream (e.g., NetworkStream)
using var session = stream.AsYamuxSession(isClient: true, options: new SessionOptions { ... });
session.Start();

// Open a new channel
using var channel = await session.OpenChannelAsync();

// Write to the channel
await channel.WriteAsync(data, cancellationToken);

// Read from the channel
var result = await channel.Input.ReadAsync(cancellationToken);
```

### Server / Client Setup

```csharp
// Server side
var listener = new TcpListener(IPAddress.Loopback, 5000);
listener.Start();
var clientSocket = await listener.AcceptSocketAsync();
await using var session = clientSocket.AsYamuxSession(isClient: false);
session.Start();

// Accept incoming channels
var channel = await session.AcceptAsync();
var stream = channel.AsStream();
// Use the stream...
```

```csharp
// Client side
using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
await socket.ConnectAsync(IPAddress.Loopback, 5000);
await using var session = socket.AsYamuxSession(isClient: true);
session.Start();

var channel = await session.OpenChannelAsync();
// Read/write via channel.Input / channel.WriteAsync
```

### Stream Wrapper

```csharp
// Convert any channel to a System.IO.Stream
using var stream = channel.AsStream();

// Works with anything that expects a Stream
await stream.WriteAsync(data);
var bytesRead = await stream.ReadAsync(buffer);
```

### Channel Options

```csharp
var channel = await session.OpenChannelAsync(new SessionChannelOptions
{
    ReceiveWindowSize = 512 * 1024,       // 512KB initial window
    ReceiveWindowUpperBound = 8 * 1024 * 1024, // 8MB max window
    MaxDataFrameSize = 32 * 1024,         // 32KB max frame payload
    AutoTuneReceiveWindowSize = true,
});
```

See `samples/Sample` and `samples/FileTransfer` for more complete examples.

## Protocol
This library implements the [Yamux protocol specification](https://github.com/hashicorp/yamux/blob/master/spec.md) by HashiCorp.

## Documentation

| Topic | Description |
|-------|-------------|
| [Session](docs/Session.md) | Session lifecycle, opening/accepting channels, ping, close |
| [SessionOptions](docs/SessionOptions.md) | Session-level configuration options |
| [SessionChannelOptions](docs/SessionChannelOptions.md) | Per-channel configuration (window sizes, auto-tuning) |
| [Channels](docs/Channels.md) | ISessionChannel, IReadOnlySessionChannel, IWriteOnlySessionChannel, IDuplexSessionChannel |
| [Statistics](docs/Statistics.md) | Bandwidth tracking, send/receive rates, Sampled event |
| [Transport](docs/Transport.md) | Implementing custom transports (ITransport interface) |
| [Exceptions](docs/Exceptions.md) | YamuxException, SessionException, SessionChannelException, error codes |


## License
This project is licensed under the MIT License.
