# Benchmark result archives

Generated with `bash benchmarks/compare.sh` (or the individual BenchmarkDotNet
filters) on 2026-11-24. Environment:

- AMD Ryzen 5 5600H, 6 physical / 12 logical cores, Fedora Linux 44
- .NET 10.0.13, X64 RyuJIT AVX2
- BenchmarkDotNet 0.14.0
- Go 1.26.11, HashiCorp yamux v0.1.2
- Nerdbank.Streams 2.13.16

Files:

| File | Description |
|---|---|
| `go-baseline.txt` | Go raw TCP and Go-to-Go yamux baseline, same total payload as the C# suite |
| `yamux-before-receive-fix-report.md` / `.csv` | C# yamux suite before the 64 KB payload-read change |
| `yamux-after-receive-fix-report.md` / `.csv` | C# yamux suite after the change |
| `nerdbank-report.md` / `.csv` | Nerdbank.Streams v1, v3 @ 256 KB, and v3 @ 1 MB |

`docs/docs/Performance.md` contains the summarized comparison. The reports are
kept for provenance; BenchmarkDotNet rewrites `BenchmarkDotNet.Artifacts/` on
every run.
