---
title: Performance
---

# Performance

## Benchmark suite

`benchmarks/Yamux.Benchmark` uses BenchmarkDotNet and covers C#↔C#, C#→Go,
Nerdbank.Streams, and raw TCP.

| Benchmark | Topology |
|---|---|
| `TcpBaselineAsync` | Raw TCP baseline |
| `TcpYamuxAsync` | C# server opens channels and writes → C# client reads (TCP) |
| `CsharpToCsharpAsync` | C# client opens/writes → C# server drains (TCP), matching the Go benchmark direction |
| `GoIntegrationAsync` | C# client opens/writes → Go server drains (TCP) |
| `MemoryYamuxAsync` | C# ↔ C# over in-memory `System.IO.Pipelines` |
| `TcpNerdbankAsync` | Nerdbank.Streams over TCP |
| `MemoryNerdbankAsync` | Nerdbank.Streams over in-memory streams |

The Go↔Go baseline is produced by `benchmarks/go-benchmark`:

| Benchmark | Topology |
|---|---|
| `GoRawTCP` | Raw TCP baseline |
| `GoYamux` | Go client → Go server |

## Methodology

- 32 KB write chunk size
- Parameters: 1 / 50 / 500 MB total data and 1 / 5 / 20 concurrent streams
- TCP benchmarks use loopback (`127.0.0.1`)
- In-memory benchmarks use `System.IO.Pipelines`
- Go reference implementation: HashiCorp yamux v0.1.2
- Nerdbank.Streams uses protocol version 1
- `CsharpToCsharpAsync` and `GoIntegrationAsync` set `MaxDataFrameSize` to 64 KB
  and disable keep-alive
- Benchmarks run on .NET 9, .NET 10, or .NET 11 (`--framework`)

### Interpreting results

- The Go baseline runs entirely in a separate process. The C#↔C# benchmarks run
  both peers in the benchmark process.
- `GoIntegrationAsync` starts a Go server process for each invocation; this is
  noticeable for the 1 MB cases and negligible at 500 MB.
- `CsharpToCsharpAsync` is the direct C#↔C# counterpart of `GoIntegrationAsync`.
- Use multiple runs and compare trends, not single absolute numbers.

## Running

```bash
dotnet run -c Release --project benchmarks/Yamux.Benchmark
```

Compare Go↔Go, C#↔C#, and C#→Go on Linux or macOS:

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

The script builds `benchmarks/go-server` and `benchmarks/go-benchmark`, prints the
Go baseline, then runs the C#↔C# and C#→Go BenchmarkDotNet filters.

## Allocation characteristics

- Receive path uses `System.IO.Pipelines` for low-allocation buffering.
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
  latency. Default 16 KiB.
- **`ReceiveWindowSize`** — Larger initial windows improve throughput for
  high-bandwidth-delay connections. Minimum/protocol initial value is 256 KB.
- **`ReceiveWindowUpperBound`** — Caps RTT-based auto-tuning. Default 16 MiB.
- **`AutoTuneReceiveWindowSize`** — Grows/shrinks the window using RTT and
  consumption rate. Disable it for a fixed window.
- **`WriteSegmentBatchingEnabled` / `MinWriteBatchSize`** — Enable scatter/gather
  writes for transports that support them; tune the batch threshold for latency
  vs. syscall overhead.
- **`EnableKeepAlive`** — Disable it in microbenchmarks to avoid ping traffic.

## Interoperability tests

`test/Yamux.Interop.Tests` builds `benchmarks/go-interop` and verifies both
directions, FIN/RST/GoAway handling, ping, multi-stream transfers, and custom
windows (including a 4 MB transfer against a 1 MB Go receive window) against Go
yamux v0.1.2:

```bash
dotnet test test/Yamux.Interop.Tests
```
