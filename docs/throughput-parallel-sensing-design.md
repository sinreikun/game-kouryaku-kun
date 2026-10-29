# 高速経験収集・入力無関係変化の分離・重複回避・並列観測器 設計（案）

- 起点: ユーザー指示 t117u（2026-10-29）
- 対象版: v0.38.0 で計測 → v0.39.0〜v0.39.5 で T0〜T5 を実装（すべてフラグ既定 OFF）
- 改訂: t118u（2026-10-29）で必須条件 C1（非同期でも因果系列を失わない）・C2（重複削減と Raw の再解釈可能性の分離）を追加。§2.2・§4・§5・§6 を改訂し §7〜§9 を追加
- 計測元: real-machine-archive/v0.38.0-20261028（app-20261028-212635.jsonl, health.txt）探索 #159

## 0. 守る原則（変更しない）
ゼロ知識／画面に見える情報は隠さない／目と手は開発者・意味は経験／下手なプレイはバグではない／Profile=身体のみ／人間ラベルを AI に入れない／GT・Track 等のしきい値を緩めない／自己コード書き換えなし／P-7 に戻さない／Observation≠Meaning・時間的≠因果。
新機能はすべてフラグ既定 OFF。OFF 時は v0.38.0 と同一経路（回帰テストで保証）。

## 1. 現状の計測（#159: 458 手・607.6 s）

**スループット: 45.2 手/分**（458/607.6s）。手と手の間隔 中央値 1282 ms・平均 1310・p90 1717・最大 3226。

| 区間（ログ上の根拠） | 中央値 | 平均 | p90 | 最大 |
|---|---|---|---|---|
| 決定 `decision_scores` → `turn` 確定（入力送出＋静止待ち） | 706 | 733 | 900 | 2628 |
| うち静止判定 `boundary_ms`（TurnDetector / AdaptiveTurnTimeout） | 604 | 609 | 767 | 2426 |
| うち入力送出・押下保持など（差分） | ~100 | ~124 | | |
| `turn` → `observation`（OCR＋観測組立） | 549 | 525 | 787 | 1169 |
| うち OCR `ocr_step.ms`（full 139 / region 314） | 473 | 460 | 711 | 1034 |
| うち観測組立・GT・Entity 等（差分） | ~76 | ~65 | | |
| `observation` → 次の `decision_scores`（DB 書込・vNext dual-append・決定） | 41 | 52 | 83 | 569 |

補足: 静止判定まで受信フレーム 中央値 25（WGC 約 49 fps）。no-change 待ちは自己調整済みで 600 ms（調整 1 回）。
Health「読み取り中の手 0」= OCR は**毎手直列で完了を待ってから次の手**に進んでいる。

### ボトルネック上位 3
1. **静止待ち 604 ms/手（約 47%）** — 固定的な「変化なし 600 ms」待ちが下限を作る。変化が即座に止まる手でも 600 ms 待つ。
2. **OCR 直列 473 ms/手（約 37%）** — 解析が次の入力をブロック。読む面積平均 0.751 と広い。
3. **重複収集** — 458 手中 状態ノード 19 種、(状態,手) の組 208 種。**約 55% の手が既出の (状態,手) の再試行**。手数あたりの新情報が少ない（時間ではなく情報の無駄）。
（その他: 入力送出 ~100 ms、組立 ~76 ms、DB/決定 ~41 ms は小さい。）

## 2. 高速化設計（正しさを落とさない）

### 2.1 観測に基づく適応的な静止判定（ASW: Adaptive Settle Wait）
- 現状: 変化なし待ち（自己調整後 600 ms）＋ StableMs 250 ＋ Max 8000。
- 案: 画面ごと・手の種類ごとに「入力から変化開始までの遅延」「変化継続時間」の経験分布（経験から学ぶ、ゲーム知識なし）を持ち、
  - 変化が始まって止まった場合: **静止確認は StableMs（250）を維持**（緩めない）。
  - 変化が始まらない場合: no-change 待ちを経験分布の上位分位（例 p99 × 安全係数 1.5、下限 250）に。分布が十分（n≥20）になるまで現行値。
  - 後から変化（`late_change` 7 件）が観測されたら、その画面の待ちを即座に戻す（安全側に自己修正）。late_change 率を Health に出し WARN 判定。
