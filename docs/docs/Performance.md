---
title: Performance
---

# Performance

## Benchmark Results

Benchmarks compare Yamux against raw TCP, the reference Go implementation, and Nerdbank.Streams.

### Methodology

- 32KB chunk size for all tests
- Parameters: 1/50/500 MB total data, 1/5/20 concurrent streams
- TCP benchmarks use loopback (127.0.0.1)
- In-memory benchmarks use `System.IO.Pipelines`
- Go comparison uses the HashiCorp Yamux reference implementation (v0.1.2)
- Nerdbank.Streams uses protocol version 1

### Expected Results

Benchmarks should show:

1. **SocketBaselineAsync** — Raw TCP throughput (theoretical maximum)
2. **YamuxStreamAsync** — .NET Yamux over TCP with multi-stream parallelism
3. **GoIntegrationAsync** — C# Yamux client vs Go Yamux server (interop benchmark)
4. **NerdbankStreamsAsync** — Nerdbank.Streams over in-memory streams
5. **YamuxInMemoryAsync** — Yamux over in-memory pipes (theoretical max)

### Running Benchmarks

```bash
dotnet run -c Release --project benchmarks/Yamux.Benchmark
```

To compare directly with Go on Linux or macOS:

```bash
bash benchmarks/compare.sh
```

The script builds `benchmarks/go-server` and `benchmarks/go-benchmark`, prints the Go
reference throughput, then runs the C#-to-Go benchmark.

Cross-implementation compatibility is covered by `test/Yamux.Interop.Tests`, which
exercises both directions, FIN handling, GoAway, ping, multi-stream transfers, and
custom windows against the Go reference implementation.

## Allocation Characteristics

The library is designed to minimize GC pressure:

- **`ReusableValueTaskSourcePool`** pools `TaskCompletionSource` instances used for write completion signaling
- **`System.IO.Pipelines`** provides zero-copy buffer management for reading
- **`Frame` struct** is stack-allocated; payload ownership is tracked via `IDisposable`
- **`StreamIdGenerator`** uses `Interlocked` for lock-free ID generation
- **`RemoteDataWindow`** uses async waiters for flow control and releases them on cancellation without consuming window credit

## Throughput Tuning

- **`MaxDataFrameSize`** — Larger frames reduce per-frame overhead but increase latency. Default 16KB.
- **`ReceiveWindowSize`** — Larger windows improve throughput for high-BDP connections. Default 256KB.
- **`AutoTuneReceiveWindowSize`** — Automatically scales window up to `ReceiveWindowUpperBound` based on RTT.
- **`ReceiveWindowUpperBound`** — Caps auto-tuning. Default 16MB.