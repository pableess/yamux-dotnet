#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
mkdir -p "$root/bin"

pushd "$root/go-server" >/dev/null
go mod tidy
go build -o "$root/bin/go-server" .
popd >/dev/null

echo "Built: $root/bin/go-server"
