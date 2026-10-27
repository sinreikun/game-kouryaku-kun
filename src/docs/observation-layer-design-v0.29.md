# Observation Layer 最終構造の設計（v0.29.0〜。拘束仕様: observation-layer-spec.txt）

原則: 目と手は開発者が作る。見たものの意味は攻略くんが経験から推理する。意味のラベル（プレイヤー・敵・HP・店など）は持たない。

## 1. Structured Observation（schema 1）
1 手 = 1 経験: Observation_before → Action → Observation_after。各層は必ず状態を持つ:
- `observed`（この手で測った）・`inherited`（画素が同じ区画の前の結果を引き継いだ。測っていないのではなく、変わっていないことを測った）・`not_observed`（取得に失敗）・`not_implemented`（その層はまだない）。

| 層 | 中身 | 出どころ（既存の部品） | ブロック |
|---|---|---|---|
| global | node（状態名）・mode（色の分布 64 ビンの群）・layout（矩形の配置）・visual_state（8×6 の群）・fg_text_layout（前景の鍵） | ScreenModeBook・VisualStateBook・ForegroundStates | 3 |
| regions | 16×12 の画素区画の変化・変化した区画のつながり（4 近傍）= 変化領域・追跡 ID（前の手の領域との重なり）・区画ごとの因果の分け方（action/independent/unknown）と入力なしの割合 | TextReader.Cells・RegionCausality | 4 |
| entities | 局所パターンの候補（ID・位置・大きさ・局所特徴・見た目の状態・移動・出現消失・追跡の確信度） | （未実装） | 5〜7 |
| relations | 候補どうしの相対位置・距離・接近離反・隣接・同方向移動 | （未実装） | 8 |
| text | 行（文字・矩形・確信度・配置の区画・observed/inherited・前の手との差 same/new/changed/gone）・差の種類・数字の系列（区画ごと） | TextReader・TextDiff・TextChangeBook | 2・9 |
| temporal | 区切りまでの時間・画面が変わったか・入力なしの割合の推定・手が起こした文字の割合 | TurnDetector・NaturalBaseline・RegionCausality | 4・10 |
| provenance | agent / human / wait・schema_version・app_version | DecisionRecord と同じ | 1 |

## 2. 既存の部品との接続
- State ID（node）は索引・圧縮として残す（global.node）。World Model（TransitionModel）は当面 node で動き、Structured Observation は記録と局所の予測（ブロック 11）の入力になる。
- 区画の因果（v0.28.0）・入力なしの基準（v0.27）・文字の変化（v0.27.2〜v0.28.2）はそのまま使い、Structured Observation に値を写す。
- Human Control の手も同じ形で記録（provenance=human）。模倣はしない。

## 3. DB（memory.sqlite schema v17）
- `structured_observations(obs_id, run_id, seq, step, schema_version, provenance, json, created_at)`。
- 旧経験（v16 以前の手）には行がない＝legacy coarse observation。推測で埋めない。`schema_meta.observation_schema` で現在の形の版を持つ。

## 4. 性能（1 Action = 1 Complete Observation）
- 文字は毎手、次の手を決める前に読み終える（v0.29.0 から手の流れを止めて待つ。読み取り中で読まない手をなくす）。
- 速さは差分で: 画素ハッシュが同じなら前の結果を継承（inherited）、変わった区画の矩形だけ読み直す（region）、見た目全体・矩形のキャッシュ（cache）。
- 「読み直していない」と「観測していない」を分ける: 継承した行は status=inherited、失敗は not_observed。
- 将来のリアルタイム用に、層は手の単位でなく時刻つきの観測としても作れる形（step と t を持つ）にしておく。

## 5. 段階
ブロック 1〜4 を v0.29.0 で実装。5 以降は順に（局所パターン → 追跡 → 見た目の状態 → 関係 → 文字・数字の統合 → 自然と手の変化 → 局所の予測 → 予測の外れ → 特徴の重要度 → 選択 → 目的・計画 → スキル → Viewer・健全性 → 長時間試験）。

## v0.29.1（ブロック 5〜8・10〜12）
- 5 局所パターンの候補: 32×24 区画の画素のハッシュの変化のつながり → 塊のまわりの枠でいちばん多い色の符号（その場所の背景）でない区画へ広げる。抜けた跡は候補にしない。
- 6 追跡: 前の区画に変化があった追跡だけを対象に、位置の差＋色の分布の差で「互いにいちばん近い」組を続ける（固定の距離なし）。消えた追跡は見た目が完全に同じときだけ再同定。変化のない所の物は居続ける。
- 7 見た目の状態: 外接矩形を 4×4 にそろえた色の符号のハッシュ。追跡ごとに A,B,C…。位置が同じで見た目だけ変わった回数を数える。
- 8 関係: 候補の組ごとの差・距離・近づく/離れる/同じ・重なり・隣接・同じ動き。
- 10 入力なし/手の分離: 出来事（移動・見た目だけ・出現・消失・文字・数字）を RegionCausality で手に依存/依存しないに分ける。
- 11 局所の予測: 画面×手ごとの出来事の確率と、いちばん多い移動。12 外れ: 驚き −ln p、平均が ln2 を超える組を「外れが続く」として記録（状態の粗さ・隠れた条件の仮説の材料）。
- 手の選択・World Model へのつなぎ込みはしない（v0.29.0 の実機の比較を保つため）。次はブロック 13 以降で、予測の外れを情報の値打ちに使う。
