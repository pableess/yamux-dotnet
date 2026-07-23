---
title: Home
---

# Yamux .NET Library

A high-performance .NET implementation of the [HashiCorp Yamux multiplexing protocol](https://github.com/hashicorp/yamux). Yamux enables multiple reliable, ordered, independent streams (channels) over a **single** underlying connection.

```bash
dotnet add package Yamux
```

<div class="row row-cols-1 row-cols-md-2 row-cols-lg-3 g-4 my-4">

<div class="col">
<div class="card h-100">
<div class="card-body">
<h5 class="card-title"><i class="bi bi-rocket-takeoff"></i> Getting Started</h5>
<p class="card-text">Setup guide for server and client using Yamux sessions and channels.</p>
<a href="getting-started/" class="stretched-link"></a>
</div>
</div>
</div>

<div class="col">
<div class="card h-100">
<div class="card-body">
<h5 class="card-title"><i class="bi bi-diagram-3"></i> Session</h5>
<p class="card-text">Session lifecycle, channel management, ping, and graceful shutdown.</p>
<a href="Session/" class="stretched-link"></a>
</div>
</div>
</div>

<div class="col">
<div class="card h-100">
<div class="card-body">
<h5 class="card-title"><i class="bi bi-intersect"></i> Channels</h5>
<p class="card-text">Using <code>IDuplexSessionChannel</code>, <code>IReadOnlySessionChannel</code>, and <code>IWriteOnlySessionChannel</code>.</p>
<a href="Channels/" class="stretched-link"></a>
</div>
</div>
</div>

<div class="col">
<div class="card h-100">
<div class="card-body">
<h5 class="card-title"><i class="bi bi-gear"></i> SessionOptions</h5>
<p class="card-text">Configuration for keep-alive, max channels, statistics, and metrics.</p>
<a href="SessionOptions/" class="stretched-link"></a>
</div>
</div>
</div>

<div class="col">
<div class="card h-100">
<div class="card-body">
<h5 class="card-title"><i class="bi bi-sliders"></i> SessionChannelOptions</h5>
<p class="card-text">Flow control, receive window sizing, and auto-tuning configuration.</p>
<a href="SessionChannelOptions/" class="stretched-link"></a>
</div>
</div>
</div>

<div class="col">
<div class="card h-100">
<div class="card-body">
<h5 class="card-title"><i class="bi bi-bar-chart"></i> Statistics</h5>
<p class="card-text">Per-session and per-channel bandwidth tracking with sampled events.</p>
<a href="Statistics/" class="stretched-link"></a>
</div>
</div>
</div>

<div class="col">
<div class="card h-100">
<div class="card-body">
<h5 class="card-title"><i class="bi bi-hdd-network"></i> Transport</h5>
<p class="card-text">Pluggable transports: <code>Stream</code>, <code>Socket</code>, <code>IDuplexPipe</code>, or custom <code>ITransport</code>.</p>
<a href="Transport/" class="stretched-link"></a>
</div>
</div>
</div>

<div class="col">
<div class="card h-100">
<div class="card-body">
<h5 class="card-title"><i class="bi bi-exclamation-triangle"></i> Exceptions</h5>
<p class="card-text">SessionException, SessionChannelException, and protocol error handling.</p>
<a href="Exceptions/" class="stretched-link"></a>
</div>
</div>
</div>

<div class="col">
<div class="card h-100">
<div class="card-body">
<h5 class="card-title"><i class="bi bi-speedometer2"></i> Performance</h5>
<p class="card-text">Benchmarks comparing Yamux .NET vs Go Yamux and Nerdbank.Streams.</p>
<a href="Performance/" class="stretched-link"></a>
</div>
</div>
</div>

</div>

## Features

- **Full-duplex multiplexing** &mdash; hundreds of logical streams over a single connection
- **Channel-based abstraction** &mdash; `IDuplexSessionChannel`, `IReadOnlySessionChannel`, `IWriteOnlySessionChannel`
- **Configurable flow control** &mdash; automatic window tuning based on RTT
- **Keep-alive and RTT** &mdash; dead connection detection and round-trip measurement
- **Bandwidth statistics** &mdash; per-session and per-channel tracking with `Sampled` events
- **OpenTelemetry metrics** &mdash; `System.Diagnostics.Metrics` integration
- **High performance** &mdash; built on `System.IO.Pipelines`, pooled value task sources, lock-free ID generation
- **AOT compatible** &mdash; supports Native AOT publish
- **Graceful shutdown** &mdash; channel drain with configurable timeouts
- **Pluggable transports** &mdash; `Stream`, `Socket`, `IDuplexPipe`, or custom `ITransport`

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
