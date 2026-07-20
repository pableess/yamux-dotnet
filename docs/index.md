---
layout: home
title: Home
nav_order: 1
---

# Yamux .NET Library

A high-performance .NET implementation of the [HashiCorp Yamux multiplexing protocol](https://github.com/hashicorp/yamux). Yamux enables multiple reliable, ordered, independent streams (channels) over a **single** underlying connection.

## Quick Links

| Topic | Link |
|-------|------|
| Getting Started | [Getting Started](getting-started.md) |
| Session | [Session](Session.md) |
| SessionOptions | [SessionOptions](SessionOptions.md) |
| SessionChannelOptions | [SessionChannelOptions](SessionChannelOptions.md) |
| Channels | [Channels](Channels.md) |
| Statistics | [Statistics](Statistics.md) |
| Transport | [Transport](Transport.md) |
| Exceptions | [Exceptions](Exceptions.md) |
| Performance | [Performance](Performance.md) |

## Features

- **Full-duplex multiplexing** — hundreds of logical streams over a single connection
- **Channel-based abstraction** — `IDuplexSessionChannel`, `IReadOnlySessionChannel`, `IWriteOnlySessionChannel`
- **Configurable flow control** — automatic window tuning based on RTT
- **Keep-alive and RTT** — dead connection detection and round-trip measurement
- **Bandwidth statistics** — per-session and per-channel tracking with `Sampled` events
- **OpenTelemetry metrics** — `System.Diagnostics.Metrics` integration
- **High performance** — built on `System.IO.Pipelines`, pooled value task sources, lock-free ID generation
- **AOT compatible** — supports Native AOT publish
- **Graceful shutdown** — channel drain with configurable timeouts
- **Pluggable transports** — `Stream`, `Socket`, `IDuplexPipe`, or custom `ITransport`

## Quick Example

```csharp
// Server
await using var session = socket.AsYamuxSession(isClient: false);
session.Start();
var channel = await session.AcceptAsync();

// Client
await using var session = socket.AsYamuxSession(isClient: true);
session.Start();
var channel = await session.OpenChannelAsync();
```

## Installation

```bash
dotnet add package Yamux
```