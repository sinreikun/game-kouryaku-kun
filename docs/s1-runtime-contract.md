# S1 Runtime Contract（Experience vNext / Kernel / Adapter）

- 文書: `docs/s1-runtime-contract.md`
- 拘束上位: `future-architecture.md`（特に §26・§30.1・付録 B t113u）
- 版: S1-contract-0.1（2026-10-28）
- 対象 Stage: **S1**（Runtime Kernel + Experience DB vNext の縦スライス）

本契約は実装可能な短い仕様である。学習アルゴリズム・Sensor Stack・Explorer 重み・Global Transform・Goal / P-7 / しきい値は **変更しない**。

---

## 0. 用語

| 語 | 意味 |
|---|---|
| 旧 DB | Profile データディレクトリの `memory.sqlite`（現行 ExperienceStore） |
| vNext DB | 同ディレクトリの **`experience-vnext.sqlite`**（別ファイル。旧 DB を UPDATE しない） |
| Experience レコード | 1 アクション完了サイクル分の観測・行動・結果・provenance |
| ModelBundle | 予測・表現・価値・方策などの版付きセット。推論に使った束の識別子 |
| Adapter | 旧 DB を **read-only** で読み、vNext 形式へ派生コピーする変換器 |
| LossReport | 変換で落ちた情報・推定した箇所の明示記録 |

---

## 1. ファイル配置

```
{profileDataDir}/
  memory.sqlite              # 旧・read-only 史料 / Replay 元（t113u。削除禁止）
  memory.sqlite-wal/-shm     # あれば Adapter は SQLite が読める形で扱う。書かない
  experience-vnext.sqlite    # 新 Experience DB（本契約）
  experience-vnext.sqlite-wal/-shm
```

- `{profileDataDir}` は現行どおり Profile 単位（例: `data/profiles/<id>/`）。
- 旧 DB パスを Adapter に渡す。vNext パスは既定で `Path.Combine(profileDataDir, ExperienceVNextStore.FileName)`（`experience-vnext.sqlite`）。
- 不可逆な旧 DB 削除は、将来不要と判断できても **その時点で改めて人間確認**（本契約では削除 API を提供しない）。

---

## 2. Experience レコード形状

論理型（実装: `ExperienceRecord`）。1 レコード = **1 アクション完了サイクル**。

| フィールド | 型 | 必須 | 説明 |
|---|---|---|---|
| `experience_id` | int64 | 書込時採番 | vNext 側主キー |
| `seq` | int64 | はい | 通し番号。Adapter 由来は旧 `experience_stream.seq` をそのまま写す |
| `run_id` | int64? | いいえ | システム実行単位の照合用。学習境界にしない |
| `created_at` | string (ISO-8601 Z) | はい | 記録時刻 |
| `action` | string | はい | 論理キー（`wait` 含む）。ゲーム意味ラベル禁止 |
| `action_source` | `agent` \| `human` \| `wait` \| `unknown` | はい | 出どころ。旧に無ければ `unknown` + LossReport.inferred |
| `observation_kind` | `structured` \| `legacy_coarse` \| `absent` | はい | 観測の質。推測で structured に昇格しない |
| `observation_json` | string? | kind=structured のとき | 旧 `structured_observations.json` をそのまま |
| `observation_schema_version` | int? | 同上 | 旧 schema_version |
| `result_changed` | bool? | あれば | 画面変化の有無（観測事実） |
| `result_next_state` | string? | あれば | **不透明トークン**。旧 State ID。意味解釈しない |
| `result_change_ratio` | double? | あれば | 変化率 |
| `prediction_json` | string? | あれば | 旧 decision の予測抜粋（JSON）。学習ロジックは読まない段階でも保持可 |
| `surprisal` / `excess` | double? | あれば | 予測誤差系の数値（意味付けしない） |
| `model_bundle_id` | string | はい | §3。旧由来はプレースホルダ + LossReport |
| `provenance_json` | string | はい | §4 |
| `source_db` | string? | Adapter 時 | 旧ファイル名またはパス識別子 |
| `source_seq` | int64? | Adapter 時 | 旧 seq（`seq` と同一でも明示） |

