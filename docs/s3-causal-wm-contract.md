# S3 Causal WM 契約（H0–H4 / PE ルーティング）

- 文書: `docs/s3-causal-wm-contract.md`
- 拘束上位: `future-architecture.md` §8 Temporal/Causality・§9 World Model・§10 Prediction Error・§30 S3
- 版: S3-contract-0.3（2026-10-28 / v0.33.9）
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
| **H3** 文脈条件付き | 同一 Action でも局所文脈で分岐 | `PredictiveWmShortHorizon` + `LocalEventModel.ContextHelps` / PredictContext。強い分岐は scored+LogBf、弱ければ partial/low_confidence（偽の確度禁止） |
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


---

## 4.1 PE 閉ループ（record→queue、v0.33.8）

「試算だけで終わらせない」最小実装: タグを **特徴候補キュー** に載せ、Health / 診断で件数を見せる。

| 要素 | 規則 |
|---|---|
| トリガ | `GKK_ATTACH_CAUSAL_WM=1` かつ `CausalComparisonRecord` 生成時 |
| カウンタ | タグ別・preferred(H0–H4/undecided) 別の累計 |
| 候補 | タグ 1 件につき `PeFeatureCandidate` 1 行（id, tag, evidence_json, created_at, status=pending\|accepted\|rejected, step） |
| 永続 | `{profileDataDir}/pe-closed-loop.sqlite`（**memory.sqlite 非変更**） |
| Accept/Reject | status 更新のみ。**WM 条件付けへの自動採用はしない**（§11 / S4） |
| Health | ログ `causal_wm` / `pe_feature_candidate` / `pe_closed_loop_summary` から件数 Info |
| 禁止 | ゲーム意味ラベル・「中央最大＝キャラ」をキュー/API に入れない。学習報酬にタグを使わない |

`reduces_with_history` / `reduces_nowhere` は稀でもキュー仕様は同じ（0 件でも Health は NotTested/Info）。


---

## 4.2 Predictive WM 短時間層 + H3（v0.33.9）

| 要素 | 規則 |
|---|---|
| 表面 | `PredictiveWmShortHorizon`（`predictive_wm_short_horizon@1`） |
| WRAP | `LocalEventModel.Predict` / `PredictContext` / `ContextHelps`。任意で `EntityExperience` ヒット率ヒント。TransitionModel は任意（本配線は局所主） |
| 出力 | 要因ごと P（move / appear-disappear / text-number 等の形式名）、mean surprise、prediction_miss、context_helps 件数 |
| H3 | 源なし → `not_observed`。ContextHelps≥1 → scored + LogBf≥ln10。文脈差のみ → scored partial（LogBf null）。**偽の確度で preferred しない** |
| PE | prediction_miss かつ context_helps>0 → `reduces_with_history`；miss のみ → `representation_coarse`（既存と OR）。`reduces_nowhere` は仍スタブ |
| フラグ | `GKK_ATTACH_CAUSAL_WM=1` 時のみ Session が Forecast→Compare。既定 OFF で探索不変 |
| 禁止 | 意味ラベル・しきい値変更・Explorer/Goal/GT 変更・学習報酬への PE 注入 |

## 5. 非目標

- P-7・しきい値緩和・GT/Track/EE アルゴリズム変更
- Explorer / Goal / Skill 再設計（S5/S6）
- 特徴重要度の本実装（S4）
- 能動実験の実行（S5）
- 深層／中長時間 Predictive WM の完成（本スライスは短時間 WRAP + H3）
- GitHub Release / `latest.json`

---

## 6. 合格条件（v0.33.9 スライス）

1. `PredictiveWmShortHorizon` が既存 LocalEvent 予測を WRAP して要因 P を返す
2. H3 が `not_implemented` から脱出（scored / not_observed / partial）。強い ContextHelps のみ preferred 可
3. prediction_miss → PE タグが PeClosedLoop に載る（フラグ ON）
4. フラグ既定 OFF で探索不変。中長時間 WM / S4 / S5 は非目標
5. `src/audit/v0.33.9.md`・§30.1 更新・全テスト緑

---

## 7. 次の微小ステップ

1. **S4** Importance（候補 Accept 時の文脈特徴試験。P-7 には戻らない）
2. 中時間 Predictive WM（旧 TransitionModel 内包）は後続
3. H4 本実装・能動実験（S5）は後続
