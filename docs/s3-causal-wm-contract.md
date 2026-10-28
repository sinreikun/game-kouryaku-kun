# S3 Causal WM 契約（H0–H4 / PE ルーティング）

- 文書: `docs/s3-causal-wm-contract.md`
- 拘束上位: `future-architecture.md` §8 Temporal/Causality・§9 World Model・§10 Prediction Error・§30 S3
- 版: S3-contract-0.1（2026-10-28 / v0.33.7）
- 対象 Stage: **S3**（因果仮説比較の契約面 + 薄いシェル。深層 WM・能動実験は後続）

本契約は実装可能な短い仕様である。GT / Track / Explorer / Goal / P-7 / しきい値 / 学習アルゴリズムは **変更しない**。

---

## 0. 用語

| 語 | 意味 |
|---|---|
| CausalComparisonRecord | 1 関連イベント（典型: 1 手完了）に対する H0–H4 比較結果 |
| HypothesisScore | 仮説ごとの証拠要約（形式名・スコア・層 status） |
| PeRoutingTag | PE の行き先タグ（§10.2）。**記録のみ**（能動実験は S5） |
| Comparer | 既存 NaturalBaseline / RegionCausality / delayed / wait から比較を **派生** |
| 学習の正 | Session → StructuredObservation → memory.sqlite（不変） |

---

## 1. H0–H4 仮説集合（§8.2）

| ID | 内容 | 採用規則（要約） |
|---|---|---|
| **H0** 自然 | wait / no-input 分布と区別不能 | NaturalBaseline / wait 観測との対照。Action 後に起きただけでは棄却も採用もしない |
| **H1** 即時因果 | 特定 Action 後に残差が説明される | RegionCausality の手依存・CausedShare 等。**Action 後に起きたことだけで H1 を採用しない**（対照頻度・LogBf が必要） |
| **H2** 遅延因果 | ラグ k で説明される | `DelayedChange` / NaturalBaseline.AfterAction 等の遅延証拠 |
| **H3** 文脈条件付き | 同一 Action でも局所文脈で分岐 | 本スライス: 多くは `not_implemented`（予測 WM 接続後） |
| **H4** 共通原因 | 複数局所が同一潜伏要因 | 本スライス: 多くは `not_implemented`（独立クラス集計のヒントのみ任意） |

### 1.1 証拠

- 対照頻度（wait vs action）
- Bayes factor / 周辺尤度差（既存 `RegionCausality.LogBeta` / `ActionDependence` を流用可）
- 交差検証損失は後続（本スライスではフィールド予約・未計算なら `not_observed`）

**禁止:** ゲーム意味ラベル（敵・罠等）。形式名のみ（`h0_natural` … `h4_common_cause`）。

### 1.2 CausalComparisonRecord

| フィールド | 説明 |
|---|---|
| `schema_version` | 現行 `1` |
| `step` | 手番号 |
| `action` | 論理キー（不透明） |
| `node` | 不透明トークン（旧 State ID）。意味解釈しない |
| `changed` | 画面変化の有無（観測事実） |
| `provenance` | agent/human/wait/… |
| `hypotheses[]` | H0–H4 各 `HypothesisScore` |
| `preferred` | 採用仮説 ID または `undecided` |
| `pe_routing_tags[]` | §2 のタグ（0 個以上） |
| `evidence_sources[]` | 使った既存源（`natural_baseline` / `region_causality` / `delayed` / `bundle_temporal` …） |
| `layers` | comparer / baselines の status |
| `comparer` | `causal_hypothesis_comparer@1` |

### 1.3 HypothesisScore

| フィールド | 説明 |
|---|---|
| `id` | `h0_natural` … `h4_common_cause` |
| `status` | `scored` / `not_observed` / `not_implemented` |
| `log_score` | 任意。相対比較用（無いなら null） |
| `log_bf` | 任意。対照の対数ベイズ因子 |
| `support_n` / `contrast_n` | 任意。対照カウント |
| `note` | 形式的短いメモ（ゲーム意味禁止） |

---

## 2. PE ルーティングタグ（§10.2・記録のみ）

| タグ | 意味 | 本スライス |
|---|---|---|
| `representation_coarse` | 表現が粗い → 文脈特徴候補（S4） | 残差大かつ説明仮説弱いとき付与可 |
| `track_unstable` | Track 不安定に集中 | IdentityPosterior≤0 / New 多いとき |
| `wait_same_distribution` | wait と同分布 → 自然変化 | H0 優勢または wait 手で変化 |
| `reduces_with_history` | 履歴で減る → 時間文脈 | 多くは `not_observed`（履歴系列未配線） |
| `reduces_nowhere` | どこでも減らない → 能動実験（S5） | 明示スタブ。勝手に能動探索しない |

タグは **記録**。Explorer 重み・しきい値・P-7 を変えない。

---

## 3. 既存断片からの写像（KEEP）

| 既存 | 使い方 |
|---|---|
| `NaturalBaseline` | H0 の wait/gap 率・Uncertainty・AfterAction カウント |
| `RegionCausality` | H1 の ActionDependence / DiffCaused / Class。しきい値定数は触らない |
| `DelayedChange` / Bundle.temporal.delayed | H2 の遅延証拠 |
| wait provenance | H0 対照の入力種別 |
| Observation Bundle | 入力面（candidates/tracks/residual）。Comparer は Bundle または Structured から読める |

無い源は捏造せず `not_observed` / `not_implemented`。

---

## 4. Kernel / Session / dual-path

| 経路 | 挙動 |
|---|---|
| 既定 | 比較しない。学習不変 |
| フラグ | `RuntimeFlags.AttachCausalWm`（`GKK_ATTACH_CAUSAL_WM=1`）。**既定 OFF** |
| フラグ ON | Session が 1 手後に Comparer を呼び、ログ `causal_wm` + `LatestCausalComparison` |
| Bundle | 任意で `ObservationBundle.CausalComparison` に載せる（schema **3**）。Assembler は渡された Record を写すだけ |
| RuntimeKernel | `StepContext.CausalComparisonJson` を provenance に保持（学習しない） |
| PlaySession 置換 | **しない** |

---

## 5. 非目標

- P-7・しきい値緩和・GT/Track/EE アルゴリズム変更
- Explorer / Goal / Skill 再設計（S5/S6）
- 特徴重要度の本実装（S4）
- 能動実験の実行（S5）
- 深層 Predictive WM の完成（本スライスは比較シェル）
- GitHub Release / `latest.json`

---

## 6. 合格条件（本スライス）

1. 本契約書がある
2. `CausalComparisonRecord` / `CausalHypothesisComparer` がある
3. 合成 fixture で H0/H1/H2 の少なくとも一部が既存源から `scored` になり、H3/H4 は明示 status
4. PE タグが形式コードのみ（意味ラベルなし）
5. フラグ既定 OFF で探索不変
6. 全テスト緑・`src/audit/v0.33.7.md`・§30.1 更新

---

## 7. 次の微小ステップ

1. PE 閉ループ: タグ → Health / 文脈候補キュー（S4 手前の配線）
2. Predictive WM 短時間層と H3 接続
3. その後 S4 Importance（P-7 には戻らない）