### 2.1 observation_kind 規則

| 条件 | kind |
|---|---|
| 同一 seq に `structured_observations` 行あり | `structured`（json をコピー。中身を再解釈しない） |
| stream 行のみ（旧 coarse state 文字列あり） | `legacy_coarse`（state / next_state を不透明トークンとして result に載せる） |
| 対応行なし | `absent`（レコードを捏造しない。スキップか LossReport.dropped） |

---

## 3. ModelBundle 識別

形式（文字列・安定・比較可能）:

```
{producer}/{bundle_name}@{version}
```

例:

- 現行未配線の既定プレースホルダ: `legacy-adapter/none@0`
- 将来の Champion: `registry/wm-explorer@2026.10.28+sha…`

| 規則 | |
|---|---|
| S1 | ライブ書込はまだしない。Adapter が書くレコードは必ず `legacy-adapter/none@0` |
| 旧 DB に bundle 列は無い | **推定しない**。「無し」をプレースホルダで明示し LossReport.inferred に `model_bundle_id=legacy-adapter/none@0 (old DB had no bundle)` |
| Bundle 実体 | S1 ではメタ表 `model_bundles` に id と json メモだけでよい。パラメータ本体は後続 Stage |

---

## 4. Provenance

`provenance_json` 最小キー:

```json
{
  "writer": "old_experience_adapter" | "runtime_kernel",
  "adapter_version": "1",
  "source": { "kind": "memory.sqlite", "schema_version": 17, "path_basename": "memory.sqlite" },
  "joined": ["experience_stream", "decision_records?", "structured_observations?"],
  "loss_report_id": 1
}
```

- ゲーム意味・人間診断ラベルを入れない。
- `writer=runtime_kernel` は S1 後半以降。本スライスの既定経路は Adapter。

---

## 5. Runtime Kernel ステップ API（契約のみ・本スライス未配線）

擬似 API（実装は後続。PlaySession への既定接続は **OFF**）:

```
StepResult Step(ActionSpec action, StepContext ctx);
// 1 action = 1 完全観測サイクル:
//   sense → (optional predict) → act → wait-stable → sense-after → append Experience → (optional learn)
```

| 要件 | |
|---|---|
| 原子性 | 途中失敗でも「半端な Experience」を成功扱いしない（欠測は Health） |
| 1 手 1 観測 | 現行方針を維持 |
| Dual-run | 同一入力で旧ループと新 Kernel を比較できる余地を残す。学習ロジックは変えない |
| Feature flag | `RuntimeFlags.UseExperienceVNextWriter` **既定 false**。true でも S1 初期は Kernel 未接続 |

本スライスでは **フラグ定数と文書のみ**。Session / PlaySession へ書き込み経路を繋がない。

---

## 6. Dual-run / Feature flag

| フラグ | 既定 | 効果（S1） |
|---|---|---|
| `UseExperienceVNextWriter` | false | true でも本スライスは no-op（Kernel 未実装）。将来ライブ追記の扉 |
| Adapter 実行 | 明示呼び出しのみ | 起動時自動変換しない（大量 I/O・誤昇格防止） |

新旧比較の Replay は「旧 DB read-only + Adapter → メモリまたは vNext」で行う。

---

## 7. OldExperienceAdapter 契約

### 7.1 入力

| 引数 | |
|---|---|
| `oldDbPath` | 既存 `memory.sqlite` へのパス（必須・存在） |
| `vNextDbPath` | 省略時は同ディレクトリの `experience-vnext.sqlite` |
| `limit` | 直近 N 件（`experience_stream.seq DESC`）。0 以下は全件（テスト以外は上限推奨） |
| `modelBundleId` | 省略時 `legacy-adapter/none@0` |

### 7.2 オープン条件（旧 DB）

- `SqliteOpenMode.ReadOnly`
- `PRAGMA query_only=ON`
- マイグレーション・バックアップ・INSERT/UPDATE/DELETE **禁止**
- 旧行の意味の上書き禁止（新側に派生行のみ）

