#!/usr/bin/env bash
# Compare the .NET yamux implementation with the Go reference implementation
# and Nerdbank.Streams under the same workload and process topology.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
TFM="${TFM:-net10.0}"
mkdir -p "$root/bin"

"$root/build-go-server.sh"

pushd "$root/go-benchmark" >/dev/null
go mod tidy
go build -o "$root/bin/go-benchmark" .
popd >/dev/null

# Build the persistent peer used by the C#-to-C# and Nerdbank benchmarks.
# Keeping it out of process makes those runs directly comparable to C#-to-Go.
dotnet build -c Release -f "$TFM" \
    -o "$root/bin/csharp-server" \
    "$root/Yamux.Benchmark.Server/Yamux.Benchmark.Server.csproj"

echo
echo "=== Go yamux (Go client -> Go server, in-process) ==="
"$root/bin/go-benchmark"

echo
echo "=== C# yamux (raw TCP, in-process, split C#, split Go, memory) ==="
dotnet run -c Release --framework "$TFM" --project "$root/Yamux.Benchmark/Yamux.Benchmark.csproj" -- --filter '*YamuxBenchmarks*'

echo
echo "=== Nerdbank.Streams (split TCP and memory, v1 vs backpressured v3) ==="
dotnet run -c Release --framework "$TFM" --project "$root/Yamux.Benchmark/Yamux.Benchmark.csproj" -- --filter '*NerdbankBenchmarks*'

echo
echo "Results are written to BenchmarkDotNet.Artifacts/results."
