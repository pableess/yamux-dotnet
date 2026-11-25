---
title: Performance
---

# Performance

## Benchmark comparison

These results compare this implementation with HashiCorp yamux (Go) and
Nerdbank.Streams under one workload, one direction, and matched flow-control
settings. They are loopback measurements: they show framing, flow-control,
copying, and syscall costs, not wide-area network behavior.

Measured 2026-11-24 on:

- AMD Ryzen 5 5600H, 6 physical / 12 logical cores, Fedora Linux 44
- .NET 10.0.13 (X64 RyuJIT AVX2), BenchmarkDotNet 0.14.0
- Go 1.26.11, HashiCorp yamux v0.1.2
- Nerdbank.Streams 2.13.16
- Loopback TCP (`127.0.0.1`), no TLS

### Workload and topology

- 1, 50, and 500 MB total payload per operation, spread across 1, 5, or 20
  concurrent channels
- 32 KB application writes
- 32 KB yamux frames (`MaxDataFrameSize = 32 KB`); Go writes 32 KB at a time
- 256 KB receive window, keep-alive off, receive-window auto-tuning off,
  Nagle off
- The client opens the channels and writes; the server drains them. The client
  waits for the remote FIN/close before the operation completes, so the
  measurement is end-to-end
- External peers are pre-warmed and reused across invocations; process startup
  and JIT are outside the measured method
- BenchmarkDotNet: throughput strategy, 3 warmups, 8 iterations, 1 launch

| Benchmark | Topology |
|---|---|
| `RawTcpAsync` | Raw loopback TCP, one byte stream, both peers in-process |
| `YamuxInProcAsync` | C# client → C# server, both peers in-process |
| `YamuxSplitAsync` | C# client → pre-warmed C# server process |
| `YamuxToGoAsync` | C# client → pre-warmed Go server process |
| `MemoryYamuxAsync` | C# ↔ C# over in-memory `System.IO.Pipelines` |
| `TcpNerdbankSplitAsync` | Nerdbank client → pre-warmed Nerdbank server process |
| `MemoryNerdbankAsync` | Nerdbank over an in-memory full-duplex stream pair |
| `GoYamux` | Go client → Go server, both peers in-process (`benchmarks/go-benchmark`) |

Compare **within a topology group**: the in-process pairs share one process
(and GC/thread pool), while the split pairs run one peer in the benchmark
process and one in a separate pre-warmed process. The raw TCP baseline is
in-process.

Nerdbank.Streams is run three ways:

- **v1**: the default protocol. It predates backpressure and ignores the
  per-channel receiving window.
- **v3, 256 KB window**: backpressure enabled, with the same window as yamux.
- **v3, 1 MB window**: backpressure enabled with Nerdbank's default window.

Nerdbank's frame payload limit is 20 KB, so a 32 KB application write is sent
as a 20 KB and a 12 KB frame. yamux and Go send one 32 KB frame per write.

### 500 MB throughput

MB/s; 1 MB = 1024² bytes. Higher is better.

| Streams | Raw TCP | C# yamux in-proc | C# yamux split | C# → Go | Go ↔ Go | Nerdbank v1 | Nerdbank v3 256 KB | Nerdbank v3 1 MB |
|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| 1 | 2,530 | 1,336 | 1,227 | 1,313 | 921 | 1,272 | 521 | 536 |
| 5 | 2,508 | 1,380 | 1,330 | 1,519 | 1,135 | 1,097 | 511 | 527 |
| 20 | 2,511 | 1,438 | 1,465 | 1,913 | 1,232 | 1,020 | 536 | 531 |

At 50 MB the picture is similar: C# yamux split is 1,217 / 1,358 / 1,689 MB/s
for 1 / 5 / 20 streams, C# → Go is 1,323 / 1,502 / 1,685 MB/s, and Go ↔ Go is
811 / 1,109 / 1,265 MB/s. The 1 MB results are dominated by connection and
session setup; the suite includes them for trend, not bandwidth.

### Receive path

Frame payloads are read directly into each channel's `System.IO.Pipelines`
buffer. The reader requests up to 64 KB per socket read, so a typical 32 KB
yamux frame is read with one receive call instead of being split across eight
4 KB pipe segments. The payload is then handed to the channel's pipe without
another copy.

At 500 MB / 20 streams, the C# server (split topology) reaches 1,465 MB/s
versus 1,913 MB/s for the Go server with the same C# client. At 1 and 5
streams the split numbers are within 6% and 12%, so the remaining gap is
concentrated at high concurrency, consistent with per-stream dispatch and
wake-up costs.

