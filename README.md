# Yamux.NET

Yamux.NET is a .NET 9 library implementing the [Yamux multiplexing protocol](https://github.com/hashicorp/yamux/blob/master/spec.md), enabling multiple reliable, ordered, and independent streams (channels) over a single underlying connection (such as TCP). This is useful for building high-performance network applications, tunneling, or protocols that require multiplexed communication.

## Features
- Full-duplex, multiplexed streams over a single connection
- Channel-based abstraction (`SessionChannel`, `IDuplexSessionChannel`)
- Configurable flow control and window sizing
- Automatic window tuning for optimal throughput
- Keep-alive and round-trip time (RTT) measurement
- Bandwidth and statistics tracking
- .NET 9, async/await friendly

## Getting Started

### Install
Add a reference to the `Yamux` project or build from source for .NET 9.

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

## License
This project is licensed under the MIT License.
