# Contributing to Yamux

## Prerequisites

- .NET SDK 9, 10, and 11 (the project targets all three)
- Go 1.21+ for interoperability tests and Go benchmarks (optional for the main test suite)

## Build

```bash
dotnet build
```

## Test

```bash
dotnet test
```

Run a single target framework:

```bash
dotnet test -f net11.0
```

Run specific tests:

```bash
dotnet test --filter "FullyQualifiedName~SessionTests"
dotnet test --filter "FullyQualifiedName~SocketTransportTests"
```

## Interoperability and compatibility tests

Cross-implementation tests live in `test/Yamux.Interop.Tests`. They build the Go
reference implementation (`benchmarks/go-interop`) and exercise both directions:

```bash
dotnet test test/Yamux.Interop.Tests
```

The tests are skipped when the Go toolchain is not installed. GitHub Actions
installs Go before running the test suite.

## Benchmarks

The benchmark project compares Yamux against raw TCP, Go Yamux, and
Nerdbank.Streams. It includes:

- `RawTcpAsync` — raw loopback TCP, both peers in-process
- `YamuxInProcAsync` — C# client → C# server, both peers in-process
- `YamuxSplitAsync` — C# client → pre-warmed C# server process
- `YamuxToGoAsync` — C# client → pre-warmed Go server process
- `MemoryYamuxAsync` — C#↔C# over in-memory pipes
- `TcpNerdbankSplitAsync` / `MemoryNerdbankAsync` — Nerdbank.Streams v1 (no
  backpressure), v3 with a 256 KB window (matching yamux), and v3 with its
  default 1 MB window

Split benchmarks use `benchmarks/Yamux.Benchmark.Server` as a long-lived peer,
so process startup and JIT are outside the measured method and the
C#-to-C#, C#-to-Go, and Nerdbank topologies match.

Run the full suite:

```bash
dotnet run -c Release --project benchmarks/Yamux.Benchmark
```

Compare Go↔Go, C#↔C#, and C#→Go on Linux or macOS:

```bash
bash benchmarks/compare.sh
TFM=net11.0 bash benchmarks/compare.sh
```

On Windows:

```powershell
pwsh benchmarks/compare.ps1 -Tfm net11.0
```

## Project Structure

```
src/Yamux.csproj              — Main library
src/Protocol/                 — Yamux wire protocol (frames, constants, enums)
src/Internal/                 — Internal implementation (reader, writer, channel manager, flow control)
test/Yamux.Tests/             — xUnit protocol, transport, stress, and robustness tests
test/Yamux.Interop.Tests/     — Go interoperability tests
benchmarks/Yamux.Benchmark/   — BenchmarkDotNet benchmarks
benchmarks/go-interop/        — Go peer used by the interop suite
benchmarks/go-server/         — Go server used by C#→Go benchmarks
benchmarks/go-benchmark/      — Go↔Go benchmark
samples/Sample/               — Live statistics sample app
samples/FileTransfer/         — Multi-file transfer sample
docs/docs/                    — Documentation pages
```

## Code Style

- Follow existing code patterns and naming conventions
- Use `ConfigureAwait(false)` in all library code
- Name CancellationToken parameters `cancellationToken`
- Prefer `ValueTask` over `Task` for hot-path async operations
- XML doc comments on all public APIs
- Keep README, `docs/docs`, and `CHANGELOG.md` in sync with behavior changes

## Pull Requests

1. Fork the repository
2. Create a feature branch
3. Make changes and add tests (including interop tests when wire behavior changes)
4. Ensure `dotnet build` and `dotnet test` pass for all target frameworks
5. Submit a PR with a clear description
