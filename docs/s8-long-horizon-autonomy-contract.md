# S8 Long-horizon Autonomy 契約（Health / 安全 / 成長スイート）

- 文書: `docs/s8-long-horizon-autonomy-contract.md`
- 拘束上位: `future-architecture.md` §22 Health・§28 Evaluation・§29 長時間安全・§30 S8
- 版: S8-contract-0.1（2026-10-28 / v0.38.0）
- 対象 Stage: **S8**（長時間で理解・到達・計算効率が同時に改善する **観測・診断スイート** の完成。探索式の改変ではない）

本契約は実装可能な短い仕様である。GT / Track しきい値緩和 / P-7 / 本番コード自己改変 / GitHub Release は **変更しない**。
「トルネコをクリア」は成功条件にしない。馬鹿プレイ ≠ FAIL。学習停滞メトリクスだけをチケットにする。

---

## 0. 用語

| 語 | 意味 |
|---|---|
| LongHorizonHealth | S3–S7 既存メトリクスを横断集約した長期ダッシュボード |
| GrowthSuite | 起動またぎの Champion 非回帰・PE 傾向・到達/Skill 再利用（表示のみ・報酬にしない） |
| EvalSuiteHooks | 合成 / Replay / 長期運転 / 原則回帰の入口（薄いランナーまたは文書化された呼び出し点） |
| SafetyChecklist | §29 を機械判定可能な WARN/FAIL に写したもの（ゲーム意味禁止） |

原則: 既存 Supervisor / ActiveExperimentBudget / SoftAdoptBudget / LiveRoi / Outer 二重フラグ / GrowthProbe / Health S3–S7 を **KEEP/拡張**。探索・学習経路はフラグ OFF で完全同一。

---

## 1. 統一 Health ダッシュボード（Area「長期自律」）

| サブ | 見る | 見ない |
|---|---|---|
| システム | Supervisor 復旧件数・回路遮断・欠測（既存）への参照要約 | 攻略の良し悪し |
| 学習 | PE 閉ループ件数・undecided/coarse 率・Intrinsic mix/interrupt | クリア進捗 |
| Outer | Champion id・rollback 可否・promote/compare 件数 | しきい値緩和 |
| 安全予算 | 実験割込み予算逼迫・soft-adopt 逼迫・Outer ライブ入力増なし | 「セーブ破壊」意味ラベル |
| 成長 | GrowthEvaluator + Skill 再利用・状態広がり（INFO） | 数の多さ＝合格 |

---

## 2. §29 SafetyChecklist（機械判定）

| # | 項目 | 判定 |
|---|---|---|
| 1 | Supervisor 生存経路 | 既存 Health Supervisor（参照） |
| 2 | 禁止キー / 入力ガード | 既存（参照）・本スイートは「ガード経路存在」INFO |
| 3 | 実験予算 | ActiveExperimentBudget 逼迫率 → WARN（予算尽きただけの馬鹿プレイは FAIL にしない） |
| 4 | 不可逆らしき遷移 | 統計スタブ: option 消滅代理が無いときは `not_tested`（正直） |
| 5 | 資源上限 | OCR/ディスク要約が無ければ `not_tested`；LiveRoi defer 件数があれば INFO |
| 6 | Human 緊急停止 | Human Control スイッチ件数 INFO（既存ログ） |
| 7 | Outer 既定でライブ入力増禁止 | Outer フラグ OFF または evaluate-only → Ok；promote ON は INFO（入力増は依然禁止） |
| 8 | セーブ非操作 | エージェントがセーブ API を持たないこと（静的/契約 INFO） |

---

## 3. GrowthSuite

| 指標 | 規則 |
|---|---|
| Champion 非回帰 | model-registry: rollback 可かつ直近 promote 後の compare 悪化ログがあれば WARN |
| PE 傾向 | pe-closed-loop / outer_compare の pe_proxy 時系列があれば傾向 INFO；無ければ NotTested |
| Reach / Skill | GrowthSnapshot StatesRun・SkillsActive / SkillUses（表示のみ） |
| 停滞チケット | 同分布で PE 改善なし・Outer 停止中に Candidate 常敗 → WARN（§22.2） |

---

## 4. EvalSuiteHooks（§28.2）

| スイート | 入口 |
|---|---|
| 合成 | `EvalSuiteHooks.SyntheticEntry` → 既存 Core.Tests の Causal/Importance/Intrinsic/Goals/Outer |
| Replay | `EvalSuiteHooks.ReplayEntry` → 診断 zip / Adapter Replay（非破壊・文書化） |
| 長期運転 | `EvalSuiteHooks.LongRunEntry` → GrowthEvaluator + LongHorizonHealth.Build |
| 原則回帰 | `EvalSuiteHooks.PrincipleRegressionEntry` → ConstraintAudit / ForbiddenApi テスト名列挙 |

薄いランナーはプロセスを起動せず、呼び出し可能な静的 API + テストが契約を固定する。

---

## 5. フラグ

| 変数 | 既定 | 効果 |
|---|---|---|
| `GKK_LONG_HORIZON_SUITE=1` | **OFF** | Session/AppEnv で追加の `long_horizon_*` ログ。Health 集約自体はデータがあれば常時表示可 |

OFF: 探索・学習・入力と完全同一。

---

## 6. 非目標

- P-7 / GT/Track 緩和 / 本番コード自己編集 / Release 公開
- 「トルネコをクリア」を成功にすること
- Outer のライブ入力増実験
- 馬鹿プレイのログを修正チケットにすること

---

## 7. 合格条件（v0.38.0 / S8 クローズ）

1. 本契約書がある
2. `LongHorizonHealth` / `GrowthSuite` が既存ストア・ログから集約できる
3. §29 SafetyChecklist が機械判定（不足は not_tested）
4. EvalSuiteHooks が 4 入口を文書化・テスト固定
5. フラグ既定 OFF
6. `src/audit/v0.38.0.md`・§30.1 **S8 クローズ可**・全テスト緑

---

## 8. Capability roadmap

S0–S8 契約+シェル完了。**Release / 実機長時間診断は親判断**（本 Stage は公開しない）。
