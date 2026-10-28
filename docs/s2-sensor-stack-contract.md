# S2 Sensor Stack 契約（Observation Bundle）

- 文書: `docs/s2-sensor-stack-contract.md`
- 拘束上位: `future-architecture.md`（特に §6 Observation / Sensor Stack、§7 Candidate/Track/Entity、§30 S2）
- 版: S2-contract-0.2（2026-10-28 / v0.33.6）
- 対象 Stage: **S2**（Sensor Stack の単一契約面。アルゴリズムは KEEP）

本契約は実装可能な短い仕様である。Global Transform / Track 同一性 / Explorer / Goal / P-7 / しきい値 / 学習は **変更しない**。

---

## 0. 用語

| 語 | 意味 |
|---|---|
| Observation Bundle | 1 ステップ分の Sensor Stack 出力の単一面（意味ラベルなし） |
| CoordinateFrame | Global Transform 仮説集合と採用結果・confidence（v0.32.9 KEEP） |
| ChangeField | 補償後の局所残差・出現消失（既存 Regions / ResidualCells の写像） |
| Candidate / Track / Relation | §7。Identity ≠ Appearance。ゲーム意味禁止 |
| EntityExperience スナップショット | Track ごとの予測可能統計の **読み取り専用コピー**（既存 `EntityExperience` KEEP） |
| Assembler | 既存観測から Bundle を **派生**する変換器（再計算しない） |
| 旧観測経路 | 現行 Session → `StructuredObservation` → `memory.sqlite`（削除しない・学習の正） |
| Kernel 正規観測面 | `RuntimeKernel.Step` が Experience に書く観測。Bundle JSON があれば **Bundle** |

---

## 1. Observation Bundle フィールド（§6.1）

論理型（実装: `ObservationBundle`）。1 Bundle = **1 アクション完了サイクルの観測面**。`schema_version` 現行 **2**（entity_experience 層追加）。

| フィールド | 型 | 必須 | 説明 |
|---|---|---|---|
| `schema_version` | int | はい | Bundle 契約版。現行 `2` |
| `step` | int64 | はい | 手番号（StructuredObservation.Step を写す） |
| `raw` | object? | いいえ | `{ "kind": "none"\|"frame_ref", "hash"?: string }`。常時フル画素は保存しない |
| `coordinate_frame` | CoordinateFrameResult? | 層 status 付き | GT 採用結果 |
| `change_field` | ChangeFieldSnapshot? | 同上 | 補償後残差・領域 |
| `candidates` / `tracks` / `relations` | 配列 | はい（空可） | §7 形式コピー |
| `text` / `numbers` | 要約 | 層 status | OCR 観測（意味は下流） |
| `temporal` | TemporalSnapshot? | 層 status | 境界・自然変化・delayed |
| `entity_experience` | EntityExperienceSnapshot? | 層 status | 表ヒット／追従組の集計コピー。無ければ `layers.entity_experience=not_observed` |
| `provenance` | string | はい | agent / human / wait / supervisor_gap 等 |
| `layers` | LayerStatusMap | はい | 各層 status（欠測捏造禁止） |
| `source` | object | はい | `{ kind, schema, assembler: "sensor_stack_assembler@2" }` |

**Meaning 欄を持たない。** 「中央最大＝キャラ」およびゲーム意味ラベルを Bundle / Kernel API に入れない。`TrackQuality` サイズビンは **人間診断のみ**（Bundle に載せない）。

### 1.5 EntityExperienceSnapshot

| フィールド | 由来（KEEP・コピーのみ） |
|---|---|
| `observed` / `keys` / `dropped` | `EntityExperience` 集計 |
| `tables[]` | 各表 `appearance` / `movement` / `relation` / `text_number` の Predicted/Hits（安定番号・不安定） |
| `follows[]` | `Follows()` の TrackA/TrackB/Follow/Trials/LogBf（上限付き） |

アルゴリズム・しきい値・Bayes factor 定数は触らない。Assembler は `EntityExperience.Snapshot()` を呼ぶだけ。

---

## 2. 既存型からの写像

| Bundle 面 | 既存 | 規則 |
|---|---|---|
| 全体 | `StructuredObservation` | 主入力。再解釈しない |
| coordinate_frame | `GlobalTransformConfidence` | null → not_observed / EntityLayer 無し → not_implemented |
| change_field | Regions + ResidualCells | コピー |
| candidates/tracks/relations | `EntityLayer` | 無し → 空 + not_implemented |
| entity_experience | `EntityExperience`（任意引数） | null → not_observed（捏造しない） |
| text/numbers/temporal | TextLayer / TemporalLayer | 既存 status |

**KEEP:** GT / Track / EntityExperience 学習。**REBUILD:** Bundle 契約面と Assembler / Kernel 正規面。

---

## 3. Sensor Stack 規則（§6.2）

1. 観測可能な量を消さない（本スライスでマスク変更なし）。
2. CoordinateFrame は既存 GT 結果の写し。
3. 説明できない局所変化は候補として残す。
4. OCR は観測。意味は下流。

---

## 4. Kernel / dual-path

| 経路 | 挙動 |
|---|---|
| 既定探索 | Session → StructuredObservation → memory.sqlite。**学習の正。変更なし** |
| Bundle 派生 | `SensorStackAssembler.FromStructured(obs, entityExperience?: …)` |
| 診断フラグ | `GKK_ATTACH_OBSERVATION_BUNDLE=1`（既定 OFF）→ ログ `observation_bundle` |
| dual-append ON | Session が Bundle を組み立て `StepContext.ObservationBundleJson` に渡す（Attach OFF でも可） |
| RuntimeKernel | `ResolveObservation`: Bundle JSON があれば **正規面 = Bundle**（`ObservationKind.Bundle`、Experience.ObservationJson = Bundle）。Structured は provenance.`legacy_structured_json` に残す |
| Bundle 無し | 従来どおり Structured / LegacyCoarse / Absent |
| PlaySession 置換 | **しない** |

`StepResult.CanonicalSurface` ∈ `bundle` \| `structured` \| `legacy_coarse` \| `absent`。

欠測ゼロ: 無い層は `layers` で明示。EntityExperience 未渡しは `not_observed`。

---

## 5. 非目標

- P-7・しきい値・GT/Track/EntityExperience **アルゴリズム**変更
- Explorer / Goal / 学習経路変更
- PlaySession 全面置換
- TrackQuality サイズビンを Bundle/AI API に混入
- StructuredObservation 経路の削除
- 「中央最大＝キャラ」の API 混入
- GitHub Release / `latest.json`（親がレビュー後に判断）

---

## 6. 合格条件（v0.33.6 スライス）

1. EntityExperience が Bundle に実スナップショットとして付く（スタブ廃止）
2. Kernel が Bundle JSON を正規観測面として書く（Structured は provenance に残る）
3. フラグ既定 OFF・dual-append 既定 OFF で探索挙動不変
4. 単体テスト: EE 写像・Kernel Bundle 正規化・意味ラベル禁止
5. `src/audit/v0.33.6.md` と §30.1 更新

---

## 7. 次の微小ステップ

1. （任意）実機 dual-append + Bundle ログ確認
2. S2 残り: Kernel API 固定の締め・診断 zip への Bundle 要約（任意）
3. **S3 Causal WM**（P-7 には戻らない）
