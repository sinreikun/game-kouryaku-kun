# ゲーム攻略くん

画面と入力だけで、ゲームを**経験から学ぶ** Windows アプリです。

ゲームの内部データ・攻略知識・意味ラベルは使いません。観測できるのは画面の画素と文字（OCR）、操作できるのはキー入力だけです。見たものの意味は、アプリが経験から推理します。

> **人間向けの文脈（AI には与えない）:** 最初の検証対象は『トルネコの大冒険 不思議のダンジョン ちょっとステキなリマスター』（Steam）です。プロファイルに書くのはウィンドウ特定とキー割当など「身体と接続条件」だけです。

## 方針（要約）

- **ゼロ知識:** ゲーム固有のルール・マップ・アイテム意味などをコードや設定に埋め込まない
- **目と手は開発者、意味は経験:** 画面取得・入力送信はインフラ。意味付けは学習側
- **しきい値を緩めない:** 実機で困っても、都合よく閾値を下げて通さない
- **LLM を記憶や攻略知識の置き場にしない:** 連続経験は `memory.sqlite` などアプリ内の記録に残す

詳細は `principle-original.md`（最上位原則）と **`future-architecture.md`（最終目標からの再設計・現行の拘束）** を先に読んでください。`design.md` / `phase2-design.md` / `src/docs/` は史料・部分移植元です（新規実装の拘束にはしません）。

## 構成

| パス | 内容 |
|------|------|
| `future-architecture.md` | 最終目標からの全面再設計（KEEP/REBUILD/REMOVE/MISSING・新ロードマップ） |
| `docs/s1-runtime-contract.md` | S1: Experience vNext / Kernel / OldDbAdapter の実装契約 |
| `src/` | .NET ソリューション（WPF 本体・Core・Storage・OCR・テストなど） |
| `src/GameKouryakuKun.sln` | ソリューション |
| `src/profiles/` | ゲーム接続用プロファイル（身体条件のみ） |
| `src/docs/` | Observation Layer などの設計ドキュメント |
| `src/audit/` | 版ごとの監査メモ |
| `tools/` | 解析・再生用スクリプト |
| `wip-chords/` | 同時押しまわりの作業用ソーススナップショット |
| `phase0/` / `phase0v2/` | 初期プローブのソース（歴史的） |

このリポジトリには **実機診断 zip** と **配布用 release zip** は含めていません（容量・ゲーム画面の取り扱いのためローカル専用）。

## ビルド（Windows / .NET 10）

要件:

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- Windows（WPF / Windows App SDK 系プロジェクトのため）
- 配布 zip まで作る場合: `python3`、`g++`、`mingw-w64`（`src/build.sh` 参照）

```powershell
cd src
dotnet build GameKouryakuKun.sln
dotnet test GameKouryakuKun.Core.Tests
dotnet run --project GameKouryakuKun.App
```

Linux 上ではユニットテストの一部とコアは動かせますが、WPF 本体の実行は Windows 向けです。

配布用の一連の publish は `src/build.sh`（出力はローカルの `phase1/` / `dist/`。これらは `.gitignore` 済み）です。

**利用者向け:** 初回だけ [Releases](https://github.com/sinreikun/game-kouryaku-kun/releases) のブートストラップ zip を展開し、`ゲーム攻略くん.exe` を起動してください。以降は同じ exe が `release/latest.json` を見て本体を自動更新します（詳細は `src/README.md` の「自動更新」）。

**開発者向け公開:** `src/build.sh` → `tools/publish_release.sh`（GitHub Releases + `release/latest.json` を main へ）。

## ライセンス

MIT（`LICENSE`）。サードパーティ部品（例: launcher 同梱の miniz）は各ディレクトリの LICENSE を参照してください。

## 関連

別系統の実験リポジトリ: [torneko-remaster-AI](https://github.com/sinreikun/torneko-remaster-AI)（本リポジトリとは別物です）。
