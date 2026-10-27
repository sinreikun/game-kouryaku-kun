#!/usr/bin/env bash
# ユニットテスト（禁止API検査を含む）→ 本体とハーネスを framework-dependent で publish → ネイティブ起動ツールのテストとクロスビルド
# → 初回用ブートストラップ zip（起動ツール + 初期 app）と、起動ツールが取り込む app-update-<版>.zip + release/latest.json
# 要: .NET 10 SDK、python3、g++、mingw-w64（x86_64-w64-mingw32-g++ / windres）、unzip
# 公開: 続けて tools/publish_release.sh（gh）で GitHub Releases に app-update を上げ、main に latest.json を推す
set -euo pipefail
cd "$(dirname "$0")"
ROOT="$(cd .. && pwd)"
OUT="$ROOT/phase1"
NAME="GameKouryakuKun-Bootstrap"
LAUNCHER_NAME="ゲーム攻略くん.exe"
dotnet build GameKouryakuKun.sln -nodeReuse:false
dotnet test GameKouryakuKun.Core.Tests --no-build -nodeReuse:false
launcher/build_launcher.sh "$OUT/launcher"
rm -rf "$OUT/bin"
dotnet publish GameKouryakuKun.App -c Release -o "$OUT/bin" -nodeReuse:false

# 版番号（csproj）
VERSION="$(python3 - <<'PY'
import re
t=open('GameKouryakuKun.App/GameKouryakuKun.App.csproj',encoding='utf-8').read()
m=re.search(r'<Version>([^<]+)</Version>',t)
print(m.group(1) if m else '0.0.0')
PY
)"
echo "VERSION=$VERSION"

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
  echo "== .NET Runtime / Windows Desktop Runtime（同梱しない。初回起動時に公式配布元から runtime\\ へ取得） =="
  echo "ライセンス: MIT（https://github.com/dotnet/runtime/blob/main/LICENSE.TXT , https://github.com/dotnet/windowsdesktop/blob/main/LICENSE.TXT）"
  echo "取得元・版・サイズ・ハッシュ: runtime-manifest.json。取得後は runtime\\LICENSE.txt と runtime\\ThirdPartyNotices.txt を参照。"
  echo
  echo "== 本体の自動更新 =="
  echo "起動ツールは https://raw.githubusercontent.com/sinreikun/game-kouryaku-kun/main/release/latest.json を見て、"
  echo "必要なら GitHub Releases の app-update-<版>.zip を取得・SHA-256 照合・app\\ へ原子的に入れ替えます。"
  echo "起動ツール（ゲーム攻略くん.exe）自身は自動では置き換えません。"
} > "$STAGE/THIRD_PARTY_NOTICES.txt"

DIST="${GKK_DIST:-$ROOT/dist}"
RELEASE_DIR="$ROOT/release"
rm -rf "$DIST" && mkdir -p "$DIST" "$RELEASE_DIR"

python3 - "$STAGE" "$DIST" "$NAME" "$OUT/bin" "$VERSION" "$RELEASE_DIR" <<'PY'
import hashlib, json, os, shutil, sys, zipfile
stage, dist, name, app_bin, version, release_dir = sys.argv[1:7]

# 1) app-update zip = app\ の中身（ルートに GameKouryakuKun.exe）
app_zip_name = f'app-update-{version}.zip'
app_zip_path = os.path.join(dist, app_zip_name)
with zipfile.ZipFile(app_zip_path, 'w', zipfile.ZIP_DEFLATED, compresslevel=9) as z:
    for d, _, fs in os.walk(app_bin):
        for f in sorted(fs):
            full = os.path.join(d, f)
            z.write(full, os.path.relpath(full, app_bin).replace('\\', '/'))