- 期待: 静止待ち 604 → 約 300–400 ms。

### 2.2 パイプライン化（非同期 Observation 契約。§7 C1 に従う）
- 静止確定フレームを不変スナップショットとして確保 → OCR を非同期に開始し、**完了を待たずに次の手の決定・入力・静止待ちへ進む**。
- 次の手の時点で前の手の OCR が未完なら、その手の観測は **pending**（欠損ではない）。決定は確定済みの層で行い、`decided_with_pending` として数える。
- OCR が後の手の実行後に完了しても、結果は **元の Frame ID・Action（seq / step）・取得時刻** に紐づけて元の時間軸へ合流（late-amend）。因果推定・予測誤差は**取得時刻**に帰属し、完了時刻には帰属しない。
- 前の手の観測の完成は、次の手の OCR を始める前に必ず待つ（前後の文字の差分の順序を保つ）。実行の終わりにも必ず回収（drain）。
- ~~「未完は unknown として扱い、推測で埋めない」~~ → **pending→late-amend に置き換え**（t118u）。deadline 超過は pending/late。一度も作られなかったものだけ missing。
- 世界予測（`_lastWorldPred`）は観測を開始した時点の値を観測に渡す（後の手の予測で上書きされて予測誤差の対応が壊れないように）。
### 2.3 目標
直列合計 ~1280 ms → 約 650–800 ms、**75–90 手/分**（1.7–2 倍）。

## 3. 入力と無関係な変化の分離（ノイズ／明滅／アニメ）
- 既存: 入力なし基準（WARN: 基準のある画面 6/10）、motion_mask、待つ手、手によらない文字変化 190.5/317。
- 案: 画面ごとの **自然変化モデル**（観測器レベル、意味づけなし）
  - 画素ジッタ: セル単位の差分分布（入力なし区間）→ 許容帯。
  - 模様の揺らぎ／周期アニメ: セルごとの変化周期（自己相関）と位相。予測可能な変化は「自然変化」として分離。
  - 区別は**削除ではなく注釈**: 生の観測は残し（画面の情報を隠さない）、経験への帰属時に「入力起因候補」と「自然変化候補」を分ける。時間的に後＝原因とはしない。
- 基準不足の解消: 待つ手を「基準が無い画面」で優先する（固定確率で増やさず、既存の情報価値で）。6/10 → 目標 ≥9/10。
- しきい値（GT・Track）は変えない。分離は新しい層で前段に足すだけ。

