---
title: Getting Started
---

# Getting Started with Yamux

Yamux allows you to multiplex multiple logical streams over a single network connection, stream, or custom transport. This is useful for building efficient networked applications that require multiple independent data streams over a single connection.

## Basic Setup

### Server Side

```csharp
using System.Net;
using System.Net.Sockets;
using Yamux;

var listener = new Socket(SocketType.Stream, ProtocolType.Tcp);
listener.Bind(new IPEndPoint(IPAddress.Loopback, 5000));
listener.Listen();
var clientSocket = await listener.AcceptAsync();

using var yamuxSession = new NetworkStream(clientSocket).AsYamuxSession(false);
yamuxSession.Start();
```

### Client Side

```csharp
using System.Net;
using System.Net.Sockets;
using Yamux;

var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, 5000));

using var yamuxSession = new NetworkStream(socket).AsYamuxSession(true);
yamuxSession.Start();
```

## Working with Channels

Once you have a Yamux session established, you can create and manage multiple channels over the same connection.

### Opening and Accepting Channels

```csharp
// Opening a new channel (Client side)
var channel = await yamuxSession.OpenChannelAsync();

// Accepting incoming channels (Server side)
var channel = await yamuxSession.AcceptAsync();
```

### Reading from a Channel

You can read data from the channel using its `Input` pipe:

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
        channel.Close();
        channel.Dispose();
    }
}
```

### Writing to a Channel

```csharp
await channel.WriteAsync(myData);
```

### Converting a Channel to a Stream

If you prefer working with streams, you can convert a channel to a `Stream`:

```csharp
using var stream = channel.AsStream();
await stream.WriteAsync(buffer, 0, buffer.Length);
await stream.FlushAsync();
```

### Closing and Disposing Channels

It is important to properly close and dispose of channels when you are done with them:

```csharp
// Gracefully close the channel (write side)
channel.Close();

// Wait for the remote peer to acknowledge the close
await channel.WhenRemoteCloseAsync(TimeSpan.FromSeconds(5));

// Dispose the channel to release resources
channel.Dispose();
```

You can also use a `using` statement for automatic disposal:

```csharp
using (var channel = await yamuxSession.OpenChannelAsync())
{
    // Use the channel
    channel.Close();
}
```

## Channel Options

When opening a new channel, you can specify custom options:

```csharp
var options = new SessionChannelOptions
{
    ReceiveWindowSize = 256 * 1024,         // 256KB window size
    ReceiveWindowUpperBound = 4 * 1024 * 1024 // 4MB receive window upper bound
};

var channel = await yamuxSession.OpenChannelAsync(options);
```

## Monitoring Statistics

Enable statistics on the session:

```csharp
var sessionOptions = new SessionOptions
{
    EnableStatistics = true
};

using var yamuxSession = new NetworkStream(socket).AsYamuxSession(true, sessionOptions);
yamuxSession.Start();

// Subscribe to session statistics
if (yamuxSession.Stats != null)
{
    yamuxSession.Stats.Sampled += (sender, args) =>
    {
        Console.WriteLine($"Send rate: {yamuxSession.Stats.SendRate}/sec, Receive rate: {yamuxSession.Stats.ReceiveRate}/sec");
    };
}
```