### Allocation per 500 MB

Managed allocation per operation in the benchmark process. For split
benchmarks only the client is measured; for in-process and memory benchmarks
both peers are included. Lower is better, but compare allocation together
with throughput.

| Implementation | 1 stream | 5 streams | 20 streams |
|---|---:|---:|---:|
| C# yamux split (client only) | 16.2 MB | 15.1 MB | 15.2 MB |
| C# → Go (client only) | 16.6 MB | 14.9 MB | 16.3 MB |
| C# yamux in-proc (both peers) | 23.7 MB | 22.1 MB | 22.5 MB |
| In-memory yamux (both peers) | 20.7 MB | 21.2 MB | 21.5 MB |
| Nerdbank v1 split (client only) | 1.4 MB | 7.2 MB | 7.5 MB |
| Nerdbank v3 256 KB split (client only) | 10.6 MB | 12.6 MB | 16.4 MB |
| Nerdbank v3 1 MB split (client only) | 11.3 MB | 15.8 MB | 16.6 MB |

The yamux client allocates roughly 3% of the payload. Nerdbank v1 allocates
much less because it has no per-channel window bookkeeping; Nerdbank v3 is in
the same range as yamux.

### Interpretation

- **This implementation is competitive with Go yamux.** In the matched
  in-process topology, C# yamux is 17–19% faster at 500 MB and 30–60% faster
  at 50 MB. The C# send path also drives C# → Go to 1.3–1.9 GB/s, the fastest
  multiplexed TCP result.
- **The receive path is fast but still has headroom at high concurrency.**
  C# yamux reaches 1,227–1,465 MB/s in the split topology. With the same C#
  client, the Go server reaches 1,313–1,913 MB/s; the gap is 6% at 1 stream,
  12% at 5, and ~25% at 20, consistent with per-stream dispatch and wake-up
  cost rather than copy or syscall cost.
- **Nerdbank v3's cost is flow-control processing, not window size.** Going
  from 256 KB to its 1 MB default changes throughput by less than 3%
  (511–536 MB/s). That is about 36–43% of C# yamux split with the same
  backpressure budget. If you need per-channel backpressure, yamux is
  substantially faster in this workload.
- **Nerdbank v1 is fast but has no per-channel backpressure.** At 1 stream it
  matches C# yamux split (1,272 vs 1,227 MB/s), but it falls behind as
  concurrency grows (1,020 vs 1,465 MB/s at 20 streams) and a slow consumer
  cannot push back on a single channel. It is a throughput/memory trade-off,
  not a like-for-like flow-controlled comparison.
- **In-memory yamux is 6.0–6.4 GB/s**, about 2.0–2.3× Nerdbank v1 and 3.3–3.8×
  Nerdbank v3. The Nerdbank in-memory benchmark uses `FullDuplexStream` and
  v3 allocates ~309–317 MB per 500 MB, so treat that comparison as
  transport-level rather than purely protocol-level. Use the TCP results for
  the fair head-to-head.
- **Raw loopback TCP is ~2.5 GB/s.** C# yamux split reaches 49–58% of raw,
  C# → Go 52–76%, Go ↔ Go 36–49%, Nerdbank v1 41–50%, and Nerdbank v3 20–21%.
  Multiplexing and flow control are not free; this implementation pays the
  least of the measured multiplexers.

### Limitations

- Single machine, loopback, one run. Run the suite multiple times before
  quoting absolute numbers; treat differences under ~10% as noise.
- Results are a snapshot of the implementation and environment above; rerun
  the suite after significant changes before comparing absolute numbers.
- Both peers are warm. Cold-start and TLS handshake behavior are not measured.
- Every reader drains as fast as possible. This does not exercise slow-consumer
  backpressure, head-of-line blocking, or memory growth under a stalled
  channel, which is where per-channel windows matter most.
- The raw TCP baseline has no multiplexing and is in-process; it is a ceiling
  for context, not a matched peer.
- 1 MB operations are setup-dominated. Use them for trend, not bandwidth.

## Methodology

- 32 KB application write size.
- Parameters: 1 / 50 / 500 MB total data and 1 / 5 / 20 concurrent streams.
- TCP benchmarks use loopback (`127.0.0.1`).
- In-memory benchmarks use `System.IO.Pipelines` (`MemoryYamuxAsync`) or
  `FullDuplexStream` (`MemoryNerdbankAsync`).
