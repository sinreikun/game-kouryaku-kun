# Game Supervisor 設計（v0.21.0。利用者の依頼 t44u）

## 目的と責任の分担
| | Game Supervisor（システム） | 攻略 AI（探索・目的・予測・並び） |
|---|---|---|
| 見るもの | プロセスの有無・終了コード、ウィンドウの有無・ハンドル・大きさ、OS の応答なしの判定と WM_NULL への応答、取得・入力の状態、DB の保存、復旧の回数 | 取得した画面だけ |
| すること | 手を止める・経験の保存・DB のチェックポイント・設定の方法での起動・ウィンドウの探し直し・取得と入力のつなぎ直し・観測し直しの要求・回路遮断 | 中立のキー入力（Profile の論理キー名と wait）を対象のウィンドウへ送る |
| 知らないこと | ゲームの中身（タイトル・設定画面・死亡・クリア等）。設定画面はふつうの未知の状態 | 起動・終了・再起動・設定の意味。再起動は AI の行動ではない |
| 記録 | `system_events`（別の表。報酬・価値・予測・並びに使わない） | `experience_stream` ほか（状態＋操作→結果） |

## 権限の境界
- プロセスの起動と終了の確認は `GameKouryakuKun.Supervisor/ProcessControl.cs` だけ（ガードレールのテストで固定）。起動できるのは `data\supervisor.json` に人が書いた実行ファイルか `steam://rungameid/{数字}` だけ。`Kill` は使わない（どこにも書けない）。メモリ・モジュール・スレッド・ハンドルは読まない。
- 応答の確認の P/Invoke は `IsHungAppWindow`・`SendMessageTimeoutW(WM_NULL)` だけを許可リストに足した（Native.cs）。
- 攻略 AI の入力は従来どおり InputGuard（Win・Apps・PrintScreen・Alt+F4・Alt+Tab 等を拒否）を通った中立のキーだけで、対象のウィンドウにだけ送る。シェル・任意のプロセス・ファイル・ウィンドウ操作の経路は AI にない。AI のコード（Core の Exploration・Goals・Turn・Perception）は Supervisor を参照しない（静的検査）。AI は supervisor.json・実行ファイルを書かない（書く処理がない）。

## 状態と段階
NORMAL → (SUSPECTED_HANG) → RECOVERING → NORMAL／RECOVERY_ABORTED。
- 画面が止まっていることだけでは何もしない。OS の「応答なし」＋ WM_NULL の無応答が hang_suspect_sec 続いたら SUSPECTED_HANG を記録して警告し、手を送らずに待つ（終了させない）。
- L1 ウィンドウを探し直す → L2 取得と入力をつなぎ直す（待つ）→ L3 プロセスを確かめる（生きていれば再起動せず window_timeout_sec 待つ。戻らなければ RECOVERY_ABORTED / WINDOW_NOT_RESTORED）→ L4 はっきり終了したときだけ再起動。
- 起動の方法が none（既定）: 自動では起動せず、manual_restart_wait_sec の間、人が起動し直すのを待って再接続する。

## 復旧の手順（1 回を 1 つの取引として段ごとに記録）
OBSERVATION_LOST → 手を止める（キーを離す）→ 経験の保存（探索の記録・動く所・目的の状態）→ DB_CHECKPOINT → PROCESS_EXITED（終了コード・OS の分類 normal/abnormal/crash/unknown）→ 待ち（backoff_initial_sec から倍、backoff_max_sec まで）→ SUPERVISOR_RESTART_BEGIN → PROCESS_STARTED → WINDOW_ACQUIRED → INPUT_RECONNECTED → CAPTURE_RESTORED → OBSERVATION_RESTORED → EXPLORATION_RESUMED。
再開の前に探索の手順の中で: 接続を差し替え、ノイズの較正をやり直し（大きさの変化に追従）、新しいフレームを通常の状態の認識で分類して今の画面とし、前の手の結果の画面（prevAfter）・送る前の手・並び・目的（放棄）・計画・小目的・前後の文脈・人間の積んだ手を捨てる。中断の前後を 1 つの「状態＋操作→結果」にしない。Human Control の ON/OFF は保つ。学習 DB は消さない。復旧は並び（ActionSequence）にもスキルにもならない（経験の表に入らないため）。

