# S5 Intrinsic & Active Experiment 契約（内発価値 / 能動実験）

- 文書: `docs/s5-intrinsic-active-exp-contract.md`
- 拘束上位: `future-architecture.md` §12 Exploration/Active Experiment・§13 Intrinsic Value・§30 S5
- 版: S5-contract-0.2（2026-10-28 / v0.35.1）— v0.35.0 シェル + 寄与統計・割込み予算で **S5 クローズ可**
- 対象 Stage: **S5**（価値ポートフォリオ + 仮説識別の契約面 + 薄いシェル。Explorer の一括置換はしない）

本契約は実装可能な短い仕様である。GT / Track しきい値 / P-7 / S6 Goal 再設計 / 学習報酬への診断ラベル注入は **変更しない**。

---

## 0. 用語

| 語 | 意味 |
|---|---|
| IntrinsicValuePortfolio | 形式的な内発成分の集合（ゲーム意味なし） |
| IntrinsicValueMixer | 候補にポートフォリオ Mixed 点を付ける（並行ログ or ソフト適用） |
| ActiveExperiment | 競合因果仮説を最もよく区別する Action の提案 |
| 実験予算 | セッション/窓/冷却の上限（スパム・同一失敗ループ防止。馬鹿プレイ禁止ではない） |
| IntrinsicContributionStats | 成分寄与・割込み前後タグの記録（S7 メタ重みの材料。本 Stage は学習しない） |
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
| モード | `parallel_log`（既定の ON 挙動） / `soft_experiment_available`（能動実験提案が強いとき） |
| 禁止 | 固定 ScoreBreakdown 係数を「正解価値」として置き換えたと偽ること |

---

## 3. Active Experiment（§12.2）とソフト割込み安全

| 項目 | 規則 |
|---|---|
| トリガ | Causal `preferred=undecided` または H0/H1 が近い / PE が `representation_coarse` |
| 提案 | 候補のうち期待識別利得（代理）最大の Action。固定キー禁止 |
| 期待利得代理 | `Uncertainty × novelty_factor × controllability`（既存項の WRAP） |
| 適用 | フラグ ON かつ **Human / Goal / Sequence mid-run 以外** かつ Rule が scored/fallback/momentum/chord_probe のとき、提案が安全なら `Rule=active_experiment` でソフト割込み |
| ガード | Human Control・明示 Goal 手・Sequence 残手中は **絶対に上書きしない**（呼び出し除外 + 内部二重ガード） |

### 3.1 予算・冷却（ライブ割込みポリシー）

`ActiveExperimentBudget`（`GKK_USE_INTRINSIC_MIXER=1` 時のみ効く）:

| 定数 | 値 | 根拠 |
|---|---|---|
| `DefaultMaxInterrupts` | 40 | 診断セッション規模のソフト割込み上限（§12.3） |
| `MaxInterruptsPerWindow` | 3 | 直近窓内の割込み上限（同一失敗ループの連打防止） |
| `WindowSteps` | 50 | 窓の手数（〜数十手の局所スパム検知） |
| `CooldownSteps` | 8 | 割込み後の冷却手数（連続上書き防止。Human/Goal とは独立） |
| `AftermathHorizonSteps` | 12 | 割込み後に因果/PE の変化を見る待ち手数 |

枯渇・冷却・窓超過時: Status=`blocked` / `budget_exhausted`。**提案ログは残す**が適用しない。馬鹿プレイ自体は Explorer 側で許容（予算は危険な同一失敗ループ／割込みスパム用）。

---

## 4. 寄与統計（S7 準備・学習しない）

| 項目 | 規則 |
|---|---|
| 記録 | 選ばれた候補の最大成分 1 票、成分合計、割込み前後の PE タグ、aftermath（ambiguity 落ち） |
| 永続 | フラグ ON 時 `profile/.../intrinsic-stats.sqlite`（memory.sqlite 非変更）。Sibling: pe-closed-loop / experience-vnext |
| Health | 「内発価値」に mix / active_experiment 件数 + 寄与・割込み Summary（表示のみ） |

---

## 5. フラグ

| 変数 | 既定 | 効果 |
|---|---|---|
| `GKK_USE_INTRINSIC_MIXER=1` | **OFF** | Mixer 並行ログ +（条件付き）能動実験ソフト割込み + stats 永続 |

OFF 時: 現行 Explorer.Choose と完全同一。Human Control は常に不変。

---

## 6. Health

ログ `intrinsic_mix` / `active_experiment` から件数・寄与・割込み Stats Info（報酬に使わない）。

---

## 7. 非目標

- Explorer 全面 REBUILD / ScoreBreakdown 式の破壊的置換
- S6 Goal・S7 Outer メタ学習の本実装（寄与統計は材料のみ）
- P-7・しきい値緩和・GT/Track 変更
- 永久観測削除・GitHub Release
- 学習重みの自動更新（S7）

---

## 8. 合格条件（v0.35.1 / S5 クローズ）

1. 本契約書（0.2）がある
2. Mixer が成分付き Mixed を出せる（並行）
3. ActiveExperiment が曖昧な因果時に提案でき、予算・冷却・ガードでスパム抑制できる
4. 寄与統計がメモリ内 + `intrinsic-stats.sqlite` に軽く残る（フラグ ON）
5. フラグ OFF で Choose 不変。Human / Goal / Sequence 不変
6. `src/audit/v0.35.1.md`・§30.1 で **S5 クローズ可**・全テスト緑
7. 次 Stage = **S6**（本タスクでは開始しない）

---

## 9. 実機ヒント

```
GKK_USE_INTRINSIC_MIXER=1
```

→ ログ `intrinsic_mix` / `active_experiment`（applied/blocked）、任意で `rule=active_experiment`、`intrinsic-stats.sqlite`、Health「内発価値」。既定 OFF では v0.34.x と同一探索。
