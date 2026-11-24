# Contributing to Yamux

## Build

```bash
dotnet build
```

## Test

```bash
dotnet test
```

To run specific test categories:

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

The benchmark project compares Yamux against raw TCP, Go Yamux, and Nerdbank.Streams.

```bash
dotnet run -c Release --project benchmarks/Yamux.Benchmark
```

To compare against the Go implementation on Linux or macOS:

```bash
bash benchmarks/compare.sh
```

On Windows, use the PowerShell scripts:

## Project Structure

```
src/Yamux.csproj         — Main library
src/Protocol/            — Yamux wire protocol (frames, constants, enums)
src/Internal/            — Internal implementation (reader, writer, channel manager, etc.)
test/Yamux.Tests/        — xUnit test suite
benchmarks/Yamux.Benchmark/ — BenchmarkDotNet benchmarks
samples/Sample/           — Live statistics sample app
samples/FileTransfer/     — Multi-file transfer sample
docs/                    — API documentation
```

## Code Style

- Follow existing code patterns and naming conventions
- Use `ConfigureAwait(false)` in all library code
- Name CancellationToken parameters `cancellationToken`
- Prefer `ValueTask` over `Task` for hot-path async operations
- XML doc comments on all public APIs

## Pull Requests

1. Fork the repository
2. Create a feature branch
3. Make changes and add tests
4. Ensure `dotnet build` and `dotnet test` pass
5. Submit a PR with a clear description