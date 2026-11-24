param([string]$Tfm = "net10.0")

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$bin = Join-Path $root "bin"
New-Item -ItemType Directory -Force -Path $bin | Out-Null

Write-Host "=== Building Go helpers ===" -ForegroundColor Cyan
& (Join-Path $root "build-go-server.ps1")

Push-Location (Join-Path $root "go-benchmark")
try {
    go mod tidy
    go build -o (Join-Path $bin "go-benchmark.exe") .
} finally {
    Pop-Location
}

Write-Host ""
Write-Host "=== Go yamux (Go client -> Go server) ===" -ForegroundColor Cyan
& (Join-Path $bin "go-benchmark.exe")

$project = Join-Path $root "Yamux.Benchmark\Yamux.Benchmark.csproj"

Write-Host ""
Write-Host "=== C# yamux (C# client -> C# server, TCP) ===" -ForegroundColor Cyan
dotnet run -c Release --framework $Tfm --project $project -- --filter '*CsharpToCsharp*'

Write-Host ""
Write-Host "=== C# yamux (C# client -> Go server) ===" -ForegroundColor Cyan
dotnet run -c Release --framework $Tfm --project $project -- --filter '*GoIntegration*'

Write-Host ""
Write-Host "Other C#-to-C# benchmarks: TcpYamuxAsync (server-initiated channels) and MemoryYamuxAsync (in-memory pipes)." -ForegroundColor Gray
Write-Host "Use BenchmarkDotNet's tables for the C# results and the Go table for the Go baseline." -ForegroundColor Gray
