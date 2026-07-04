# Yamux.NET

> This library is not yet production ready.  Api may change prior to 1.0 release.  Please use with caution and provide feedback.
>

Yamux.NET is a .NET 9 library implementing the [Yamux multiplexing protocol](https://github.com/hashicorp/yamux/blob/master/spec.md), enabling multiple reliable, ordered, and independent streams (channels) over a single underlying connection (such as TCP). This is useful for building high-performance network applications, tunneling, or protocols that require multiplexed communication.

## Features
- Full-duplex, multiplexed streams over a single connection
- Channel-based abstraction (`SessionChannel`, `IDuplexSessionChannel`)
- Configurable flow control and window sizing
- Automatic window tuning for optimal throughput
- Keep-alive and round-trip time (RTT) measurement
- Bandwidth and statistics tracking
	- Low allocations and high-performance design (uses System.IO.Pipelines to reduce buffer copies)
- .NET 9, async/await friendly

## Getting Started

### Install
Add a reference to the `Yamux` project or build from source for .NET 9.


## NuGet

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
| [Exceptions](docs/Exceptions.md) | YamuxException, SessionException, SessionChannelException, error codes |


## License
This project is licensed under the MIT License.
