# Changelog

All notable changes to this project will be documented in this file.

## [0.1.0-rc1] - 2026-11-24

### Added

- Public `Session` constructor accepting `ITransport`, `isClient`, `leaveOpen`, and `SessionOptions`
- .NET 11 target (`net11.0`) in addition to .NET 9 and .NET 10
- Go interoperability suite (`test/Yamux.Interop.Tests`) and helper (`benchmarks/go-interop`) covering both directions, FIN/RST/GoAway handling, ping, multi-stream transfers, and custom windows against HashiCorp yamux v0.1.2
- C#-to-C# benchmark (`CsharpToCsharpAsync`) mirroring the C#-to-Go direction, alongside the existing TCP and in-memory C# benchmarks
- Cross-platform comparison scripts (`benchmarks/compare.sh`, rewritten `benchmarks/compare.ps1`)
- `ITransport.FlushAsync()` default interface method
- `ITransport.SupportsBatching` and sequence-based `WriteAsync(ReadOnlySequence<byte>, ...)`
- `ISessionChannel.FlushWritesAsync()` with transport flush barriers in batched mode
- `IAsyncDisposable` support on `ISessionChannel`
- `SessionOptions.SessionCloseTimeout`, `MaxIncomingFrameSize`, `WriteSegmentBatchingEnabled`, and `MinWriteBatchSize`
- RST frame sent for data frames on unknown streams (yamux spec compliance)
- Channel removed from `ChannelManager` immediately on RST
- New test suites: `SocketTransportTests`, `StressTests`, `ProtocolEdgeCaseTests`, `ErrorPathTests`, `ProtocolRobustnessTests`, `ResilienceTests`
- Nerdbank.Streams and in-memory transport benchmarks

### Changed

- **Breaking:** Renamed extension parameter `keepOpen` to `leaveOpen` (.NET convention)
- **Breaking:** `CancellationToken? cancel` changed to `CancellationToken cancellationToken = default` on public APIs
- **Breaking:** `FlushWritesAsync` return type changed from `Task` to `ValueTask`
- **Breaking:** `ReceiveWindowSize`/`ReceiveWindowUpperBound` below the 256 KB protocol initial window are rejected
- **Breaking:** Removed unused `SessionOptions.ReadTimeout`; keep-alive is the supported liveness mechanism
- `Session` constructor access changed from `internal` to `public`
- `Session.Start()` is idempotent and safe to call concurrently
- `CloseAsync()` force-closes channels; `CloseOpenChannelsAsync()` sends GoAway and drains channels
- `SessionOptions.SessionCloseTimeout` is now used during shutdown
- Ping responses are awaited, and pings time out after `ConnectionWriteTimeout`
- Metrics use a per-session `Meter`; instruments are unregistered when the session is disposed
- `ConfigureAwait(false)` is used throughout library internals

### Fixed

- Frame payload discard no longer reads past the current frame (frame desync)
- Zero-length data frames no longer close the session
- FIN on a data frame no longer discards the frame payload
- Duplicate FIN is no longer attached to window updates after close
- Canceled or timed-out window waiters no longer fault the session on the next window update
- Receive-window advertisements are validated against the remaining window
- Remotely initiated channels no longer send a SYN when a custom receive window is configured
- Receive-window uint underflow no longer produces a ~4 GiB window update
- RST now completes pending `OpenChannelAsync(waitForAcknowledgement: true)` calls immediately
- `OpenChannelAsync` waits for the SYN write, so data frames cannot overtake stream setup
- Concurrent `AcceptAsync` calls are safe (the accept queue supports multiple readers)
- Accept backlog exhaustion resets new streams instead of blocking the frame reader
- Keep-alive pings close dead sessions; pending pings fail on session close
- `CloseAsync` is best-effort and idempotent when a transport throws during shutdown
- Batched writes honor `ITransport.FlushAsync` for `FlushWritesAsync`
- Statistics count each frame once and count payload bytes consistently
- Auto-tune uses consistent `Stopwatch`/`TimeSpan` units
- Disposing a gracefully closed channel no longer emits a redundant RST
- `Close`, `WhenRemoteCloseAsync`, and `WhenRemoteAckAsync` tolerate concurrent channel disposal
- Stream ID generation detects exhaustion instead of wrapping and reports `SessionErrorCode.StreamsExhausted`
- `Statistics.Sampled` handler exceptions no longer escape the timer callback
- Reduced hot-path allocations: pooled write-completion sources, a reusable batch
  header buffer, no per-frame `ArrayPool` rent, and no linked CTS for
  uncancellable writes
- Control frames (RST/GoAway) no longer block the frame reader or leave unobserved write faults
- RTT calculation uses `Stopwatch.Frequency`
- `ConnectionReader` cancellation no longer falls through to a garbage parse
- Channels are removed from `ChannelManager` on all disposal paths
- `CloseOpenChannelsAsync` handles already-disposed channels gracefully

### Security

- Maximum incoming frame size validation limits memory exhaustion from malicious peers
- Receive windows are enforced against actual advertised credit
- Accept-backlog and `MaxChannels` limits reset excess streams instead of blocking or over-allocating
