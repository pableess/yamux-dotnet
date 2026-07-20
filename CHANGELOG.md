# Changelog

All notable changes to this project will be documented in this file.

## [0.1.0] - Unreleased

### Added
- Public `Session` constructor accepting `ITransport`, `isClient`, `leaveOpen`, and `SessionOptions`
- `ITransport.FlushAsync()` default interface method for transport-level flushing
- `ISessionChannel.FlushWritesAsync()` properly flushes to underlying transport
- `IAsyncDisposable` support on `ISessionChannel`
- `SessionOptions.SessionCloseTimeout` for configurable graceful shutdown
- `SessionOptions.MaxIncomingFrameSize` for defensive frame size validation
- `SessionOptions.ReadTimeout` for transport read timeout
- Graceful session shutdown: channels drain before force-close
- `ReusableValueTaskSourcePool` integrated into `ConnectionWriter` for TCS pooling
- RST frame sent for data frames on unknown streams (Yamux spec compliance)
- Channel immediately removed from `ChannelManager` on RST receipt
- `Nerdbank.Streams` and in-memory transport benchmarks
- New test suites: `SocketTransportTests`, `StressTests`, `ProtocolEdgeCaseTests`, `ErrorPathTests`

### Changed
- **Breaking:** Renamed extension parameter `keepOpen` to `leaveOpen` (.NET convention)
- **Breaking:** `CancellationToken? cancel` changed to `CancellationToken cancellationToken = default` on all public APIs
- **Breaking:** `FlushWritesAsync` return type changed from `Task` to `ValueTask`
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