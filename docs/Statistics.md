# Statistics

The `Statistics` class tracks bandwidth and byte statistics for a Yamux session.

## Properties
- `ulong TotalBytesSent` — Total bytes sent.
- `ulong TotalBytesReceived` — Total bytes received.
- `ByteSize SendRate` — Current send bandwidth (bytes/sec).
- `ByteSize ReceiveRate` — Current receive bandwidth (bytes/sec).
- `TimeSpan SampleInterval` — Sampling interval.

## Events
- `EventHandler? Sampled` — Raised when a new sample is taken.

## Methods
- `Statistics(int intervalMilliseconds, CancellationToken cancel)` — Constructor.
- `void UpdateSent(ulong bytesSent)` — Updates sent bytes.
- `void UpdateReceived(ulong bytesReceived)` — Updates received bytes.
- `void Dispose()` — Disposes the statistics tracker.

## Example
```csharp
var stats = new Statistics(1000, CancellationToken.None);
stats.UpdateSent(1024);
stats.UpdateReceived(2048);
```
