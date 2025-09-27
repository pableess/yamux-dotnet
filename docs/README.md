# Yamux .NET Library Documentation

This documentation provides an overview of the public APIs for the Yamux .NET library, which implements the Yamux multiplexing protocol for .NET applications.

## Table of Contents
- [Session](Session.md)
- [SessionChannelOptions](SessionChannelOptions.md)
- [SessionOptions](SessionOptions.md)
- [Statistics](Statistics.md)
- [ISessionChannel and Channel Interfaces](Channels.md)
- [Exceptions](Exceptions.md)

---

## Getting Started

Yamux allows you to multiplex multiple logical streams over a single network connection, stream, or custom transport. This is useful for building efficient networked applications that require multiple independent data streams over a single stream.

### Basic Setup Example

```csharp
using Yamux;
using System.Net.Sockets;

// Server side
var listener = new Socket(SocketType.Stream, ProtocolType.Tcp);
listener.Bind(new IPEndPoint(IPAddress.Loopback, 5000));
listener.Listen();
var clientSocket = await listener.AcceptAsync();
using var yamuxSession = new NetworkStream(clientSocket).AsYamuxSession(false);
yamuxSession.Start();

// Client side
var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, 5000));
using var yamuxSession = new NetworkStream(socket).AsYamuxSession(true);
yamuxSession.Start();
```

### Working with Channels

Once you have a Yamux session established, you can create and manage multiple channels over the same connection:

#### Opening and Accepting Channels

```csharp
// Opening a new channel (Client side)
var channel = await yamuxSession.OpenChannelAsync();

// Accepting incoming channels (Server side)
await foreach (var incomingChannel in yamuxSession.AcceptChannelsAsync())
{
    // Process the new channel
    _ = HandleChannelAsync(incomingChannel);
}
```

#### Reading from a Channel

```csharp
async Task HandleChannelAsync(ISessionChannel channel)
{
    try
    {
        var reader = channel.Input;
        while (true)
        {
            var result = await reader.ReadAsync();
            var buffer = result.Buffer;
            try
            {
                if (result.IsCanceled)
                    break;
                // Process the data in the buffer
                foreach (var segment in buffer)
                {
                    // Work with the data...
                }
                if (result.IsCompleted)
                    break;
            }
            finally
            {
                reader.AdvanceTo(buffer.Start, buffer.End);
            }
        }
    }
    finally
    {
        // Properly close and dispose the channel
        await channel.CloseAsync();
        channel.Dispose();
    }
}
```

#### Writing to a Channel

You can write to a channel using its `Output` pipe:

```csharp
async Task WriteToChannelAsync(ISessionChannel channel, byte[] data)
{
    var writer = channel.Output;
    await writer.WriteAsync(data);
    await writer.FlushAsync();
}
```

#### Converting a Channel to a Stream

If you prefer working with streams, you can convert a channel to a `Stream`:

```csharp
using System.IO;

// Convert the channel to a Stream
using var stream = channel.AsStream();

// Now you can use standard Stream methods
await stream.WriteAsync(buffer, 0, buffer.Length);
await stream.FlushAsync();
```

#### Closing and Disposing Channels

It is important to properly close and dispose of channels when you are done with them to free resources and signal the remote peer:

```csharp
// Gracefully close the channel
await channel.CloseAsync();

// Dispose the channel to release resources
channel.Dispose();
```

You can also use a `using` statement for automatic disposal:

```csharp
using (var channel = await yamuxSession.OpenChannelAsync())
{
    // Use the channel
    // ...
    await channel.CloseAsync();
}
```

### Channel Options

When opening a new channel, you can specify custom options:

```csharp
var options = new SessionChannelOptions
{
    WindowSize = 256 * 1024,  // 256KB window size
    ReceiveBufferSize = 64 * 1024  // 64KB receive buffer
};

var channel = await yamuxSession.OpenChannelAsync(options);
```

### Monitoring Statistics with the Sampled Event

Yamux supports tracking statistics such as transfer speeds and throughput at both the session and channel level. To enable statistics, set the `EnableStatistics` property in `SessionOptions` when creating a session:

```csharp
var sessionOptions = new SessionOptions
{
    EnableStatistics = true
};

using var yamuxSession = new NetworkStream(socket).AsYamuxSession(true, sessionOptions);
yamuxSession.Start();
```

You can subscribe to the `Sampled` event on the session or on individual channels to receive periodic updates:

```csharp
// Subscribe to session statistics
if (yamuxSession.Stats != null)
{
    yamuxSession.Stats.Sampled += (sender, args) =>
    {
        Console.WriteLine($"[Session] Bytes sent: {yamuxSession.Stats.TotalBytesSent}, Bytes received: {yamuxSession.Stats.TotalBytesReceived}");
        Console.WriteLine($"[Session] Send rate: {yamuxSession.Stats.SendRate}/sec, Receive rate: {yamuxSession.Stats.ReceiveRate}/sec");
    };
}

// Subscribe to channel statistics after opening a channel
var channel = await yamuxSession.OpenChannelAsync();
if (channel.Stats != null)
{
    channel.Stats.Sampled += (sender, args) =>
    {
        Console.WriteLine($"[Channel] Bytes sent: {channel.Stats.TotalBytesSent}, Bytes received: {channel.Stats.TotalBytesReceived}");
        Console.WriteLine($"[Channel] Send rate: {channel.Stats.SendRate}/sec, Receive rate: {channel.Stats.ReceiveRate}/sec");
    };
}
```

This allows you to monitor transfer speeds and throughput in real time for both the session and each channel.

See the individual API documentation for more details.
