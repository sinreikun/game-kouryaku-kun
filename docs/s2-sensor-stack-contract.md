# S2 Sensor Stack 契約（Observation Bundle）

- 文書: `docs/s2-sensor-stack-contract.md`
- 拘束上位: `future-architecture.md`（特に §6 Observation / Sensor Stack、§7 Candidate/Track/Entity、§30 S2）
- 版: S2-contract-0.1（2026-10-28）
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
| Assembler | 既存 `StructuredObservation` 等から Bundle を **派生**する変換器（再計算しない） |
| 旧観測経路 | 現行 Session → `StructuredObservation` → `memory.sqlite`（削除しない） |

---

## 1. Observation Bundle フィールド（§6.1）

論理型（実装: `ObservationBundle`）。1 Bundle = **1 アクション完了サイクルの観測面**。

| フィールド | 型 | 必須 | 説明 |
|---|---|---|---|
| `schema_version` | int | はい | Bundle 契約版。現行 `1` |
| `step` | int64 | はい | 手番号（StructuredObservation.Step を写す） |
| `raw` | object? | いいえ | 参照用。`{ "kind": "none"\|"frame_ref", "hash"?: string }`。常時フル画素は保存しない |
| `coordinate_frame` | CoordinateFrameResult? | 層 status 付き | GT 採用結果。無ければ層 status=`not_observed`/`not_implemented` |
| `change_field` | ChangeFieldSnapshot? | 同上 | 補償後残差・領域。既存 Regions / ResidualCells |
| `candidates` | CandidateSnapshot[] | はい（空可） | 今フレームの局所仮説。意味ラベルなし |
| `tracks` | TrackSnapshot[] | はい（空可） | 時間的連続性の仮説（ID） |
| `relations` | RelationSnapshot[] | はい（空可） | 距離・相対変位・隣接・重なり・同一運動 |
| `text` | TextSnapshotRef? | 層 status 付き | OCR 観測の要約（文字列は観測事実。意味は下流） |
| `numbers` | NumberSnapshotRef[] | はい（空可） | 数字系列（何の数かは持たない） |
| `temporal` | TemporalSnapshot? | 層 status 付き | 境界時間・自然変化・delayed |
| `provenance` | string | はい | `agent` \| `human` \| `wait` \| `supervisor_gap` \| 既存 Provenance 写し |
| `layers` | LayerStatusMap | はい | 各層の `observed` / `inherited` / `not_observed` / `not_implemented` |
| `source` | object | はい | `{ "kind": "structured_observation", "schema": 1, "assembler": "sensor_stack_assembler@1" }` |

**Meaning 欄を持たない。** API・JSON キー・列挙値にゲーム意味（キャラ・敵・扉等）と診断注釈「中央最大＝キャラ」を入れない。形式名のみ（`moved` / `appearance_changed` / `field` / `translation` 等）。

### 1.1 CoordinateFrameResult

| フィールド | 由来（KEEP） |
|---|---|
| `decision` | `GlobalTransformConfidence.Decision`（none/zero/translation/field） |
| `accepted` | `Accepted` |
| `method` | `Method` |
| `p_field` / `p_translation` / `p_zero` / `p_none` | 各事後 |
| `log_odds` | `LogOdds` |
| `field` | `Field`（8 係数） |
| `inliers` / `points` / `coverage` / `median_residual` / `error_reduction` / `continuity` | 同名 |

アルゴリズム・しきい値・BIC は触らない。値のコピーのみ。

### 1.2 ChangeFieldSnapshot

| フィールド | 由来 |
|---|---|
| `changed_cells` | `RegionLayer.ChangedCells` |
| `raw_changed_cells` | `RegionLayer.RawChangedCells` |
| `common_shift` | `RegionLayer.CommonShift` または EntityLayer |
| `regions` | `ChangeRegion` の形式コピー（Id/Track/bbox/cells/causality 形式名・NewTrack） |
| `residual_cells` | `EntityLayer.ResidualCells`（あれば） |

### 1.3 CandidateSnapshot / TrackSnapshot / RelationSnapshot

| 概念 | 由来 |
|---|---|
| Candidate | `EntityCandidate`（位置・Cells・Appearance 符号・Dx/Dy・Moved/AppearanceChanged/New/Confidence/Age/IdentityPosterior 等）。**AppearanceState は形式記号** |
| Track | 候補から派生する ID 要約（Track Id・Age/Lifetime・Missing 相当は Disappeared リスト）＋ `TrackQuality` 集計が取れる場合のみ任意 |
| Relation | `RelationObs`（Dx/Dy/Distance/Change/Adjacent/Overlap/SameMove） |

