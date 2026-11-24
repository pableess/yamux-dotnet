---
title: Getting Started
---

# Getting Started with Yamux

Yamux multiplexes independent, ordered streams (channels) over one reliable
connection. This guide shows the typical lifecycle: start a session, open/accept
channels, read and write, then close and dispose.

## Prerequisites

- .NET 9, .NET 10, or .NET 11
- `dotnet add package Yamux`

## Basic setup

A `Session` must be started before channels are opened or accepted.
`Session` implements `IAsyncDisposable`; use `await using` so the transport and
background loops are released.

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
```

## Working with channels

### Opening and accepting

```csharp
// Client: open a channel. waitForAcknowledgement waits for the peer ACK.
using var outbound = await session.OpenChannelAsync(waitForAcknowledgement: true);

// Server: accept an incoming channel. Reset channels are skipped automatically.
using var inbound = await session.AcceptAsync();
```

### Reading

Data is exposed as a `System.IO.Pipelines.PipeReader`:

```csharp
static async Task ReadChannelAsync(
    IReadOnlySessionChannel channel,
    CancellationToken cancellationToken)
{
    while (true)
    {
        var result = await channel.Input.ReadAsync(cancellationToken);

        try
        {
            if (result.IsCanceled)
            {
                break;
            }

            foreach (var segment in result.Buffer)
            {
                // Consume segment.Span ...
            }
        }
        finally
        {
            channel.Input.AdvanceTo(result.Buffer.End, result.Buffer.End);
        }

        if (result.IsCompleted)
        {
            break;
        }
    }
}
```

Consuming data sends receive-window updates to the peer. Completing the reader
signals that the application will not read further; when the receive loop next
observes completion it half-closes the channel's write side.

### Writing

```csharp
await channel.WriteAsync(myData);
```

Writes are serialized per channel and respect the peer's receive window. A write
that cannot obtain window credit within `SessionOptions.StreamSendTimeout`
throws `TimeoutException`.

### Stream conversion

```csharp
await using var stream = channel.AsStream(leaveOpen: true);
await stream.WriteAsync(buffer, cancellationToken);
await stream.FlushAsync(cancellationToken);
```

`AsStream(leaveOpen: true)` keeps the channel open when the stream is disposed.
Synchronous stream APIs (`Read`, `Write`, `Seek`, etc.) are not supported.

### Closing and disposing

```csharp
channel.Close();                              // FIN: half-close the write side
await channel.WhenRemoteCloseAsync(TimeSpan.FromSeconds(5));
channel.Dispose();                            // or rely on using/await using
```

- `Close()` is a no-op after disposal.
- `Abort()` sends RST and closes both directions immediately.
- `WhenRemoteCloseAsync` returns `false` on timeout.
- Writes after close throw `SessionChannelException` with `ChannelWriteClosed`.

## Channel options

```csharp
var options = new SessionChannelOptions
{
    ReceiveWindowSize = 512 * 1024,        // >= 256 KB
    ReceiveWindowUpperBound = 4 * 1024 * 1024,
    MaxDataFrameSize = 32 * 1024,
    AutoTuneReceiveWindowSize = true,
};

options.Validate();

using var channel = await session.OpenChannelAsync(options);
```

`ReceiveWindowSize` cannot be below 256 KB or above `ReceiveWindowUpperBound`.

## Session options

```csharp
var options = new SessionOptions
{
    EnableKeepAlive = true,
    KeepAliveInterval = TimeSpan.FromSeconds(30),
    StreamOpenTimeout = TimeSpan.FromSeconds(75),
    ConnectionWriteTimeout = TimeSpan.FromSeconds(10),
    SessionCloseTimeout = TimeSpan.FromSeconds(30),
    EnableStatistics = true,
    StatisticsSampleInterval = 1000,
    EnableMetrics = true,
};

await using var session = socket.AsYamuxSession(
    isClient: true,
    leaveOpen: false,
    options: options);
session.Start();
```

See [SessionOptions](SessionOptions.md) for every setting and its default.

## Monitoring statistics

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

Session statistics stop when the session closes. Enable
`SessionChannelOptions.EnableStatistics` for per-channel totals.

## Next steps

- [Session](Session.md) — lifecycle, GoAway, keep-alive, shutdown
- [Channels](Channels.md) — channel interfaces and semantics
- [SessionChannelOptions](SessionChannelOptions.md) — flow control
- [Transport](Transport.md) — custom transports and batching
- [Exceptions](Exceptions.md) — error codes and failure modes
