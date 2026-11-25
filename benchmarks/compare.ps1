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

Write-Host "=== Building the persistent C# benchmark server ===" -ForegroundColor Cyan
dotnet build -c Release -f $Tfm -o (Join-Path $bin "csharp-server") `
    (Join-Path $root "Yamux.Benchmark.Server\Yamux.Benchmark.Server.csproj")

Write-Host ""
Write-Host "=== Go yamux (Go client -> Go server, in-process) ===" -ForegroundColor Cyan
& (Join-Path $bin "go-benchmark.exe")

$project = Join-Path $root "Yamux.Benchmark\Yamux.Benchmark.csproj"

Write-Host ""
Write-Host "=== C# yamux (raw TCP, in-process, split C#, split Go, memory) ===" -ForegroundColor Cyan
dotnet run -c Release --framework $Tfm --project $project -- --filter '*YamuxBenchmarks*'

Write-Host ""
Write-Host "=== Nerdbank.Streams (split TCP and memory, v1 vs backpressured v3) ===" -ForegroundColor Cyan
dotnet run -c Release --framework $Tfm --project $project -- --filter '*NerdbankBenchmarks*'

Write-Host ""
Write-Host "Results are written to BenchmarkDotNet.Artifacts\results." -ForegroundColor Gray
