# S5 Intrinsic & Active Experiment 契約（内発価値 / 能動実験）

- 文書: `docs/s5-intrinsic-active-exp-contract.md`
- 拘束上位: `future-architecture.md` §12 Exploration/Active Experiment・§13 Intrinsic Value・§30 S5
- 版: S5-contract-0.1（2026-10-28 / v0.35.0）
- 対象 Stage: **S5**（価値ポートフォリオ + 仮説識別の契約面 + 薄いシェル。Explorer の一括置換はしない）

本契約は実装可能な短い仕様である。GT / Track しきい値 / P-7 / S6 Goal 再設計 / 学習報酬への診断ラベル注入は **変更しない**。

---

## 0. 用語

| 語 | 意味 |
|---|---|
| IntrinsicValuePortfolio | 形式的な内発成分の集合（ゲーム意味なし） |
| IntrinsicValueMixer | 候補にポートフォリオ Mixed 点を付ける（並行ログ or ソフト適用） |
| ActiveExperiment | 競合因果仮説を最もよく区別する Action の提案 |
| 実験予算 | 同一失敗ループ等の上限（本スライスはカウンタ・スタブ） |
| 学習の正 | Session → StructuredObservation → memory.sqlite（不変） |

原則: 馬鹿プレイ ≠ バグ。固定「正解キー」禁止。生の発見数を価値に直接しない。人間診断ラベルは価値に入れない。

---

## 1. ポートフォリオ成分（§13.1）

| ID | 内容（形式） | 本スライスの源 |
|---|---|---|
| `info_gain_pe` | 情報利得 / PE 減少代理 | CandidateScore InfoTerm+UncTerm、任意 ShortHorizon miss |
| `controllability` | Action が分布を動かす | Discovery+Leave+Change+Text（既存 Rates 分解） |
| `reach_unknown` | 到達拡大 / 未知遷移 | Novelty + Discovery |
| `reversibility_options` | 可逆・option 維持 | Reversibility |
| `skill_wm_long_term` | Skill/WM 長期改善 | **stub** `not_implemented`（S6/S7） |

初期重みは均等（合計 1）。**メタ重み学習は S7**（本スライスは寄与統計の記録のみ）。

---

## 2. Mixer

| 項目 | 規則 |
|---|---|
| 入力 | `Explorer.ScoreCandidates` の CandidateScore 列 + 任意 Causal/PE/ShortHorizon |
| 出力 | 候補ごと Mixed + 成分内訳。既存 ScoreBreakdown 式は **書き換えない** |
| モード | `parallel_log`（既定の ON 挙動） / `soft_experiment`（能動実験提案が強いときのみ） |
| 禁止 | 固定 ScoreBreakdown 係数を「正解価値」として置き換えたと偽ること |

---

## 3. Active Experiment（§12.2）

| 項目 | 規則 |
|---|---|
| トリガ | Causal `preferred=undecided` または H0/H1 が近い / PE が `representation_coarse` |
| 提案 | 候補のうち期待識別利得（代理）最大の Action。固定キー禁止 |
| 期待利得代理 | `Uncertainty × novelty_factor × ambiguity`（既存項の WRAP） |
| 適用 | フラグ ON かつ Human/Goal/Sequence 以外のとき、提案が安全なら `Rule=active_experiment` でソフト割込み |
| 予算 | `ActiveExperimentBudget` 手数上限（スタブ可）。枯渇時は提案のみ |

---

## 4. フラグ

| 変数 | 既定 | 効果 |
|---|---|---|
| `GKK_USE_INTRINSIC_MIXER=1` | **OFF** | Mixer 並行ログ +（条件付き）能動実験ソフト割込み |

OFF 時: 現行 Explorer.Choose と完全同一。Human Control は常に不変。

---

## 5. Health

ログ `intrinsic_mix` / `active_experiment` から件数・上位成分 Info（報酬に使わない）。

---

## 6. 非目標

- Explorer 全面 REBUILD / ScoreBreakdown 式の破壊的置換
- S6 Goal・S7 Outer メタ学習の本実装
- P-7・しきい値緩和・GT/Track 変更
- 永久観測削除・GitHub Release

---

## 7. 合格条件（v0.35.0）

1. 本契約書がある
2. Mixer が成分付き Mixed を出せる（並行）
3. ActiveExperiment が曖昧な因果時に提案できる
4. フラグ OFF で Choose 不変。Human 不変
5. `src/audit/v0.35.0.md`・§30.1・全テスト緑

---

## 8. 次の微小ステップ

1. Mixer 寄与統計の蓄積と診断 zip 強化
2. ソフト割込みの予算・安全条件の実機評価
3. その後 Skill/WM 長期成分・S6
