# S7 Outer Self-Improvement 契約（Meta / Registry / Rollback）

- 文書: `docs/s7-outer-self-improvement-contract.md`
- 拘束上位: `future-architecture.md` §19 Meta-Learning・§20 Performance・§27 Rollback・§30 S7
- 版: S7-contract-0.1（2026-10-28 / v0.37.0）
- 対象 Stage: **S7**（Candidate 昇格・rollback・性能等価最適化の契約面 + 薄いシェル。Model Registry・Replay）

本契約は実装可能な短い仕様である。GT / Track しきい値緩和 / P-7 / 本番コード自己改変 / S8 / GitHub Release は **変更しない**。

---

## 0. 用語

| 語 | 意味 |
|---|---|
| Champion | 現在の推論に使う Model Bundle ポインタ |
| Candidate | 改善仮説から作った Bundle（未昇格） |
| ModelBundle | 版付きパラメータ/重み/ROI/モデル選択のセット（コード改変ではない） |
| OuterLoopEvaluator | 同一経験（または fixture）で Champion vs Candidate を比較 |
| Provenance | 昇格・棄却・rollback 理由の追記専用記録 |
| 学習の正 | Session → StructuredObservation → memory.sqlite（不変） |

原則: 「改善したつもり」禁止。Rollback 不能なら Outer loop を動かさない。Outer は既定でライブ入力を増やさない（replay/fixture 先）。S5 寄与統計・S4 PE Accept は仮説入力であり自動昇格しない。

---

## 1. スコープ（§19.3）

| 許可 | 禁止（本 Stage） |
|---|---|
| パラメータ・ポートフォリオ重み・ROI 方策・モデル選択 | 本番コードの自動書き換え |
| replay / 合成 fixture 上の比較 | しきい値緩和での「合格」 |
| Champion ポインタの原子的切替 + rollback | 比較なしの昇格 |

Candidate `kind`: `params` | `weights` | `roi` | `model_select` | `code_edit`（**常に reject / not_supported**）

---

## 2. Model Registry

| 項目 | 規則 |
|---|---|
| 永続 | profile 配下 `model-registry.sqlite`（memory / experience-vnext と分離。vNext `model_bundles` への Ensure は任意併記） |
| Champion | 単一ポインタ + `previous`（rollback 用） |
| Candidate | 仮説 JSON・status（proposed / compared / promoted / rejected / rolled_back） |
| 比較 | `comparisons` 行なしでは Promote 不可 |
| Rollback | previous が無い／壊れているとき Outer 評価を拒否（`rollback_unavailable`） |

---

## 3. OuterLoopEvaluator

| 項目 | 規則 |
|---|---|
| 入力 | Champion / Candidate の MetricSnapshot（PE 代理・calibration 代理・寄与統計・任意 fixture） |
| 出力 | CompareResult（winner / can_promote / reasons / provenance） |
| 優越 | Candidate が全主要指標で悪化せず、少なくとも 1 指標で明確改善（形式床） |
| 既定 | evaluate+log のみ。昇格は第二フラグ |

---

## 4. フラグ

| 変数 | 既定 | 効果 |
|---|---|---|
| `GKK_ENABLE_OUTER_LOOP=1` | **OFF** | Registry 読み・評価・ログ（昇格しない） |
| `GKK_OUTER_LOOP_PROMOTE=1` | **OFF** | 上記 ON かつ比較合格時のみ Promote 許可 |

両方 OFF: 現行探索・学習と完全同一。ライブ入力増なし。

---

## 5. Health

Area「Outer/Registry」: champion id・last compare・rollback available・promote 件数（表示のみ）。

---

## 6. 非目標

- 本番コード自己編集・S8 長期自律・P-7・GT/Track 緩和・Release
- ライブゲーム入力を増やす Outer 実験（既定禁止）
- S5/S4 の自動昇格

---

## 7. 合格条件（v0.37.0）

1. 本契約書がある
2. ModelRegistry が Champion/Candidate/compare/promote/rollback を記録できる
3. 比較なし Promote が失敗する
4. Rollback が Champion ポインタを復元する
5. `code_edit` Candidate は reject / not_supported
6. フラグ既定 OFF
7. `src/audit/v0.37.0.md`・§30.1・全テスト緑

---

## 8. 次の微小ステップ

1. Replay 実ログからの MetricSnapshot 抽出
2. Intrinsic portfolio 重み Candidate の安全な適用経路
3. Promote 予算・Champion 切替の実機診断 → S7 クローズ
