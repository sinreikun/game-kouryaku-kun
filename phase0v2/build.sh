#!/usr/bin/env bash
# win-x64 自己完結・トリミング済み・圧縮単一exeをビルドする（Linux/macOS/Windows Git Bash）
set -euo pipefail
cd "$(dirname "$0")/src"
dotnet publish -c Release -o ../bin
