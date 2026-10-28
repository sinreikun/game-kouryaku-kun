# S6 Goal / Plan / Skill 契約（構造化目的・計画・技能）

- 文書: `docs/s6-goal-plan-skill-contract.md`
- 拘束上位: `future-architecture.md` §14 Goal・§15 Planning・§16 Skill・§30 S6
- 版: S6-contract-0.2（2026-10-28 / v0.36.1）— v0.36.0 シェル + Bundle 述語・WM lookahead・soft-adopt 予算で **S6 クローズ可**
- 対象 Stage: **S6**（構造化条件・マクロ Skill・合成計画。Goals/Sequences の一括破壊的置換はしない）

本契約は実装可能な短い仕様である。GT / Track しきい値 / P-7 / S7 Outer / 学習報酬への達成数注入 / GitHub Release は **変更しない**。

---

## 0. 用語

| 語 | 意味 |
|---|---|
| ObservationGoal | 観測言語の達成条件 + 期待学習/到達価値（外部意味禁止） |
| ObservationPredicate | 形式述語（Track 関係再現・数字系列変化試験・高不確かさ再試験・Frontier 到達） |
| SkillRecord | `{pre, body, post, effect_model+confidence, origin}` |
| StructuredPlanner | Skill マクロ展開 + PredictiveWmShortHorizon 効果チェック |
| SoftAdoptBudget | soft adopt の session/window/cooldown + 復旧ギャップ冷却 |
| 学習の正 | Session → StructuredObservation → memory.sqlite（不変） |
| State ID | **粗い索引のみ**。述語は Structured/Bundle を優先 |

原則: 馬鹿プレイ ≠ バグ。クリア/ボス/HP 等の外部意味 Goal 禁止。達成数を価値・報酬にしない。人間模倣報酬なし。浅い階層のみ。計画を実行区切りにしない。復旧観測前後を接着しない。

---

## 1. ObservationGoal（§14）

| 述語 ID | 証拠源（本スライス） | Status |
|---|---|---|
| `frontier_reach` | SelfGoal Reach WRAP | scored |
| `high_uncertainty_retest` | SelfGoal Retest WRAP | scored |
| `track_relation_reproduce` | Bundle.Relations / EntityExperience relation・Follows / Structured EntityLayer | scored when evidence; else not_enough_evidence |
| `number_series_change_test` | StructuredObservation.Text.Numbers（Series/Previous） | scored when numbers; else not_enough_evidence |

価値 = PE↓ / controllability / options 代理。達成カウント禁止。

---

## 2. SkillRecord（§16）

ActionSequence / Goal 区間を Skill 表面へ昇格（並び学習 KEEP）。浅い 1 層。human imitation 報酬なし。

---

## 3. StructuredPlanner（§15）

| 項目 | 規則 |
|---|---|
| マクロ | Skill body 展開 |
| WM | `PredictiveWmShortHorizon` / `LatestShortHorizon` で短時間効果チェック。`PredictionMiss` なら skill soft adopt 抑制。`wm_lookahead` = observed / partial / not_observed / unused（正直） |
| 失敗 | abandon/replan hint。実行区切りにしない。復旧ギャップ非接着 |
| 適用 | 並行ログ主。soft adopt はフラグ ON + SoftAdoptBudget |

### 3.1 SoftAdoptBudget

| 定数 | 値 | 根拠 |
|---|---|---|
| `DefaultMaxSoftAdopts` | 20 | セッション上限（S5 interrupt より控えめ） |
| `MaxSoftAdoptsPerWindow` | 2 | 窓内連打防止 |
| `WindowSteps` | 50 | 局所窓 |
| `CooldownSteps` | 10 | soft adopt 後冷却 |
| `RecoveryGapCooldownSteps` | 15 | 復旧後の非接着 |

ブロック理由: `human` / `budget` / `cooldown` / `window` / `recovery_gap`。Human Control 中は Goal soft adopt 経路に入らない。

---

## 4. フラグ

| 変数 | 既定 | 効果 |
|---|---|---|
| `GKK_USE_STRUCTURED_GOALS=1` | **OFF** | ObservationGoal/Skill/Plan 並行ログ + 条件付き soft adopt |

OFF: SelfGoals / ActionSequences 完全同一。

---

## 5. Health

`structured_goal` / `skill_record` / `plan_propose` → Area「Goal/Skill」。達成数は良し悪しに使わない。

---

## 6. 非目標

SelfGoals 破壊的 REBUILD・S7 Outer・P-7・GT/Track・深い入れ子 Skill・完全 WM 木探索・Release。

---

## 7. 合格条件（v0.36.1 / S6 クローズ）

1. 本契約 0.2
2. Track/Number 述語が証拠ありで scored
3. Planner が ShortHorizon を読み wm_lookahead を正直に出す
4. SoftAdoptBudget + Human/recovery-gap ガードとテスト
5. フラグ OFF 不変
6. `src/audit/v0.36.1.md`・§30.1 で **S6 クローズ可**・全テスト緑
7. 次 = **S7**（本タスクでは開始しない）

---

## 8. 実機ヒント

```
GKK_USE_STRUCTURED_GOALS=1
```

→ `structured_goal`（legacy + track/number）・`plan_propose`（wm=… / soft_block=…）・Health「Goal/Skill」。既定 OFF では v0.35.x と同一探索。
