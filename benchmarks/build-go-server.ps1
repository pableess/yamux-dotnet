param([switch]$Force)

$ErrorActionPreference = "Stop"
$targetDir = Join-Path $PSScriptRoot "bin"
if (!(Test-Path $targetDir)) { New-Item -ItemType Directory -Path $targetDir -Force | Out-Null }

$serverExe = Join-Path $targetDir "go-server.exe"
if (!(Test-Path $serverExe) -or $Force) {
	Push-Location (Join-Path $PSScriptRoot "go-server")
	try {
		go mod tidy
		go build -o $serverExe .
		Write-Host "Built: $serverExe"
	} finally {
		Pop-Location
	}
} else {
	Write-Host "Already exists: $serverExe (use -Force to rebuild)"
}