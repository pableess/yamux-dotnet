#!/usr/bin/env bash
# Compare the .NET yamux implementation with the Go reference implementation.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
mkdir -p "$root/bin"

"$root/build-go-server.sh"

pushd "$root/go-benchmark" >/dev/null
go mod tidy
go build -o "$root/bin/go-benchmark" .
popd >/dev/null

echo
echo "=== Go yamux (Go client -> Go server) ==="
"$root/bin/go-benchmark"

echo
echo "=== C# yamux (C# client -> C# server, TCP) ==="
TFM="${TFM:-net10.0}"
dotnet run -c Release --framework "$TFM" --project "$root/Yamux.Benchmark/Yamux.Benchmark.csproj" -- --filter '*CsharpToCsharp*'

echo
echo "=== C# yamux (C# client -> Go server) ==="
dotnet run -c Release --framework "$TFM" --project "$root/Yamux.Benchmark/Yamux.Benchmark.csproj" -- --filter '*GoIntegration*'

echo
echo "Other C#-to-C# benchmarks: TcpYamuxAsync (server-initiated channels) and MemoryYamuxAsync (in-memory pipes)."
echo "Interpret the Go baseline (separate process) and the C# runs accordingly."