## 4. 重複回避（§8 C2: Raw と Aggregate を分離）
- **Raw Experience / Event Stream**（append-only・潰さない）: 1 手 1 行。turn_id・seq・step・frame ID・取得時刻・手・前後の node・静止判定の根拠（first_change / settled / frames / peak・final ratio）・OCR の layout/text hash・表現版（representation_version）・保持階層（tier）。memory.sqlite の turn（縮小画像 Downscale(4) を既に保持）への参照を持つ。
- **Aggregated Experience / Index**: 現在の表現（representation_version）で同一と判断した (状態シグネチャ, 手, 結果シグネチャ) を 1 行に集約し count・最初/最後の seq・結果分布を持つ。学習・検索用。
- **再集約**: 新しい観測器・特徴が入ったら、Raw をリプレイして新しいシグネチャ関数で Aggregate を作り直す（別の representation_version として並存。古い集約は消さない）。
- 状態シグネチャ（v1）: node（m..|..）＋OCR layout hash（あれば）。自然変化の除外は T3 の注釈を使う将来の v2。
- 選択への反映（novelty 飽和）は本段では入れない（収集の偏りを先に計測。プレイの上手さは合否にしない）。
- 指標: 重複率 = 既出 (sig, 手, 結果) の手 / 全手、新規 (sig, 手, 結果)/分、Raw 保持率。
## 5. 並列観測器と監督機構へのバス
- 観測器: FrameDiff（静止フレーム対基準）・OCR・自然変化注釈・turn（静止判定）。GT/Entity は既存の観測組立の中（しきい値不変）。音は現状なし（口だけ）。
- 共有バス `ObserverBus`: すべての結果は `SensorResult`（§7 の必須項目）として投入。単調時計で取得時刻と処理開始/完了を持つ。
- 背圧: 観測器ごとに容量制限。満杯時に間引けるのは **静止確定でない中間フレームの解析だけ**（`coalesced` として件数と元 frame ID を残す）。静止確定（settle）・基準（pre）フレームの結果は落とさない。
- deadline: 観測器ごと（OCR 1000 ms・FrameDiff 200 ms・注釈 200 ms）。超過は **late**（落とさない・後から合流）。
- 順序: 合流は (step, seq, sensor) で整列。完了順ではなく取得時刻順に並べ直して Supervisor へ。同じ観測器で取得時刻が逆転して届いた件数を `reordered` として数える。
- Supervisor: 観測器ごとの遅延 p50/p90・late・coalesced・queue 最大・対応違反を Health に出す。意味づけはしない。
## 6. 段階的実装（全フラグ既定 OFF・OFF 時は v0.38.0 と同一経路）
| 段 | 版 | 内容 | フラグ（環境変数=1） | 成功基準（すべて FAIL 0） |
|---|---|---|---|---|
| T0 | v0.39.0 | 区間計測 `step_timing`（決定/送出/静止/記録/観測待ち/後処理） | `GKK_STEP_TIMING` | 区間合計が手の間隔と ±5% |
| T1 | v0.39.1 | 非同期 Observation 契約＋OCR パイプライン＋`ObservationLedger` | `GKK_PIPELINE_SENSING` | ≥60 手/分、対応違反 0、pending 未解決率 0%、late-amend 成功率 100% |
| T2 | v0.39.2 | 画面ごとの適応的静止判定（no-change 待ちのみ。StableMs・差分基準不変） | `GKK_ADAPTIVE_SETTLE` | 静止待ち中央値 ≤400 ms、late_change 率 ≤ 5%（既存 WARN 基準）かつ v0.38 実測 1.5% を大きく超えない |
| T3 | v0.39.3 | 自然変化モデル（セルごとの揺れ・周期。注釈のみ） | `GKK_NATURAL_NOISE_MODEL` | 注釈率・入力起因候補率を表示、学習経路不変 |
| T4 | v0.39.4 | Raw/Aggregate 分離ストア `experience-raw-agg.sqlite`・再集約 | `GKK_RAW_AGG_SPLIT` | Raw 保持率 100%、重複率表示、再集約で件数一致 |
| T5 | v0.39.5 | 並列観測器バス→Supervisor | `GKK_PARALLEL_OBSERVERS` | 75–90 手/分（T1+T2 併用時）、settle 結果の間引き 0、deadline 超過は late として 100% 合流 |

最初の実機確認は T0 単独 → T0+T1 → T0+T1+T2 → 全部、の順で比較する。

## 7. 必須条件 C1: 非同期 Sensor でも因果系列を失わない
### 7.1 SensorResult（全観測器の結果の必須項目）
| 項目 | 意味 |
|---|---|
| `frame_id` | 元 Frame / Observation ID |
| `capture_ms` | フレームの取得時刻（単調時計） |
| `step` / `seq` | 対応する Action（この実行の手番号・学習 DB の seq） |
| `position` | Action に対するフレームの位置: `pre`（入力前の基準）/ `post`（入力後の途中）/ `settle`（静止確定）/ `gap`（手と手の間の入力なし区間） |
| `sensor` | 観測器の種別（ocr / frame_diff / noise / turn …） |
| `start_ms` / `finish_ms` | 処理開始・完了（単調時計） |
| `provenance` | `observed`（その手の決定前に完成）/ `inherited`（変化なしで前の結果を継承）/ `pending`（未完）/ `late`（後の手の実行後に完成し元の時間軸へ追補）/ `missing`（一度も作られなかった） |
### 7.2 契約
1. 1 Action = 1 Complete Observation を捨てず **「1 Action = 1 Observation（後から完成可）」** に拡張。Action 時点では一部 pending を許す。
2. 結果の帰属は常に `capture_ms` と `step/seq/frame_id`。`finish_ms` は遅延の計測にだけ使う。
3. 台帳 `ObservationLedger` が (step, sensor) を開き、結果の frame_id/seq が開いた時と一致しなければ **対応違反**（Health FAIL）。
4. deadline 超過は late（合流待ち）。実行終了時に未完のものは drain で回収。回収後も残るもの＝`pending_unresolved`（WARN>0）。例外で作られなかったものだけ missing。
5. 「OCR of frame F123 captured after action A1」= `sensor_result{sensor:ocr, frame_id:123, step:1, position:settle, provenance:late}` として記録され、観測（`observation` / memory の observation 行）は元の step・seq で保存される。