## 回路遮断
restart_window_sec の中の再起動が max_restarts 回に達したら、探索と再起動を止め、DB を保存し、診断 zip（健全性レポートを含む）を書き出し、RECOVERY_ABORTED（RESTART_LOOP）を記録して人を待つ。

## 見張るファイル
`watch_files` に明示したファイルだけ、起動時の基準からの変化（大きさ・SHA-256・有無）を 60 秒ごとに確かめ、FILE_CHANGED として差分を記録して警告するだけ。推測で対象を増やさない・自動では戻さない・学習 DB とセーブは対象外。将来: 基準のスナップショットを保存し、区別が確かなものだけ人の操作で戻す（今はしない）。

## 記録
`system_events(event_id, run_id, seq_before, kind CHECK, recovery_id, detail, data_json, at)`（スキーマ v14）。seq_before は直前のゲームの経験の通し番号で、時間の上の分析だけに使う。再起動をエピソードの終わりとは決めない。

## v0.23.1 の見直し（利用者の報告 t45u・t44u の全項目の点検）
- 自動で起動し直す（既定 ON）: 接続したときに ProcessControl が OS に実行ファイルの場所だけを尋ね（PROCESS_QUERY_LIMITED_INFORMATION で開いてすぐ閉じる）、data\launch-settings.json に記録する。隣の steam_appid.txt か Steam ライブラリの appmanifest_*.acf（appid と installdir だけ）で Steam のゲームと分かれば steam://rungameid で起動する。画面の上の欄で OFF・実行ファイルを選ぶ・Steam app id を選べる。supervisor.json に launch_method（none 以外）を書けばそちらが優先。引数・作業フォルダは他のプロセスのメモリを読まないと取れないので取らない（作業フォルダは実行ファイルのフォルダ）。
- 起動の方法は使うたびに読み直す（画面の変更がすぐ効く）。起動は ProcessControl.Launch だけ。exe は起動したプロセスの pid を見張り、Steam 経由はウィンドウが出たらそのプロセスを見張る（段 process_tracked）。
- ウィンドウが消えてプロセスが生きている・分からない: window_timeout_sec 待っても戻らなければ、起動の方法があれば終了させずに起動し直す（段 relaunch_without_exit。回路遮断の数に入る）。なければ WINDOW_NOT_RESTORED で人を待つ。プロセスが終わった: すぐ起動し直す。
- 入力の方式の失敗（続けて 5 回の送信失敗）は探索を止めずに Supervisor の信号 INPUT_LOST にし、入力をつなぎ直す。時間の窓の中で max_restarts 回を超えたら INPUT_LOOP で止めて人を待つ。
- 見張るファイルの基準は最初に 1 回だけ取って data\supervisor-file-baseline.json に保存し、起動・変化のたびに取り直さない。同じ変化は 1 回だけ知らせる。基準を今にするのは人がそのファイルを消したときだけ。

## 実機での安全な確かめ方
1. （v0.23.1）画面の上の欄「ゲームが消えたら自動で起動し直す」を確かめる（既定 ON・接続したゲームの実行ファイル）。JSON を書く必要はない。
2. いつもどおり接続して開始し、数十手動かす。
3. ゲームをふつうに 1 回閉じる（ゲームのメニューから、またはウィンドウの × ボタン）。
4. 確かめること（画面の Supervisor の行と「Supervisor の記録」の欄）: RECOVERING（PROCESS_EXIT・段）→（none の場合は人がゲームを起動し直す）→ NORMAL・Restart 1（none のときは 0）・1 手サマリーに「──── PROCESS EXIT ────」などの区切り。
5. 停止して診断データを書き出し、zip を送る。健全性レポートの「Supervisor」の項目（復旧 #1 の段・DB の保存・観測し直してから再開）と、system_events に記録があり experience_stream に中断の前後をつなぐ手がないこと（supervisor_reobserve の from_node と observed_node）で確かめる。
