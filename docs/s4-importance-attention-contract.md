# S4 Importance & Attention 契約（特徴重要度 / 観測配分）

- 文書: `docs/s4-importance-attention-contract.md`
- 拘束上位: `future-architecture.md` §11 Feature Importance・§6.3 観測配分・§30 S4
- 版: S4-contract-0.1（2026-10-28 / v0.34.0）
- 対象 Stage: **S4**（重要度スコアと注意重みの契約面 + 薄いシェル。ライブ ROI 変更・S5 探索再設計は後続）

本契約は実装可能な短い仕様である。GT / Track しきい値 / Explorer / Goal / P-7 / 学習報酬は **変更しない**。

---

## 0. 用語

| 語 | 意味 |
|---|---|
| FeatureImportanceRecord | 特徴 f の重要度スコアと method タグ |
| AttentionWeights | 観測バケット（regions / tracks / ocr / residual）への配分重み |
| ROI policy surface | 高コスト処理への寄与を下げる助言面（本スライスは advisory） |
| PeClosedLoop 推奨 | 重要度に基づく Accept/Reject **推奨**（Status は pending のまま可。WM 自動拡大禁止） |
| 学習の正 | Session → StructuredObservation → memory.sqlite（不変） |

---

## 1. 重要度の定義（§11.1）

```
importance(f) ≈ 「f を知ると予測損失がどれだけ下がるか」
```

| method | 内容 | 本スライス |
|---|---|---|
| `pe_tag_proxy` | PE ルーティングタグからのオンライン代理 | **実装** |
| `context_helps_proxy` | ShortHorizon ContextHelps / H3 LogBf | **実装** |
| `local_surprise_proxy` | 局所驚き（mean surprise）の高低 | **実装** |
| `ablation_replay` | leave-one-out / ablation on replay | `not_implemented`（リプレイハーネス後続） |

**禁止:** 人間診断ラベル・ゲーム意味（「中央最大＝キャラ」等）を重要度や報酬に使うこと。

---

## 2. FeatureImportanceRecord

| フィールド | 説明 |
|---|---|
| `schema_version` | 現行 `1` |
| `feature_id` | 形式 ID（例: `pe:representation_coarse` / `ctx:local_context` / `bucket:regions`） |
| `score` | 0..1 目安（相対比較用。校正は後続） |
| `method` | 上表の method タグ |
| `status` | `scored` / `not_observed` / `not_implemented` |
| `evidence_summary` | 短い形式メモ（意味禁止） |
| `step` / `created_at` | 任意 |

---

## 3. AttentionWeights / ROI

| バケット | 意味（形式） |
|---|---|
| `regions` | 画素区画・変化場 |
| `tracks` | 追跡候補 |
| `ocr` | 文字・数字 |
| `residual` | 残差セル |

規則（§11.2・§6.3）:

1. **永久削除しない** — 重みを下げても観測自体は残す
2. 高コスト処理への寄与だけ下げる（本スライスは重み記録のみ）
3. **低頻度再確認の床**（`RareRecheckFloor`）を残す
4. Viewer 強調・診断に使ってよい。**報酬ラベルにしない**
5. ライブ capture / GT / Track アルゴリズムは **本スライスでは変えない**（advisory + ログ + Health + Bundle 任意付帯）

---

## 4. PeClosedLoop との接続

| 段階 | 規則 |
|---|---|
| 入力 | pending `PeFeatureCandidate` + CausalComparison / ShortHorizon |
| 出力 | `RecommendedStatus` = `accepted` \| `rejected` \| null、`Importance` / `ImportanceMethod` |
| Status | **自動で accepted にしない**（WM 条件付けの無制限拡大禁止。§9） |
| 閾値 | 推奨 Accept: score ≥ `RecommendAcceptMin`（既存 ln10 精神の代理 0.7）。Reject 推奨: score ≤ `RecommendRejectMax`（0.2） |

---

## 5. フラグ

| 変数 | 既定 | 効果 |
|---|---|---|
| `GKK_ATTACH_IMPORTANCE=1` | **OFF** | Session が重要度推定・注意重み・PE 推奨・診断ログを付ける |

OFF 時: 探索・学習・capture/GT/Track 不変。

---

## 6. Health

ログ `feature_importance` / `attention_weights` / `pe_importance_recommend` から件数 Info（学習報酬に使わない）。

---

## 7. 非目標

- P-7・しきい値緩和・GT/Track アルゴリズム変更
- Explorer / Intrinsic 再設計（S5）
- ライブ ROI で OCR/capture 頻度を変えること（後続マイクロステップ）
- ablation_replay 本実装
- GitHub Release / `latest.json`

---

## 8. 合格条件（v0.34.0）

1. 本契約書がある
2. `FeatureImportanceEstimator` が proxy method で scored レコードを出せる
3. AttentionWeights が床付きで出る（advisory）
4. PeClosedLoop に RecommendedStatus が付く（Status は pending のまま可）
5. フラグ既定 OFF・全テスト緑・`src/audit/v0.34.0.md`・§30.1 更新

---

## 9. 次の微小ステップ

1. ライブ ROI 任意接続（フラグ下・可逆）または S4 閉じて **S5**
2. ablation_replay（Experience vNext / diag replay）
3. Viewer 強調 UI