## 8. 必須条件 C2: 重複削減と Raw の再解釈可能性の分離
- §4 のとおり Raw（append-only）と Aggregate（表現版ごと）を分離。Aggregate は Raw からいつでも再生成可能で、Aggregate の削除・作り直しで情報は失われない。
- ディスク対策の選択肢と失う再解釈能力（本版は Raw 全件保持・縮小画像は既存 memory 側。対策は未実施で明示のみ）:

| 方式 | 容量 | 失う再解釈能力 |
|---|---|---|
| 全フレーム原寸保存 | 最大 | なし |
| 静止確定フレームのみ原寸（現状に近い: memory は Downscale(4)） | 中 | 中間フレーム（アニメの途中・最初の変化の形）。細かい文字の再 OCR は縮小で不可 |
| keyframe＋差分ブロック | 中〜小 | 差分しきい値未満の微小変化（後の観測器が微小変化を使うと再解析不能） |
| 可逆圧縮（PNG/zstd） | 小 | なし（CPU のみ） |
| 非可逆圧縮 | 小 | 画素ジッタ・模様の揺れのモデル化（T3 の再学習）が不正確に |
| 保持階層（hot 全件 → warm keyframe → cold 特徴のみ） | 最小 | cold 化した経験は新しい画素特徴での再解析不能（記号層の再集約のみ可） |
| 重要経験の長期保存（新規 sig・驚き大・予測外れ・late_change） | ― | 重要でないと判定された経験の画素根拠（判定は現在の表現に依存する点を明示） |

## 9. 真の throughput 指標と回帰
- **CIO/min（因果保持観測/分）**: 対応違反なく、complete（observed/inherited）または late-amend 済みの観測数 / 分。主指標。
- **Raw 保持率**: Raw 行数 / 手数（目標 100%）。
- **late-amend 成功率**: late で合流できた件数 / late 件数（目標 100%）。
- **pending 未解決率**: drain 後も pending / 手数（目標 0）。
- **decided_with_pending 率**: 前の観測が未完のまま決めた手の割合（情報量の代償として表示）。
- 参考: actions/min、重複率、noise 注釈率、late_change 率。
- 回帰（テストで固定）: (a) フラグ OFF で各部品が既存経路と同値、(b) 予測: late でも観測に渡す世界予測は開始時の値、(c) 因果: 帰属は capture 時刻、(d) Replay: Raw から Aggregate を再生成して件数一致、(e) 継続学習: memory への CommitTurn は不変、(f) Outer Self-Improvement: 既存 S7 の評価入力（ログ・DB）を削らない。Prediction/Causality/Replay/Continual Learning/Outer の情報品質を落とす変更は採用しない。