EntityExperience 統計の本移植は **本スライス対象外**（スタブ／将来インタフェースのみ可）。

### 1.4 text / numbers / temporal

| 層 | 由来 |
|---|---|
| text | `TextLayer`（Status/ReadKind/DiffKind/Lines 要約・件数） |
| numbers | `NumberObs` の形式コピー |
| temporal | `TemporalLayer` + `DelayedChange`（あれば） |

各層は `layers.*` に status を明示。欠測を推測で埋めない。

---

## 2. 既存型からの写像（REBUILD は表面のみ）

| Bundle 面 | 既存 | 規則 |
|---|---|---|
| 全体 | `StructuredObservation` | 主入力。再解釈しない |
| coordinate_frame | `EntityLayer.GlobalTransform` → `GlobalTransformConfidence` | null → 層 `not_observed` または EntityLayer 無しなら `not_implemented` |
| change_field | `RegionLayer` + ResidualCells | Regions をコピー |
| candidates/tracks/relations | `EntityLayer` | null → candidates/tracks/relations 空 + 層 `not_implemented` |
| text/numbers | `TextLayer` | 既存 status を継承 |
| temporal | `TemporalLayer` | 同上 |
| provenance | `StructuredObservation.Provenance` | 写す |
| TrackQuality / EntityExperience | 任意・後続 | Assembler は読まなくてよい（本スライス） |

**KEEP:** CommonMotion / Global Transform 受け入れ数学、EntityTracker 同一性、しきい値。  
**REBUILD:** Bundle という単一契約面と Assembler。

---

## 3. Sensor Stack 規則（§6.2）

1. 画面から観測可能な量を開発者が消さない（マスク用途の分離は現行継承。本スライスでマスク変更なし）。
2. CoordinateFrame は「多くの局所変位を一つの低次元変換が説明できるか」の証拠で決める（ゲーム意味なし）— **既存 GT の結果を写すだけ**。
3. 説明できない局所変化は候補として残す（ResidualCells / Candidates）。
4. OCR は観測。文字列の意味は下流が経験で付ける。

---

## 4. Kernel / dual-path

| 経路 | 挙動 |
|---|---|
| 既定（現行） | Session が StructuredObservation を作り memory.sqlite に書く。**変更なし** |
| Bundle 派生 | `SensorStackAssembler.FromStructured(obs)` が Bundle を返す（純粋関数・副作用なし） |
| 診断フラグ | `RuntimeFlags.AttachObservationBundle`（環境変数 `GKK_ATTACH_OBSERVATION_BUNDLE=1`）。**既定 OFF** |
| フラグ ON | Session が観測保存後に Bundle を組み立て、ログ `observation_bundle` に要約を書く。探索・学習・GT は不変 |
| RuntimeKernel | StepContext に任意で `ObservationBundleJson` を載せられる（空シェル）。dual-append 既定は Bundle を必須にしない |
| PlaySession 置換 | **しない** |

欠測ゼロ: Assembler は入力に無い層を捏造せず `layers` で明示する。

---

## 5. 非目標（やらない）

- P-7 並列化・しきい値緩和・GT/Track アルゴリズム変更
- Explorer / Goal / Skill / Planning / 学習の変更
- PlaySession の Runtime Kernel への全面置換
- EntityExperience 本書き換え（インタフェース／スタブのみ可）
- StructuredObservation 経路の削除（唯一経路化は後続）
- ゲーム意味ラベル・「中央最大＝キャラ」の API 混入
- GitHub Release / Bootstrap / `release/latest.json` 更新（本スライスはコード＋契約＋テスト）

---

## 6. 合格条件（本スライス）

1. 本契約書がリポジトリにある
2. `ObservationBundle` モデルと `SensorStackAssembler` がある
3. 合成 StructuredObservation から Bundle を組み立て、GT 信頼度フィールドが round-trip する
4. 欠測層が `layers` で明示される（推測埋めなし）
5. 単体テストが意味ラベル／「中央最大＝キャラ」を Bundle JSON に含まないことを検証する
6. フラグ既定 OFF で探索挙動が変わらない（配線は no-op）
7. `src/audit/v0.33.5.md` と `future-architecture.md` §30.1 の更新

---

## 7. 次の微小ステップ（示唆）

1. 実機でフラグ ON の数手で Bundle ログ要約を確認（任意・挙動非変更のため必須ではない）
2. EntityExperience を Bundle 付帯または別契約へ（アルゴリズム KEEP）
3. Kernel Step が Bundle を正規入力として扱う（Session 分解の続き）
4. その後 S3 Causal WM