### 7.3 出力

1. **メモリ:** `IReadOnlyList<ExperienceRecord>` + `LossReport`
2. **任意永続:** 同じレコードを `experience-vnext.sqlite` の `experiences` へ INSERT、`loss_reports` へ 1 行

### 7.4 主結合

```
experience_stream AS s
LEFT JOIN decision_records d ON d.seq = s.seq
LEFT JOIN structured_observations o ON o.seq = s.seq
ORDER BY s.seq ASC   -- 出力は時系列昇順（読取は直近 limit を切ってから昇順化してよい）
```

- 同一 seq に structured が複数ある場合: **最新 obs_id を 1 件**採用し、余剰は LossReport.dropped。
- decision が複数ある場合: **最大 decision_id を 1 件**、余剰は dropped。

### 7.5 LossReport フィールド

| フィールド | 説明 |
|---|---|
| `adapter_version` | 文字列。現行 `"1"` |
| `source_path` | 渡した旧 DB パス |
| `source_schema_version` | `PRAGMA user_version` または migrations 最大 |
| `mapped_count` | 出力 Experience 件数 |
| `dropped` | `{ code, detail, count? }[]` 落ちた情報 |
| `inferred` | `{ code, detail, count? }[]` 推定・プレースホルダ |
| `notes` | 自由文（ゲーム意味禁止） |

**dropped の例（正直に列挙）:**

| code | いつ |
|---|---|
| `no_structured_observation` | stream のみ（legacy_coarse） |
| `no_decision_record` | decision 無し |
| `duplicate_structured_dropped` | 同一 seq の余剰 structured |
| `duplicate_decision_dropped` | 同一 seq の余剰 decision |
| `table_missing` | schema が古く表が無い |
| `unmapped_side_table` | reaction_maps / goals 等は S1 で写さない（集約 count） |

**inferred の例:**

| code | いつ |
|---|---|
| `model_bundle_placeholder` | 旧に bundle が無い |
| `action_source_default` | 列欠落時に `unknown` |
| `legacy_state_as_opaque_token` | state/next_state を意味付けせずトークンとしてコピーしたことの明示 |

---

## 8. vNext スキーマ（S1 最小）

メタ表名: `vnext_schema_meta`。マイグレーション一覧: `Schemas.ExperienceVNext`。

```
experiences(...)           -- §2 列
loss_reports(report_id, created_at, source_path, adapter_version, json)
model_bundles(model_bundle_id, created_at, json)
```

- `user_version` / migrations 表は SqliteDb 共通手順。
- S1 で旧 Memory マイグレーション番号を進めない。

---

## 9. S1 非目標（やらない）

- 学習アルゴリズム・Explorer 重み・Goal / Skill / Planning の変更
- Sensor Stack / Global Transform / Track / EntityExperience の再実装（S2）
- PlaySession を Runtime Kernel に置換すること
- 旧 `memory.sqlite` の in-place migrate / 削除 / 行の意味上書き
- しきい値緩和、P-7 並列化
- 起動時の全件自動変換
- GitHub Release / Bootstrap zip（インフラのみのため不要）

---

## 10. 合格条件（本スライス）

1. 契約書がリポジトリにある（本稿）
2. Adapter が旧 DB を ReadOnly で開き、合成 fixture で Experience + LossReport を返す
3. 任意で vNext ファイルへ書け、旧ファイルに書き込みが無い
4. 単体テストがゲーム意味ラベルを持たない
5. Session へのライブ配線は無い（または既定 OFF の no-op）

---

## 11. 次の微小ステップ（示唆）

1. Adapter を診断 Replay ツールから呼び、実機 `memory.sqlite` で LossReport 分布を見る（書込は vNext のみ）
2. Runtime Kernel の空シェル（Step が旧 Session 1 手を委譲するだけ）をフラグ裏に置く
3. その後 S2 で Sensor Stack 契約へ GT / Track を移植