## 10. 実装状況（v0.39.5 時点）
| 段 | 版 | 主な部品 | ログ／Health |
|---|---|---|---|
| T0 | v0.39.0 | `Core/Throughput/StepTiming`・`ThroughputFlags` | `step_timing`・`step_timing_summary`／「手の速さ」 |
| T1 | v0.39.1 | `SensorResult`・`ObservationLedger`・`ThroughputMetrics`・Session の pending/drain・世界予測の固定 | `sensor_result`・`ledger_summary`／対応違反 FAIL・未解決 WARN・真の throughput |
| T2 | v0.39.2 | `AdaptiveSettle`（no-change のみ・遅れた変化で戻す＋下限を上げる） | `adaptive_settle_summary`／早すぎた割合 >5% WARN |
| T3 | v0.39.3 | `NaturalNoiseModel`（区画の率・周期。注釈のみ） | `noise_annotation`・`noise_summary` |
| T4 | v0.39.4 | `RawExperience`・`AggregateIndex`・`RawAggStore`（experience-raw-agg.sqlite） | `raw_agg_summary`／リプレイ不一致 FAIL |
| T5 | v0.39.5 | `ObserverBus`（frame_diff・noise・noise_gap・ocr） | `observer_bus`／静止確定の間引き FAIL |

回帰テスト: `ThroughputRegressionTests`（OFF 時の 1 手 1 完全観測・観測の順序・予測の固定・CommitTurn 不変・適応は no-change だけ・ノイズは注釈のみ）と `ThroughputT0〜T5Tests`。

## 11. 高速化比較の自動実行（v0.40.0・t119u）
- 利用者の操作: 通常の `ゲーム攻略くん.exe` を起動し、ボタン **「高速化比較を自動実行（約35分）」** を 1 回押すだけ（未接続なら自動で接続）。環境変数・複数回の実行は不要。
- 区間: A 基準（すべて OFF。区間計測と台帳の記帳だけ＝挙動不変）／B ＋パイプライン／C ＋適応的静止判定／D 6 フラグすべて。各 4 分、順序 ABCD→DCBA。
- 順序の理由: 学習が進むと画面が既知になり速さ・新しさが時間で変わる。ABBA 型では各設定の平均の実行位置が同じ（1+8＝2+7＝3+6＝4+5）なので一次のドリフトが差から消える。比較は同じブロックの A と対にし、両ブロックで同じ向きのときだけ採用。
- 安全: 区間中に対応違反・静止確定の間引きが出たらその場で打ち切り、その設定の残りは A に置換して記録。経験はすべての区間で memory に記録し、`throughput_segment`（start/end/abort・label・run_id）で出どころを残す。
- 出力: データフォルダ `throughput-comparison/comparison-<時刻>.md/.json` → 自動で診断 zip に同梱して書き出し、クリップボードへコピー。
- 判定: 採用（両ブロックで因果保持観測/分 +10% 以上・品質の後退なし）／不採用（FAIL または品質の後退: 記録・観測の保存・判断の記録・予測との照合が 5pt 以上低下、予測の当たりが 2SE 超低下、文字の対の低下、対応違反・未解決 pending・Raw 再集約不一致）／保留。**助言のみで既定は変えない。**

## §12 手ごとに交互の比較（v0.41.0）
- 実機 v0.40.2: 4 分の区間ごとに設定を変えると、訪れる画面が設定ごとに違い（D は m48・A は m44/m1/m42）、同じ (画面, 手) の照合が 0 件になった。
- 1 回の探索（32 分）の中で、(画面, 手の種類) ごとに A/B/C/D を並べ替えたブロックで手ごとに割り当てる（固定の種）。各手に設定のラベル（interleave_step）。
- 切り替えの安全: 前の手の pending の観測は次の手の観測の前に必ず待つ（従来どおり）。残りの待ちは観測を出した手の設定の時間に付け替える。並列の観測器を外す手の前に Bus を drain。適応的静止判定・入力前の間は、その手の設定が使うときだけ効く（学習は全手の観測から。観測そのものは設定に依らない事実）。
- FAIL（対応違反・静止確定の間引き）は、その手と未合流だった前の手の設定を割り当てから外す。A だけで起きたら従来どおり打ち切り。
- 判定: 同じ (画面, 手) で重み付けた因果保持観測/分が前半・後半とも +10% 以上、かつ 1 手の時間・変化なし・予測の当たり・因果保持の割合・前半・後半のすべてで実効 30 件以上、後退なし（変化なし +5pt かつ 2SE 超・予測 2SE 超の低下・因果保持 5pt かつ 2SE 超の低下）のときだけ採用。助言のみ・既定は OFF のまま。
