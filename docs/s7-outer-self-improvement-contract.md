# S7 Outer Self-Improvement 契約（Meta / Registry / Rollback）

- 文書: `docs/s7-outer-self-improvement-contract.md`
- 拘束上位: `future-architecture.md` §19 Meta-Learning・§20 Performance・§27 Rollback・§30 S7
- 版: S7-contract-0.2（2026-10-28 / v0.37.1）
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

## 3.1 Experience MetricSnapshot（v0.37.1）

| 項目 | 規則 |
|---|---|
| 同一スライス | Champion / Candidate は同じ PE 閉ループ・IntrinsicStats・因果タグから構築 |
| Method | `experience_v1`（十分な mix/PE 件数）/ `experience_partial_v1`（sparse・正直）/ `fixture_v1`（単体テスト） |
| PE 代理 | preferred=undecided 率 + representation_coarse 率（小さいほど良い） |
| Candidate PE | 同一スライス上の **counterfactual**（info_gain 重み増 × 割込み後 ambiguity 低下率）。ライブ入力は増やさない |
| 校正 | pending/enqueued 比率（両側同一） |

## 3.2 Weight apply（可逆）

| 項目 | 規則 |
|---|---|
| 対象 | Candidate `kind=weights` のみ。`code_edit` 拒否 |
| Promote | 第二フラグ ON かつ比較合格 → `PortfolioWeightState.ApplyFromBundle` 成功後に Champion ポインタ切替。失敗なら重み rollback |
| Rollback | レジストリ previous 復元 + 重み履歴 pop（対で実行） |
| Mixer | `IntrinsicValueMixer.Mix(..., PortfolioWeights: state.Current)`。既定フラグ OFF 時は Default 均等のまま |

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
- 本番コード自己編集

---

## 7. 合格条件（v0.37.1 / S7 クローズ）

1. 本契約書がある（experience MetricSnapshot + weight apply 記載）
2. ModelRegistry が Champion/Candidate/compare/promote/rollback を記録できる
3. 比較なし Promote が失敗する
4. Rollback が Champion ポインタ **と** ポートフォリオ重みを復元する
5. `code_edit` Candidate は reject / not_supported（重み適用も拒否）
6. フラグ既定 OFF → 探索・学習と同一
7. Session が `intrinsic_fixture_v1` に依存しない（experience / partial）
8. `src/audit/v0.37.1.md`・§30.1 **S7 クローズ可**・全テスト緑

---

## 8. 次の微小ステップ（S8 へ）

S7 クローズ後の次 Stage は **S8 Long-horizon Autonomy**（本契約の外。別着手）。
S7 残作業ではない: Promote 予算の実機長時間診断は S8 Health/成長スイートで扱う。
