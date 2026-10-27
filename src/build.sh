#!/usr/bin/env bash
# ユニットテスト（禁止API検査を含む）→ 本体とハーネスを framework-dependent で publish → ネイティブ起動ツールのテストとクロスビルド
# → 配布zip（25MB 以下・分割なし。.NET ランタイムは同梱せず、初回起動時に起動ツールが公式配布元から取得）
# 要: .NET 10 SDK、python3、g++、mingw-w64（x86_64-w64-mingw32-g++ / windres）
set -euo pipefail
cd "$(dirname "$0")"
ROOT="$(cd .. && pwd)"
OUT="$ROOT/phase1"
NAME="GameKouryakuKun-Phase1"
LAUNCHER_NAME="ゲーム攻略くん.exe"
dotnet build GameKouryakuKun.sln -nodeReuse:false
dotnet test GameKouryakuKun.Core.Tests --no-build -nodeReuse:false
launcher/build_launcher.sh "$OUT/launcher"
rm -rf "$OUT/bin"
dotnet publish GameKouryakuKun.App -c Release -o "$OUT/bin" -nodeReuse:false
STAGE="$OUT/stage/$NAME"
rm -rf "$OUT/stage" && mkdir -p "$STAGE/profiles/torneko-remaster" "$STAGE/app"
cp -r "$OUT/bin/." "$STAGE/app/"
cp "$OUT/launcher/launcher.exe" "$STAGE/$LAUNCHER_NAME"
cp launcher/runtime-manifest.json "$STAGE/"
cp profiles/torneko-remaster/profile.json "$STAGE/profiles/torneko-remaster/"
cp README.md "$STAGE/"
{
  echo "ゲーム攻略くん 同梱・取得するサードパーティ部品"
  echo
  echo "== miniz 3.0.2（ゲーム攻略くん.exe に静的リンク。zip 展開） =="
  cat launcher/third_party/miniz/LICENSE
  echo
  echo "== .NET 8 Runtime / Windows Desktop Runtime（同梱しない。初回起動時に公式配布元から runtime\\ へ取得） =="
  echo "ライセンス: MIT（https://github.com/dotnet/runtime/blob/main/LICENSE.TXT , https://github.com/dotnet/windowsdesktop/blob/main/LICENSE.TXT）"
  echo "取得元・版・サイズ・ハッシュ: runtime-manifest.json。取得後は runtime\\LICENSE.txt と runtime\\ThirdPartyNotices.txt を参照。"
} > "$STAGE/THIRD_PARTY_NOTICES.txt"
python3 - "$STAGE" "${GKK_DIST:-$ROOT/dist}" "$NAME" <<'PY'
import hashlib, os, shutil, sys, zipfile
stage, dist, name = sys.argv[1], sys.argv[2], sys.argv[3]
shutil.copytree('.', os.path.join(stage, 'source'), ignore=shutil.ignore_patterns('bin', 'obj', '*.user', 'TestResults', 'build'))
base = os.path.dirname(stage)
shutil.rmtree(dist, ignore_errors=True); os.makedirs(dist)
zpath = os.path.join(dist, name + '.zip')
with zipfile.ZipFile(zpath, 'w', zipfile.ZIP_DEFLATED, compresslevel=9) as z:
    for d, _, fs in os.walk(stage):
        for f in sorted(fs):
            full = os.path.join(d, f)
            z.write(full, os.path.relpath(full, base))
h = hashlib.sha256(open(zpath, 'rb').read()).hexdigest(); size = os.path.getsize(zpath)
LIMIT = 25 * 1024 * 1024
if size > LIMIT: sys.exit(f'配布zipが 25MB を超えました: {size} bytes')
with open(os.path.join(dist, 'SHA256SUMS.txt'), 'w', encoding='utf-8', newline='\n') as s: s.write(f'{h}  {name}.zip  ({size} bytes)\n')
print(f'{name}.zip\t{size}\t{h}')
PY
ls -l "${GKK_DIST:-$ROOT/dist}"
