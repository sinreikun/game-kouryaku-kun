#!/usr/bin/env bash
# src/build.sh の成果（dist/ と release/latest.json）を GitHub Releases に公開し、latest.json を main へ推す。
# 要: gh 認証済み、作業ツリーがクリーン（または意図した変更だけコミット済み）
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"
DIST="${GKK_DIST:-$ROOT/dist}"
VERSION="$(python3 - <<'PY'
import re
t=open('src/GameKouryakuKun.App/GameKouryakuKun.App.csproj',encoding='utf-8').read()
print(re.search(r'<Version>([^<]+)</Version>',t).group(1))
PY
)"
TAG="v${VERSION}"
APP_ZIP="$DIST/app-update-${VERSION}.zip"
LATEST="$ROOT/release/latest.json"
BOOT="$DIST/GameKouryakuKun-Bootstrap.zip"
test -f "$APP_ZIP"
test -f "$LATEST"
test -f "$BOOT"

# latest.json を main に含める
git add release/latest.json
if ! git diff --cached --quiet; then
  git commit -m "release: latest.json for ${TAG} (app auto-update manifest)"
fi
git push origin HEAD:main

# 既存タグがあれば assets だけ上げ直す
if gh release view "$TAG" >/dev/null 2>&1; then
  gh release upload "$TAG" "$APP_ZIP" "$BOOT" "$DIST/SHA256SUMS.txt" --clobber
else
  gh release create "$TAG" "$APP_ZIP" "$BOOT" "$DIST/SHA256SUMS.txt" \
    --title "ゲーム攻略くん ${TAG}" \
    --notes "$(cat <<NOTES
## ゲーム攻略くん ${TAG}

### 初回だけ
1. \`GameKouryakuKun-Bootstrap.zip\` を展開
2. \`ゲーム攻略くん.exe\` を起動（以降同じ exe で自動更新）

### すでに起動ツールがある場合
\`ゲーム攻略くん.exe\` を起動するだけで、\`release/latest.json\` を見て本体（\`app\\\`）を更新します。
起動ツール自身はこの Release では置き換えません。

マニフェスト: https://raw.githubusercontent.com/sinreikun/game-kouryaku-kun/main/release/latest.json
NOTES
)"
fi
echo "Published $TAG"
echo "latest.json URL: https://raw.githubusercontent.com/sinreikun/game-kouryaku-kun/main/release/latest.json"
echo "Release: https://github.com/sinreikun/game-kouryaku-kun/releases/tag/$TAG"
