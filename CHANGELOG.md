# Changelog

All notable changes to this project will be documented in this file.

## [0.1.0] - Unreleased

### Added
- Public `Session` constructor accepting `ITransport`, `isClient`, `leaveOpen`, and `SessionOptions`
- Go interoperability test suite (`test/Yamux.Interop.Tests`) and helper (`benchmarks/go-interop`) covering both directions, FIN, GoAway, ping, multi-stream transfers, and custom windows
- Cross-platform benchmark comparison script (`benchmarks/compare.sh`)
- `ITransport.FlushAsync()` default interface method for transport-level flushing
- `ISessionChannel.FlushWritesAsync()` properly flushes to underlying transport
- `IAsyncDisposable` support on `ISessionChannel`
- `SessionOptions.SessionCloseTimeout` for configurable graceful shutdown
- `SessionOptions.MaxIncomingFrameSize` for defensive frame size validation
- Graceful session shutdown: channels drain before force-close
- `ReusableValueTaskSourcePool` integrated into `ConnectionWriter` for TCS pooling
- RST frame sent for data frames on unknown streams (Yamux spec compliance)
- Channel immediately removed from `ChannelManager` on RST receipt
- `Nerdbank.Streams` and in-memory transport benchmarks
- New test suites: `SocketTransportTests`, `StressTests`, `ProtocolEdgeCaseTests`, `ErrorPathTests`, `ProtocolRobustnessTests`, `ResilienceTests`
- .NET 11 target (`net11.0`) in addition to .NET 9 and .NET 10

### Fixed
- Frame payload discard no longer reads past the current frame (frame desync)
- Zero-length data frames no longer close the session
- FIN on a data frame no longer discards the frame payload
- Duplicate FIN is no longer attached to window updates after close
- Canceled or timed-out window waiters no longer fault the session on the next window update
- Receive window advertisements are validated against the remaining window
- Remotely initiated channels no longer send a SYN when a custom receive window is configured
- Receive window sizes below the 256 KB protocol initial value are rejected instead of underflowing the window update
- RST now completes pending `OpenChannelAsync(waitForAcknowledgement: true)` calls immediately
- Accept backlog exhaustion resets new streams instead of blocking the frame reader
- Keep-alive pings time out and close dead sessions; pending pings fail on session close
- `CloseAsync` is best-effort and idempotent when a transport throws during shutdown
- Per-session metrics unregister their instruments when the session is disposed
- Batched writes honor `ITransport.FlushAsync` for `FlushWritesAsync`
- Statistics count each frame once and count payload bytes consistently
- Auto-tune uses consistent `Stopwatch`/`TimeSpan` units
- Disposing a gracefully closed channel no longer emits a redundant RST
- `Close`/`WhenRemoteCloseAsync` tolerate concurrent channel disposal
- Stream ID generation detects exhaustion instead of wrapping
- Statistics `Sampled` handler exceptions no longer escape the timer callback
- `Session.Start` is safe against concurrent calls
- Reduced hot-path allocations: pooled write-completion sources, a reusable batch
  header buffer, no per-frame `ArrayPool` rent, and no linked CTS for
  uncancellable writes

### Changed
- **Breaking:** Renamed extension parameter `keepOpen` to `leaveOpen` (.NET convention)
- **Breaking:** `CancellationToken? cancel` changed to `CancellationToken cancellationToken = default` on all public APIs
- **Breaking:** `FlushWritesAsync` return type changed from `Task` to `ValueTask`
- Removed unused `SessionOptions.ReadTimeout`; keep-alive is the supported liveness mechanism
- `Session` constructor access changed from `internal` to `public`
- Ping response is now awaited instead of fire-and-forget
- `ConfigureAwait(false)` added throughout library internals

### Fixed
- RTT calculation now correctly uses `Stopwatch.Frequency`
- `ConnectionReader` cancellation no longer falls through to garbage parse
- Channels properly removed from `ChannelManager` on all disposal paths
- `CloseOpenChannelsAsync` handles already-disposed channels gracefully
- `EnqueueFrame` now properly returns the TCS to pool on failure

### Security
- Max incoming frame size validation prevents memory exhaustion from malicious peers