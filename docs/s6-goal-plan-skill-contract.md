# S6 Goal / Plan / Skill 契約（構造化目的・計画・技能）

- 文書: `docs/s6-goal-plan-skill-contract.md`
- 拘束上位: `future-architecture.md` §14 Goal・§15 Planning・§16 Skill・§30 S6
- 版: S6-contract-0.1（2026-10-28 / v0.36.0）
- 対象 Stage: **S6**（構造化条件・マクロ Skill・合成計画の契約面 + 薄いシェル。Goals/Sequences の一括破壊的置換はしない）

本契約は実装可能な短い仕様である。GT / Track しきい値 / P-7 / S7 Outer / 学習報酬への達成数注入 / GitHub Release は **変更しない**。

---

## 0. 用語

| 語 | 意味 |
|---|---|
| ObservationGoal | 観測言語の達成条件 + 期待学習/到達価値（外部意味禁止） |
| ObservationPredicate | 形式述語（Track 関係再現・数字系列変化試験・高不確かさ再試験・Frontier 到達） |
| SkillRecord | `{pre, body, post, effect_model+confidence, origin}` |
| StructuredPlanner | Skill マクロ展開 or 短時間 WM lookahead（本スライスは薄いシェル） |
| 学習の正 | Session → StructuredObservation → memory.sqlite（不変） |
| State ID | **粗い索引のみ**。述語は Structured/Bundle を優先（本スライスは SelfGoal からの WRAP も可） |

原則: 馬鹿プレイ ≠ バグ。クリア/ボス/HP 等の外部意味 Goal 禁止。達成数を価値・報酬にしない。人間模倣報酬なし（human provenance は遷移観測のみ）。浅い階層のみ。

---

## 1. ObservationGoal（§14）

| 項目 | 規則 |
|---|---|
| 定義 | 達成条件（述語）+ 成功時期待価値（PE↓ / controllability / options 代理。達成カウントではない） |
| 述語 ID | `track_relation_reproduce` / `number_series_change_test` / `high_uncertainty_retest` / `frontier_reach` |
| 既存対応 | `SelfGoals` Reach → `frontier_reach`（粗い node 索引 WRAP）。Retest → `high_uncertainty_retest`。旧経路は削除しない |
| stub | Track 関係・数字系列は型と提案口のみ（証拠不足時 `not_enough_evidence`） |
| 禁止 | clear / boss / HP / 「賢く見える」外部意味 |

---

## 2. SkillRecord（§16）

| 項目 | 規則 |
|---|---|
| 形 | pre_condition / body（Action 列 or 方策断片）/ post_condition / effect_model（信頼度）/ origin |
| 採掘 | `SkillMiner` が `ActionSequenceCandidate`・Goal 区間を **新表面に昇格**（並び学習ロジックは既存 Miner を KEEP） |
| 階層 | 浅い 1 層のみ（入れ子 Skill 禁止・本 Stage） |
| 人間 | 模倣報酬なし。origin に human を書いても価値加点しない |

---

## 3. StructuredPlanner（§15）

| 項目 | 規則 |
|---|---|
| 入力 | 現行 ObservationGoal + SkillLibrary + 粗い cur node（任意 Bundle） |
| 出力 | PlanProposal（steps / 展開 Skill / status） |
| マクロ | Skill body を 1 マクロとして展開 |
| WM lookahead | **stub** `wm_lookahead=not_implemented`（短時間 Predictive WM 接続は後続） |
| 失敗 | replan / abandon を提案メモに載せる。計画を実行区切りにしない。Supervisor ギャップ前後を接着しない |
| 適用 | 既定は **並行ログ**。ライブ差し替えが危険なため、フラグ ON でも主経路は SelfGoals。任意 soft adopt のみ |

---

## 4. フラグ

| 変数 | 既定 | 効果 |
|---|---|---|
| `GKK_USE_STRUCTURED_GOALS=1` | **OFF** | ObservationGoal / Skill / Plan の並行提案ログ +（条件付き）Skill マクロ soft adopt |

OFF 時: 現行 `SelfGoals` / `ActionSequenceMiner` / `GoalStepOrNull` と完全同一。

Soft adopt（フラグ ON 時のみ）: Plan が既存 Active ActionSequence を第一手に提案し、SelfGoals の次手と異なるとき、その Sequence が user-allowed なら Goal 手を Sequence に差し替え（`Rule` は `goal` のまま Reason に `soft_adopt=skill_macro`）。Human / Sequence mid-run / Intrinsic active_experiment ガードは触らない。

---

## 5. Health

ログ `structured_goal` / `skill_record` / `plan_propose` から件数表示（Area「Goal/Skill」）。達成数を良し悪しに使わない。

---

## 6. 非目標

- SelfGoals / ActionSequences の破壊的 REBUILD（本スライスは WRAP + 新表面）
- S7 Outer・Champion・重み学習
- P-7・しきい値・GT/Track
- 深い入れ子 Skill・完全 WM 木探索
- GitHub Release

---

## 7. 合格条件（v0.36.0）

1. 本契約書がある
2. ObservationGoal / SkillRecord / StructuredPlanner シェルが動く（並行）
3. SkillMiner が ActionSequence から SkillRecord を作れる
4. フラグ OFF で SelfGoals 経路不変
5. `src/audit/v0.36.0.md`・§30.1・全テスト緑

---

## 8. 次の微小ステップ

1. Bundle/Entity 述語の本配線（Track・数字系列の証拠）
2. WM short-horizon lookahead を Plan に接続
3. soft adopt 予算と Supervisor ギャップ明示テスト → S6 クローズ準備
