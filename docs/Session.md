# Session

The `Session` class represents a Yamux session, allowing multiple logical streams over a single connection.

## Public API

### Constructors
- `internal Session(FrameFormatterBase frameFormatter, bool isClient, SessionOptions? options = null)`

### Properties
- `TimeSpan? RTT` — Most recent round-trip time measurement.
- `Statistics? Stats` — Bandwidth and byte statistics.
- `bool IsClosed` — Indicates if the session is closed.

### Methods
- `ValueTask<IDuplexSessionChannel> OpenChannelAsync(SessionChannelOptions options, bool waitForAcknowledgement = false, CancellationToken? cancel = null)`
  - Opens a new channel. Throws `SessionException` if the session is closed or remote sent GoAway.
- `ValueTask<IDuplexSessionChannel> OpenChannelAsync(bool waitForAcknowledgement = false, CancellationToken? cancel = null)`
  - Opens a new channel with default options.
- `ValueTask<IDuplexSessionChannel> AcceptAsync(CancellationToken? cancel = null)`
  - Accepts a new channel.
- `ValueTask<IDuplexSessionChannel> AcceptAsync(SessionChannelOptions channelOptions, CancellationToken? cancel)`
  - Accepts a new channel with custom options.
- `ValueTask<IReadOnlySessionChannel> AcceptReadOnlyChannelAsync(CancellationToken? cancel)`
  - Accepts a new channel with read-only semantics.
- `Task<TimeSpan> PingAsync(CancellationToken cancellation)`
  - Sends a ping and returns the round-trip time.
- `void Start()`
  - Starts the session. Throws `SessionException` if already closed.
- `Task CloseAsync()`
  - Closes the session.
- `void Dispose()` / `ValueTask DisposeAsync()`
  - Disposes the session and resources.

### Exceptions
- `SessionException` — Thrown for protocol or session errors.
- `SessionChannelException` — Thrown for channel-specific errors.

---

See [SessionOptions](SessionOptions.md) and [SessionChannelOptions](SessionChannelOptions.md) for configuration details.