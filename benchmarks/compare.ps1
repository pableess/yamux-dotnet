param([switch]$Full)

$ErrorActionPreference = "Stop"

Write-Host "=== Building Go benchmark ===" -ForegroundColor Cyan
Push-Location (Join-Path $PSScriptRoot "go-benchmark")
try {
    go build -o (Join-Path $PSScriptRoot "bin\go-benchmark.exe") .
} finally {
    Pop-Location
}

Write-Host "=== Running Go benchmark ===" -ForegroundColor Cyan
$goResults = @{}
$goOutput = & (Join-Path $PSScriptRoot "bin\go-benchmark.exe") 2>&1
$goOutput | ForEach-Object { Write-Host $_ -ForegroundColor Gray }

# Parse Go results
$goOutput | Select-String '^(Go\w+)\s+(\d+)\s+(\d+)\s+([\d.]+[a-z]?)\s+([\d.]+)' | ForEach-Object {
    $method = $_.Matches.Groups[1].Value
    $mbs = $_.Matches.Groups[2].Value
    $streams = $_.Matches.Groups[3].Value
    $mbps = $_.Matches.Groups[5].Value
    $goResults["$method|$mbs|$streams"] = [double]$mbps
}

Write-Host "`n=== Running C# benchmark (filtered) ===" -ForegroundColor Cyan
$csResults = @{}
$tempFile = Join-Path $env:TEMP "cs-bench-out.txt"

if ($Full) {
    $csOutput = dotnet run -c Release --project (Join-Path $PSScriptRoot "..\Yamux.Benchmark") -- --filter *CsharpToGo* 2>&1
} else {
    $csOutput = dotnet run -c Release --project (Join-Path $PSScriptRoot "..\Yamux.Benchmark") -- --filter *CsharpToGo* 2>&1
}
$csOutput | Out-File $tempFile

# Parse C# results - look for Mean values
$currentMethod = ""
$currentMBs = 0
$currentStreams = 0
Get-Content $tempFile | ForEach-Object {
    if ($_ -match 'CsharpToGoAsync.*MBs:\s*(\d+).*Streams:\s*(\d+)') {
        $currentMBs = [int]$Matches[1]
        $currentStreams = [int]$Matches[2]
        $currentMethod = "CsharpToGo"
    }
    if ($_ -match 'Mean\s*=\s*([\d.]+)\s*ms') {
        $meanMs = [double]$Matches[1]
        if ($currentMethod -eq "CsharpToGo" -and $currentMBs -gt 0) {
            $totalMB = $currentMBs * $currentStreams
            $mbps = ($totalMB) / ($meanMs / 1000.0)
            $csResults["$currentMethod|$currentMBs|$currentStreams"] = [double]$mbps
        }
    }
}

Write-Host "`n============================================" -ForegroundColor Green
Write-Host "   SIDE-BY-SIDE: C# Yamux vs Go Yamux" -ForegroundColor Green
Write-Host "============================================" -ForegroundColor Green
Write-Host ""

# Find all unique parameter combinations
$allKeys = @{}
$goResults.Keys | ForEach-Object { $allKeys[$_] = $true }
$csResults.Keys | ForEach-Object { $allKeys[$_] = $true }

Write-Host ("{0,-10} {1,5} {2,8} {3,15} {4,15} {5,15}" -f "Method", "MBs", "Streams", "Go MB/s", "C# MB/s", "Ratio C#/Go")
Write-Host ("-" * 70)

$sortedKeys = $allKeys.Keys | Sort-Object
foreach ($key in $sortedKeys) {
    $parts = $key -split '\|'
    $method = $parts[0]
    $mbs = $parts[1]
    $streams = $parts[2]
    
    $goVal = $goResults[$key]
    $csVal = $csResults[$key]
    
    $goStr = if ($goVal) { "{0,13:F1}" -f $goVal } else { "        N/A" }
    $csStr = if ($csVal) { "{0,13:F1}" -f $csVal } else { "        N/A" }
    
    $ratio = ""
    if ($goVal -and $csVal -and $goVal -gt 0) {
        $ratio = "{0,13:F2}x" -f ($csVal / $goVal)
    } elseif ($goVal) {
        $ratio = "           -"
    }
    
    Write-Host ("{0,-10} {1,5} {2,8} {3,15} {4,15} {5,15}" -f $method, $mbs, $streams, $goStr, $csStr, $ratio)
}

Write-Host ""
Write-Host "Ratio > 1.0 means C# is faster. Ratio < 1.0 means Go is faster."