app_sha = hashlib.sha256(open(app_zip_path, 'rb').read()).hexdigest()
app_bytes = os.path.getsize(app_zip_path)
# GitHub Releases の安定 URL（タグ v<version>）
app_url = f'https://github.com/sinreikun/game-kouryaku-kun/releases/download/v{version}/{app_zip_name}'
manifest = {
    'protocol': 1,
    'version': version,
    'sha256': app_sha,
    'bytes': app_bytes,
    'url': app_url,
}
# release/latest.json（リポジトリへコミットする用）と dist 側コピー
os.makedirs(release_dir, exist_ok=True)
with open(os.path.join(release_dir, 'latest.json'), 'w', encoding='utf-8', newline='\n') as f:
    json.dump(manifest, f, indent=2, ensure_ascii=False)
    f.write('\n')
shutil.copy2(os.path.join(release_dir, 'latest.json'), os.path.join(dist, 'latest.json'))
shutil.copy2(app_zip_path, os.path.join(release_dir, app_zip_name))

# 2) 初回ブートストラップ（起動ツール + 初期 app + 周辺）。ソースは同梱しない（公開リポジトリへ誘導）
#    初回からオフラインでも動くよう初期 app を同梱。以降の更新は latest.json 経由。
base = os.path.dirname(stage)
# app.lock を初期 app に書いておく（起動ツールが「導入済み」と認識）
lock_dir = os.path.join(stage, 'app', '.gkk')
os.makedirs(lock_dir, exist_ok=True)
# lock は展開後のファイル一覧が必要。zip を一度読んで作るのは重いので、簡易に exe だけでもよいが
# 起動ツールは files が空だと ParseLock が失敗する → 実ファイルを列挙
files_lines = []
app_stage = os.path.join(stage, 'app')
for d, _, fs in os.walk(app_stage):
    for f in sorted(fs):
        if f.endswith('.lock'): continue
        full = os.path.join(d, f)
        rel = os.path.relpath(full, app_stage).replace('\\', '/')
        if rel.startswith('.gkk/'): continue
        sz = os.path.getsize(full)
        # CRC は 0 でよい（深い検査はランタイム用。app.lock の CRC は起動ツールが深い検査に使わない）
        files_lines.append(f'F\t{sz}\t00000000\t{rel}')
lock = (
    '# ゲーム攻略くん ランチャーの導入記録。手で編集しないでください（消すと次回起動時に再導入します）\n'
    f'id=app\nversion={version}\nzip_sha256={app_sha}\ninstalled_at=bootstrap\n'
    + '\n'.join(files_lines) + '\nend\n'
)
with open(os.path.join(lock_dir, 'app.lock'), 'w', encoding='utf-8', newline='\n') as f:
    f.write(lock)

zpath = os.path.join(dist, name + '.zip')
with zipfile.ZipFile(zpath, 'w', zipfile.ZIP_DEFLATED, compresslevel=9) as z:
    for d, _, fs in os.walk(stage):
        for f in sorted(fs):
            full = os.path.join(d, f)
            z.write(full, os.path.relpath(full, base).replace('\\', '/'))
boot_sha = hashlib.sha256(open(zpath, 'rb').read()).hexdigest()
boot_size = os.path.getsize(zpath)
LIMIT = 40 * 1024 * 1024  # ブートストラップは初期 app 込みで少し大きめを許容
if boot_size > LIMIT:
    sys.exit(f'ブートストラップ zip が {LIMIT} bytes を超えました: {boot_size}')
if app_bytes > 100 * 1024 * 1024:
    sys.exit(f'app-update zip が 100MB を超えました: {app_bytes}')

with open(os.path.join(dist, 'SHA256SUMS.txt'), 'w', encoding='utf-8', newline='\n') as s:
    s.write(f'{boot_sha}  {name}.zip  ({boot_size} bytes)\n')
    s.write(f'{app_sha}  {app_zip_name}  ({app_bytes} bytes)\n')
print(json.dumps({'bootstrap': {'file': name+'.zip', 'sha256': boot_sha, 'bytes': boot_size},
                  'app_update': manifest}, indent=2, ensure_ascii=False))
PY

ls -l "$DIST"
echo "latest.json:"
cat "$RELEASE_DIR/latest.json"
