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

## Benchmarks

The benchmark project compares Yamux against raw TCP, Go Yamux, and Nerdbank.Streams.

```bash
dotnet run -c Release --project benchmarks/Yamux.Benchmark
```

To compare with the Go implementation, first build the Go server:

```bash
pwsh benchmarks/build-go-server.ps1
```

Then run the comparison:

```bash
pwsh benchmarks/compare.ps1
```

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