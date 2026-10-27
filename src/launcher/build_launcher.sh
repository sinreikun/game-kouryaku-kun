#!/usr/bin/env bash
# ランチャーの単体テスト（Linux ネイティブ）→ Windows x64 版をクロスビルド（mingw-w64、静的リンク）
# 要: g++, x86_64-w64-mingw32-g++, x86_64-w64-mingw32-windres, python3
set -euo pipefail
cd "$(dirname "$0")"
OUT="${1:-build}"
mkdir -p "$OUT"
python3 gen_manifest.py
gcc -O2 -DMINIZ_NO_ZLIB_COMPATIBLE_NAMES -w -c third_party/miniz/miniz.c -o "$OUT/miniz-host.o"
g++ -std=c++17 -O2 -Wall -Wextra core.cpp test_core.cpp "$OUT/miniz-host.o" -o "$OUT/test_core"
"$OUT/test_core"
x86_64-w64-mingw32-gcc -O2 -DMINIZ_NO_ZLIB_COMPATIBLE_NAMES -w -c third_party/miniz/miniz.c -o "$OUT/miniz-win.o"
x86_64-w64-mingw32-windres -c 65001 launcher.rc -O coff -o "$OUT/launcher-res.o"
x86_64-w64-mingw32-g++ -std=c++17 -O2 -s -Wall -Wextra -Wno-missing-field-initializers -municode -mwindows -static -static-libgcc -static-libstdc++ \
  -D_WIN32_WINNT=0x0A00 core.cpp win_main.cpp "$OUT/miniz-win.o" "$OUT/launcher-res.o" \
  -lwinhttp -lcomctl32 -lshell32 -o "$OUT/launcher.exe"
ls -l "$OUT/launcher.exe"