- Go reference implementation: HashiCorp yamux v0.1.2.
- `benchmarks/go-benchmark` uses the same total payload, 32 KB writes, a
  256 KB stream window, and keep-alive disabled. It waits for the server to
  drain every stream before stopping the timer.
- The C# yamux benchmarks set `MaxDataFrameSize = 32 KB`,
  `ReceiveWindowSize = ReceiveWindowUpperBound = 256 KB`, disable keep-alive
  and receive-window auto-tuning, and set `NoDelay` on every socket.
- Nerdbank.Streams uses protocol v1 (no backpressure) and v3 with 256 KB and
  1 MB windows. `ChannelReceivingWindowSize` is set on both the offer and the
  accept.
- The Go baseline and the C#-to-Go/interop server run as separate binaries.
  `benchmarks/Yamux.Benchmark.Server` is the long-lived C# peer for split
  C#-to-C# and Nerdbank runs, so all split pairs have the same topology.

### Interpreting results

- Compare in-process pairs with in-process pairs, and split pairs with split
  pairs.
- The Go baseline runs both peers in one process. `YamuxInProcAsync` does the
  same; that is the language/runtime comparison.
- `YamuxSplitAsync` and `YamuxToGoAsync` use the same C# client and differ only
  in the server implementation. That is the server receive-path comparison.
- `TcpNerdbankSplitAsync` uses the same split topology as `YamuxSplitAsync`.
- Use multiple runs and compare trends, not single absolute numbers.

## Running

```bash
dotnet run -c Release --project benchmarks/Yamux.Benchmark
```

Run the full comparison (Go baseline, raw TCP, C#↔C#, C#→Go,
Nerdbank) on Linux or macOS:

```bash
bash benchmarks/compare.sh
```

Select a target framework:

```bash
TFM=net11.0 bash benchmarks/compare.sh
```

On Windows:

```powershell
pwsh benchmarks/compare.ps1 -Tfm net11.0
```

The script builds `benchmarks/go-server`, `benchmarks/go-benchmark`, and
`benchmarks/Yamux.Benchmark.Server`, prints the Go baseline, then runs the
`YamuxBenchmarks` and `NerdbankBenchmarks` BenchmarkDotNet filters.
BenchmarkDotNet writes reports to `BenchmarkDotNet.Artifacts/results`.

## Allocation characteristics

- Receive path uses `System.IO.Pipelines` for low-allocation buffering.
- Payload reads request up to 64 KB from the channel pipe, so a normal frame is
  read with one socket receive rather than the pipe's 4 KB default segment
  size.
- `SessionFrameWriter` pools `ResettableValueTaskSource` instances for frame
  completion and reuses one growable header buffer per batch. There is no
  per-frame `ArrayPool` rent for headers.
- `SessionChannel.WriteAsync` avoids a linked `CancellationTokenSource` when the
  caller's token cannot be canceled.
- `Frame` is a value type; payload ownership is tracked via `IDisposable` for
  transports that rent buffers.
- `StreamIdGenerator` uses `Interlocked` and detects ID-space exhaustion.
- `RemoteDataWindow` releases canceled/timed-out waiters without consuming window
  credit.

## Throughput tuning

- **`MaxDataFrameSize`** — Larger frames reduce per-frame overhead but increase
  latency. Default 16 KB; the comparison uses 32 KB.
- **`ReceiveWindowSize`** — Larger initial windows improve throughput for
  high-bandwidth-delay connections. Minimum/protocol initial value is 256 KB.
- **`ReceiveWindowUpperBound`** — Caps RTT-based auto-tuning. Default 16 MiB.
- **`AutoTuneReceiveWindowSize`** — Grows/shrinks the window using RTT and
  consumption rate. Disable it for a fixed window.
- **`WriteSegmentBatchingEnabled` / `MinWriteBatchSize`** — Enable scatter/gather
  writes for transports that support them; tune the batch threshold for latency
  vs. syscall overhead.
- **`EnableKeepAlive`** — Disable it in microbenchmarks to avoid ping traffic.
- **`EnableMetrics`** — Metrics are on by default and add per-frame counter
  updates. Disable them for the lowest-overhead transport measurement.

## Interoperability tests

`test/Yamux.Interop.Tests` builds `benchmarks/go-interop` and verifies both
directions, FIN/RST/GoAway handling, ping, multi-stream transfers, and custom
windows (including a 4 MB transfer against a 1 MB Go receive window) against Go
yamux v0.1.2:

```bash
dotnet test test/Yamux.Interop.Tests
```
