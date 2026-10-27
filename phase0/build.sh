#!/usr/bin/env bash
# Linux/macOS/Windows(Git Bash) から win-x64 自己完結単一exeをビルドする
set -euo pipefail
cd "$(dirname "$0")/src"
dotnet publish -c Release -o ../bin
