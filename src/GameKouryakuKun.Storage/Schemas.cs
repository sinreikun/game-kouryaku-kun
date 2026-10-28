namespace GameKouryakuKun.Storage;

/// <summary>
/// スキーマ定義（設計書 §5.3）。マイグレーションは前方のみ。版を上げるときは配列の末尾に追加する（既存の版は書き換えない）。
/// </summary>
public static class Schemas
{
    public const string MemoryMetaTable = "schema_meta";
    public const string AppMetaTable = "app_schema_meta";
    public const string DiagMetaTable = "diag_schema_meta";

    // ============================================================
    // memory.sqlite（Profile 単位の経験DB）
    // ============================================================
    public static readonly IReadOnlyList<Migration> Memory = new[]
    {
        new Migration(1, "初期スキーマ（設計書 v2 §5.3）", """
CREATE TABLE schema_meta(key TEXT PRIMARY KEY, value TEXT);

CREATE TABLE screenshots(
  screenshot_id INTEGER PRIMARY KEY,
  sha1 TEXT UNIQUE, path TEXT, width INTEGER, height INTEGER,
  format TEXT, quality TEXT,
  reason TEXT,
  retained INTEGER DEFAULT 0,
  created_at TEXT
);

CREATE TABLE runs(
  run_id INTEGER PRIMARY KEY,
  started_at TEXT, ended_at TEXT,
  end_reason TEXT CHECK(end_reason IN ('death','clear','abort','crash','unknown')),
  profile_version TEXT, recognizer_version TEXT, policy_version TEXT,
  input_backend_id TEXT, capture_backend_id TEXT,
  llm_model_id TEXT,
  turns_total INTEGER, game_turns_est INTEGER,
  max_depth_value INTEGER,
  irreversible_transitions INTEGER,
  novelty_count INTEGER,
  notes_json TEXT
);

CREATE TABLE turns(
  turn_id INTEGER PRIMARY KEY,
  run_id INTEGER REFERENCES runs,
  turn_index INTEGER,
  boundary_kind TEXT,
  latency_ms INTEGER,
  game_turn_consumed_p REAL,
  abstract_state_key TEXT,
  created_at TEXT,
  UNIQUE(run_id, turn_index)
);
CREATE INDEX ix_turns_state ON turns(abstract_state_key);

CREATE TABLE observations(
  obs_id INTEGER PRIMARY KEY,
  turn_id INTEGER UNIQUE REFERENCES turns,
  screen_mode_id INTEGER,
  self_cell_x INTEGER, self_cell_y INTEGER, self_ve_id INTEGER,
  floor_seq INTEGER,
  stats_json TEXT,
  local_view_json TEXT,
  features_blob BLOB,
  menu_json TEXT,
  recog_conf_json TEXT,
  screenshot_id INTEGER REFERENCES screenshots
);

CREATE TABLE message_templates(
  template_id INTEGER PRIMARY KEY,
  pattern TEXT UNIQUE,
  slot_types_json TEXT,
  first_seen_turn INTEGER, seen_count INTEGER
);
CREATE TABLE observation_messages(
  obs_id INTEGER REFERENCES observations,
  line_index INTEGER, raw_text TEXT,
  template_id INTEGER REFERENCES message_templates,
  slots_json TEXT,
  PRIMARY KEY(obs_id, line_index)
);

CREATE TABLE actions(
  action_id INTEGER PRIMARY KEY,
  turn_id INTEGER UNIQUE REFERENCES turns,
  logical_key TEXT,
  intent TEXT,
  intent_target TEXT,
  plan_id INTEGER,
  score REAL, score_components_json TEXT,
  reason_json TEXT,
  policy_mode TEXT
);
CREATE TABLE decision_candidates(
  turn_id INTEGER REFERENCES turns, rank INTEGER,
  logical_key TEXT, intent TEXT, intent_target TEXT,
  score REAL, components_json TEXT,
  PRIMARY KEY(turn_id, rank)
);
CREATE TABLE outcomes(
  action_id INTEGER PRIMARY KEY REFERENCES actions,
  next_turn_id INTEGER REFERENCES turns,
  screen_changed INTEGER, self_moved INTEGER,
  stat_delta_json TEXT,
  new_message_templates_json TEXT,
  entity_events_json TEXT,
  mode_changed INTEGER, irreversible_transition INTEGER,
  reward_intrinsic REAL,
  surprise REAL
);

CREATE TABLE entities(
  ve_id INTEGER PRIMARY KEY,
  kind_guess TEXT,
  display_name TEXT,
  aliases_json TEXT,
  prototype_hash TEXT, embedding BLOB,
  icon_screenshot_id INTEGER REFERENCES screenshots,
  first_seen_turn INTEGER, seen_count INTEGER,
  merged_into INTEGER REFERENCES entities
);
CREATE TABLE entity_sightings(
  turn_id INTEGER REFERENCES turns, ve_id INTEGER REFERENCES entities,
  track_id INTEGER, cell_x INTEGER, cell_y INTEGER, conf REAL,
  PRIMARY KEY(turn_id, ve_id, track_id)
);

CREATE TABLE hypotheses(
  hyp_id INTEGER PRIMARY KEY,
  template TEXT,
  domain TEXT,
  condition_json TEXT,
  action_json TEXT,
  effect_json TEXT,
  parent_hyp_id INTEGER REFERENCES hypotheses,
  status TEXT CHECK(status IN ('tentative','supported','established','weakened','refuted','merged')),
  source TEXT CHECK(source IN ('stat','llm','user_note')),
  alpha REAL, beta REAL,
  n_obs INTEGER, n_success INTEGER, n_failure INTEGER,
  confidence REAL,
  effect_mean REAL, effect_var REAL,
  importance REAL,
  summary_text TEXT,
  created_at TEXT, updated_at TEXT
);
CREATE INDEX ix_hyp_status ON hypotheses(status, domain);
CREATE TABLE hypothesis_evidence(
  evidence_id INTEGER PRIMARY KEY,
  hyp_id INTEGER REFERENCES hypotheses,
  turn_id INTEGER REFERENCES turns,
  result TEXT CHECK(result IN ('support','contradict','neutral')),
  weight REAL,
  observed_value REAL,
  note TEXT
);
CREATE TABLE hypothesis_revisions(
  revision_id INTEGER PRIMARY KEY,
  hyp_id INTEGER REFERENCES hypotheses,
  revised_at_turn INTEGER REFERENCES turns,
  kind TEXT CHECK(kind IN ('create','promote','demote','refine_split','effect_update','merge','refute','revive')),
  before_json TEXT, after_json TEXT,
  trigger TEXT,
  explanation TEXT
);

CREATE TABLE deaths(
  run_id INTEGER PRIMARY KEY REFERENCES runs,
  turn_id INTEGER REFERENCES turns,
  last_messages_json TEXT,
  last_stats_json TEXT,
  nearby_entities_json TEXT,
  cause_estimate_json TEXT,
  screenshot_ids_json TEXT,
  llm_summary TEXT
);

CREATE TABLE stats_run(run_id INTEGER REFERENCES runs, key TEXT, value REAL, PRIMARY KEY(run_id,key));
CREATE TABLE stats_global(key TEXT PRIMARY KEY, value REAL, updated_at TEXT);
CREATE TABLE sa_stats(
  abstract_state_key TEXT, logical_key TEXT, intent TEXT,
  n INTEGER, reward_sum REAL, reward_sq_sum REAL, death_within_k INTEGER,
  PRIMARY KEY(abstract_state_key, logical_key, intent)
);
CREATE TABLE hazard_model(
  model_version INTEGER PRIMARY KEY, trained_at TEXT, feature_names_json TEXT, weights_blob BLOB, metrics_json TEXT
);

CREATE TABLE llm_calls(
  call_id INTEGER PRIMARY KEY, provider_id TEXT, model_id TEXT,
  purpose TEXT, request_digest TEXT, response_json TEXT,
  tokens_in INTEGER, tokens_out INTEGER, latency_ms INTEGER, created_at TEXT,
  status TEXT CHECK(status IN ('pending','done','failed','cancelled'))
);
"""),
        new Migration(2, "runs.end_detail を追加。旧版で終了理由が CHECK 制約外のため閉じられなかった冒険を補完", """
ALTER TABLE runs ADD COLUMN end_detail TEXT;
UPDATE runs SET
  ended_at = COALESCE((SELECT MAX(t.created_at) FROM turns t WHERE t.run_id = runs.run_id), started_at),
  end_reason = 'unknown',
  end_detail = 'backfill_v2:終了記録なし（旧版の終了理由が制約外で記録に失敗、終了時刻は最終ターン）',
  turns_total = (SELECT COUNT(*) FROM turns t WHERE t.run_id = runs.run_id)
WHERE ended_at IS NULL;
"""),
        new Migration(3, "Phase 2 の土台（phase2-design.md §10.4）: 終了理由を手続き上の区切りに改め（試験完了を区別）、終了種別の推定列を追加。outcomes の差分の数値を diff_metrics_json へ移す。kind_guess を汎用値に制限。認識・遷移・過程データ管理の表を追加", """
-- 1) runs: end_reason を手続き上の区切りへ（ended/abort/crash/test_completed/unknown）。終了種別（死亡/脱出/不明）は推定値として別の列
CREATE TABLE runs_v3(
  run_id INTEGER PRIMARY KEY,
  started_at TEXT, ended_at TEXT,
  end_reason TEXT CHECK(end_reason IN ('ended','abort','crash','test_completed','unknown')),
  end_detail TEXT,
  end_mode_id INTEGER,
  end_kind_est TEXT CHECK(end_kind_est IS NULL OR end_kind_est IN ('death','escape','unknown')),
  end_kind_conf REAL,
  end_kind_version TEXT,
  profile_version TEXT, recognizer_version TEXT, policy_version TEXT,
  input_backend_id TEXT, capture_backend_id TEXT,
  llm_model_id TEXT,
  turns_total INTEGER, game_turns_est INTEGER,
  max_depth_value INTEGER,
  irreversible_transitions INTEGER,
  novelty_count INTEGER,
  notes_json TEXT
);
INSERT INTO runs_v3(run_id, started_at, ended_at, end_reason, end_detail, end_kind_est, profile_version, recognizer_version, policy_version,
  input_backend_id, capture_backend_id, llm_model_id, turns_total, game_turns_est, max_depth_value, irreversible_transitions, novelty_count, notes_json)
SELECT run_id, started_at, ended_at,
  CASE
    WHEN end_reason IN ('death','clear') THEN 'ended'
    WHEN end_reason = 'abort' AND policy_version = 'separation-test' AND end_detail = 'completed' THEN 'test_completed'
    WHEN end_reason IN ('abort','crash','unknown') THEN end_reason
    WHEN end_reason IS NULL THEN NULL
    ELSE 'unknown' END,
  end_detail,
  CASE end_reason WHEN 'death' THEN 'death' WHEN 'clear' THEN 'escape' ELSE NULL END,
  profile_version, recognizer_version, policy_version,
  input_backend_id, capture_backend_id, llm_model_id, turns_total, game_turns_est, max_depth_value, irreversible_transitions, novelty_count, notes_json
FROM runs;
DROP TABLE runs;
ALTER TABLE runs_v3 RENAME TO runs;

-- 学習の集計に使う冒険（試験用ハーネスの分離試験は除外。phase2-design.md §13）
CREATE VIEW learning_runs AS SELECT * FROM runs WHERE COALESCE(policy_version,'') <> 'separation-test';

-- 2) outcomes: 画面差分の数値は diff_metrics_json へ（entity_events_json はトラックのできごと専用に戻す）
ALTER TABLE outcomes ADD COLUMN diff_metrics_json TEXT;
UPDATE outcomes SET diff_metrics_json = entity_events_json, entity_events_json = NULL
WHERE entity_events_json IS NOT NULL AND json_valid(entity_events_json)
  AND (json_type(entity_events_json, '$.peak_ratio') IS NOT NULL OR json_type(entity_events_json, '$.first_change_ms') IS NOT NULL
       OR json_type(entity_events_json, '$.frames') IS NOT NULL OR json_type(entity_events_json, '$.input_backend') IS NOT NULL);

-- 3) entities: kind_guess は汎用値だけ（意味ラベルの混入を構造的に防ぐ）
CREATE TABLE entities_v3(
  ve_id INTEGER PRIMARY KEY,
  kind_guess TEXT CHECK(kind_guess IS NULL OR kind_guess IN ('mobile','static','unknown')),
  display_name TEXT,
  aliases_json TEXT,
  prototype_hash TEXT, embedding BLOB,
  icon_screenshot_id INTEGER REFERENCES screenshots,
  first_seen_turn INTEGER, seen_count INTEGER,
  merged_into INTEGER REFERENCES entities
);
INSERT INTO entities_v3 SELECT ve_id, CASE WHEN kind_guess IN ('mobile','static','unknown') THEN kind_guess ELSE NULL END,
  display_name, aliases_json, prototype_hash, embedding, icon_screenshot_id, first_seen_turn, seen_count, merged_into FROM entities;
DROP TABLE entities;
ALTER TABLE entities_v3 RENAME TO entities;

-- 4) observations: 認識結果の列（Phase 2 で埋まる）
ALTER TABLE observations ADD COLUMN layout_version INTEGER;
ALTER TABLE observations ADD COLUMN grid_id INTEGER;
ALTER TABLE observations ADD COLUMN self_conf REAL;
ALTER TABLE observations ADD COLUMN cells_blob BLOB;
ALTER TABLE observations ADD COLUMN slots_json TEXT;
ALTER TABLE observations ADD COLUMN panels_json TEXT;
ALTER TABLE observations ADD COLUMN noise_json TEXT;
ALTER TABLE observations ADD COLUMN abstract_keys_json TEXT;

-- 5) screenshots: 過程データの自動削除（phase2-design.md §10.5）のための列
ALTER TABLE screenshots ADD COLUMN run_id INTEGER;
ALTER TABLE screenshots ADD COLUMN purpose TEXT DEFAULT 'process';
ALTER TABLE screenshots ADD COLUMN size_bytes INTEGER;
UPDATE screenshots SET run_id = (SELECT t.run_id FROM observations o JOIN turns t ON t.turn_id = o.turn_id WHERE o.screenshot_id = screenshots.screenshot_id LIMIT 1);
UPDATE screenshots SET purpose = CASE WHEN retained = 1 THEN 'representative' ELSE 'process' END;
CREATE INDEX ix_screenshots_run ON screenshots(run_id, purpose);

-- 6) 認識・状態表現（2a）
CREATE TABLE layout_versions(
  layout_version INTEGER PRIMARY KEY, capture_w INTEGER, capture_h INTEGER,
  regions_json TEXT, created_turn INTEGER, evidence_json TEXT, created_at TEXT
);
CREATE TABLE layout_reviews(
  review_id INTEGER PRIMARY KEY, layout_version INTEGER REFERENCES layout_versions, answer TEXT CHECK(answer IN ('yes','no')), created_at TEXT
);
CREATE TABLE grid_estimates(
  grid_id INTEGER PRIMARY KEY, layout_version INTEGER REFERENCES layout_versions,
  basis_ux REAL, basis_uy REAL, basis_vx REAL, basis_vy REAL, origin_x REAL, origin_y REAL, perspective_k REAL,
  explained_ratio REAL, confidence REAL, created_turn INTEGER, created_at TEXT
);
CREATE TABLE screen_modes(
  mode_id INTEGER PRIMARY KEY, prototype_blob BLOB, layout_signature TEXT, seen_count INTEGER,
  response_profile_json TEXT, unresponsive_p REAL, first_seen_turn INTEGER
);
CREATE TABLE ve_prototypes(
  ve_id INTEGER REFERENCES entities, proto_index INTEGER, feature_blob BLOB, seen_count INTEGER,
  PRIMARY KEY(ve_id, proto_index)
);
CREATE TABLE ve_links(
  ve_a INTEGER REFERENCES entities, ve_b INTEGER REFERENCES entities, kind TEXT CHECK(kind IN ('temporal','merge')), count INTEGER,
  PRIMARY KEY(ve_a, ve_b, kind)
);
CREATE TABLE ve_stats(ve_id INTEGER REFERENCES entities, key TEXT, value REAL, PRIMARY KEY(ve_id, key));
CREATE TABLE token_slots(
  slot_id INTEGER PRIMARY KEY, region_ref TEXT, line_index INTEGER, x_bucket INTEGER, left_text TEXT, shape TEXT, first_seen_turn INTEGER
);
CREATE TABLE token_values(
  turn_id INTEGER REFERENCES turns, slot_id INTEGER REFERENCES token_slots, value REAL, max_value REAL, raw_text TEXT, conf REAL, engine TEXT,
  PRIMARY KEY(turn_id, slot_id)
);
CREATE TABLE token_links(
  slot_a INTEGER REFERENCES token_slots, slot_b TEXT, relation TEXT, n INTEGER, stats_json TEXT,
  PRIMARY KEY(slot_a, slot_b, relation)
);
CREATE TABLE panels(
  panel_sig TEXT PRIMARY KEY, border_json TEXT, key_distribution_json TEXT, lines_json TEXT, seen_count INTEGER
);
CREATE TABLE noise_events(
  noise_id INTEGER PRIMARY KEY, turn_id INTEGER REFERENCES turns, kind TEXT, rect_json TEXT, duration_ms INTEGER, evidence_json TEXT
);
CREATE TABLE floor_maps(
  run_id INTEGER REFERENCES runs, floor_seq INTEGER, cells_blob BLOB, created_at TEXT,
  PRIMARY KEY(run_id, floor_seq)
);

-- 7) 画面遷移の自律学習（2b）と終了種別
CREATE TABLE mode_transitions(
  from_node TEXT, logical_key TEXT, to_node TEXT, n INTEGER, success_rate REAL, reversible INTEGER, danger INTEGER DEFAULT 0, updated_at TEXT,
  PRIMARY KEY(from_node, logical_key, to_node)
);
CREATE TABLE run_end_features(run_id INTEGER PRIMARY KEY REFERENCES runs, features_json TEXT, cluster_id INTEGER, version TEXT);
""", RebuildsTables: true),
        new Migration(4, "最上位原則（攻略知識の外部注入禁止）に沿った整理: ゲームの意味を前提にした列・表（終了種別の事前ラベルなど）を削除し、遷移パターンの教師なし分類・可逆性・説明層の名前の表を追加。論理キー名の中立化に伴い旧キー名の遷移を破棄", """
-- 1) runs: 終了種別の事前ラベル（end_kind_*）と深さ（max_depth_value）の列を削除（U1: ラベルは本体に持たない）
DROP VIEW IF EXISTS learning_runs;
ALTER TABLE runs DROP COLUMN end_kind_est;
ALTER TABLE runs DROP COLUMN end_kind_conf;
ALTER TABLE runs DROP COLUMN end_kind_version;
ALTER TABLE runs DROP COLUMN max_depth_value;
-- 学習の集計に使う冒険（診断の記録は v4 以降 memory.sqlite に入らない。旧版の分離試験の行も除外する）
CREATE VIEW learning_runs AS SELECT * FROM runs WHERE COALESCE(policy_version,'') NOT IN ('separation-test') AND COALESCE(policy_version,'') NOT LIKE 'diag%';

-- 2) ゲームの意味を前提にした表・列を削除（中身は書かれたことがない設計上の表）
DROP TABLE IF EXISTS deaths;
DROP TABLE IF EXISTS hazard_model;
DROP TABLE IF EXISTS floor_maps;
ALTER TABLE observations DROP COLUMN floor_seq;
ALTER TABLE observations DROP COLUMN menu_json;
ALTER TABLE observations RENAME COLUMN self_cell_x TO anchor_x;
ALTER TABLE observations RENAME COLUMN self_cell_y TO anchor_y;
ALTER TABLE observations RENAME COLUMN self_ve_id TO anchor_ve_id;
ALTER TABLE observations RENAME COLUMN self_conf TO anchor_conf;
ALTER TABLE outcomes RENAME COLUMN self_moved TO anchor_moved;
ALTER TABLE sa_stats DROP COLUMN death_within_k;
DROP INDEX IF EXISTS ix_hyp_status;
ALTER TABLE hypotheses DROP COLUMN domain;
CREATE INDEX ix_hyp_status ON hypotheses(status);

-- 3) 遷移の記録: 危険度の列を削除。論理キー名を中立名（dpad_*・button*）に改めたため、旧キー名の遷移は破棄する（画面の種類は残す）
DROP TABLE mode_transitions;
CREATE TABLE mode_transitions(
  from_node TEXT, logical_key TEXT, to_node TEXT, n INTEGER, success_rate REAL, updated_at TEXT,
  PRIMARY KEY(from_node, logical_key, to_node)
);
-- 操作ごとの可逆性（別の画面へ移ったあと元へ戻れたか。安全規則の学習。U2）
CREATE TABLE action_reversibility(
  logical_key TEXT PRIMARY KEY, returned INTEGER, not_returned INTEGER, undo_hits INTEGER, updated_at TEXT
);

-- 4) 遷移パターンの教師なし分類（U1）。名前は持たない
CREATE TABLE transition_patterns(
  pattern_id INTEGER PRIMARY KEY, centroid_json TEXT, n INTEGER, feature_names_json TEXT, updated_at TEXT
);
CREATE TABLE transition_pattern_stats(id INTEGER PRIMARY KEY CHECK(id = 1), n INTEGER, mean_json TEXT, m2_json TEXT, updated_at TEXT);
CREATE TABLE transition_events(
  event_id INTEGER PRIMARY KEY, run_id INTEGER REFERENCES runs, start_step INTEGER, end_step INTEGER,
  pattern_id INTEGER REFERENCES transition_patterns, distance REAL, features_json TEXT, created_at TEXT
);
CREATE INDEX ix_transition_events_run ON transition_events(run_id);
-- 説明層（人向けの名前）。本体の学習・判断は読まない（静的検査で保証）
CREATE TABLE pattern_labels(
  pattern_id INTEGER PRIMARY KEY REFERENCES transition_patterns, label TEXT, note TEXT, updated_at TEXT
);
"""),
        new Migration(5, "連続経験履歴と系列の統計的発見（2026-10-18 09:36）: 学習の区切りを与えない。履歴は通し番号の1本の系列で持ち、反復系列・循環・境界の候補からパターンを作る。区切りに依存した表（遷移のまとまりの分類・冒険末尾の特徴）と、操作対象を前提にした列を削除", """
-- 1) 冒険・区切りに依存した表を削除（説明層の名前の表は、参照先を作り直すため一緒に作り直す）
DROP TABLE IF EXISTS pattern_labels;
DROP TABLE IF EXISTS transition_events;
DROP TABLE IF EXISTS transition_patterns;
DROP TABLE IF EXISTS transition_pattern_stats;
DROP TABLE IF EXISTS run_end_features;
-- runs は実行単位（システム上の記録）としてだけ残す。学習の集計用の見方は置かない
DROP VIEW IF EXISTS learning_runs;
-- 2) 操作対象（入力に連動して動く点）を前提にした列を削除
ALTER TABLE observations DROP COLUMN anchor_x;
ALTER TABLE observations DROP COLUMN anchor_y;
ALTER TABLE observations DROP COLUMN anchor_ve_id;
ALTER TABLE observations DROP COLUMN anchor_conf;
ALTER TABLE outcomes DROP COLUMN anchor_moved;

-- 3) 連続経験履歴: 全実行を通した通し番号 seq の1本の系列。run_id・turn_id は記録の照合用（システム情報）で、学習は seq の順だけを読む
CREATE TABLE experience_stream(
  seq INTEGER PRIMARY KEY, run_id INTEGER REFERENCES runs, turn_id INTEGER,
  state TEXT NOT NULL, action TEXT NOT NULL, changed INTEGER NOT NULL, next_state TEXT NOT NULL,
  change_ratio REAL, surprisal REAL, boundary TEXT, created_at TEXT
);
-- 既存の記録（v0.7.0 の中立のキー名のもの）を、記録の順に履歴へ移す
INSERT INTO experience_stream(run_id, turn_id, state, action, changed, next_state, change_ratio, created_at)
SELECT t.run_id, t.turn_id, json_extract(o.recog_conf_json, '$.node_before'), a.logical_key,
       CASE WHEN oc.screen_changed = 1 OR t.boundary_kind = 'timeout' THEN 1 ELSE 0 END,
       json_extract(o.recog_conf_json, '$.node_after'), json_extract(oc.diff_metrics_json, '$.final_ratio'), t.created_at
FROM turns t JOIN runs r ON r.run_id = t.run_id JOIN observations o ON o.turn_id = t.turn_id JOIN actions a ON a.turn_id = t.turn_id JOIN outcomes oc ON oc.action_id = a.action_id
WHERE COALESCE(r.policy_version,'') NOT LIKE 'diag%' AND COALESCE(r.policy_version,'') <> 'separation-test'
  AND json_valid(o.recog_conf_json) AND json_extract(o.recog_conf_json, '$.node_before') IS NOT NULL AND json_extract(o.recog_conf_json, '$.node_after') IS NOT NULL
  AND (a.logical_key GLOB 'dpad_*' OR a.logical_key GLOB 'button*')
ORDER BY t.turn_id;

-- 4) 系列から見つけたパターン（種類は見つけ方の区別だけ。名前は持たない）
CREATE TABLE sequence_patterns(
  pattern_id INTEGER PRIMARY KEY, kind TEXT NOT NULL CHECK(kind IN ('motif','cycle','segment')), signature TEXT NOT NULL,
  length INTEGER, n INTEGER, first_seq INTEGER, last_seq INTEGER, updated_at TEXT, UNIQUE(kind, signature)
);
-- 説明層（人向けの名前）。本体の学習・判断は読まない（静的検査で保証）
CREATE TABLE pattern_labels(
  pattern_id INTEGER PRIMARY KEY REFERENCES sequence_patterns, label TEXT, note TEXT, updated_at TEXT
);
"""),
        new Migration(6, "汎用の機構の追加（2026-10-18 10:49）: 予測の外れ（ふだんの不確かさとの差）の記録、区画とキーの反応の対応表、経験から見つけた操作の並び（複合の操作の候補）、見た目の状態と遷移の特徴の別々の表現と、証拠に基づくノードの分割", """
-- 1) 連続経験履歴に、ふだんの不確かさを引いた予測の外れと、見た目の状態（細かい配置の群の番号）を足す
ALTER TABLE experience_stream ADD COLUMN excess REAL;
ALTER TABLE experience_stream ADD COLUMN vis_state INTEGER;
-- 2) 区画とキーの反応の対応表（区画は位置の番号だけ。cell = -1 の行は手数）
CREATE TABLE reaction_maps(
  context TEXT NOT NULL, action TEXT NOT NULL, cell INTEGER NOT NULL,
  n INTEGER, changed INTEGER, translated INTEGER, sum_dx REAL, sum_dy REAL, updated_at TEXT,
  PRIMARY KEY(context, action, cell)
);
-- 3) 経験から見つけた操作の並び（名前は ActionSequence #番号 だけ。事前の価値は持たない）
CREATE TABLE action_sequences(
  sequence_id INTEGER PRIMARY KEY, steps TEXT NOT NULL UNIQUE, start_node TEXT NOT NULL, end_node TEXT,
  n_total INTEGER, n_at_start INTEGER, start_similarity REAL, reproducibility REAL, found_seq INTEGER, updated_at TEXT
);
-- 4) 見た目の状態（細かい配置の群）・その遷移の特徴・分割（見た目と遷移の特徴は別々の表現として持つ）
CREATE TABLE visual_states(
  vis_id INTEGER PRIMARY KEY, prototype_blob BLOB NOT NULL, seen_count INTEGER, coarse_node TEXT, trans_group INTEGER, updated_at TEXT
);
CREATE TABLE state_fingerprints(
  vis_id INTEGER NOT NULL, action TEXT NOT NULL, n_none INTEGER, n_stay INTEGER, n_leave INTEGER, PRIMARY KEY(vis_id, action)
);
CREATE TABLE state_splits(
  vis_id INTEGER PRIMARY KEY, coarse_node TEXT NOT NULL, split_node TEXT NOT NULL, distance REAL, leader_vis_id INTEGER, decided_at TEXT
);
"""),
        new Migration(7, "経験の出どころの区別（2026-10-18 13:07 のユーザー決定）: 直接の経験（mode_transitions・experience_stream）と、分割による読み直し（derived_transitions・stream_reinterpretations）と、弱い事前の推定（experience_priors: 似た画面からの借用・分割前のノード）を別の表に持つ。操作の並びの候補の状態（選択の候補／休止）と状態の変化の履歴", """
-- 1) 操作の並びの候補の状態。active＝選択の候補、dormant＝休止（記録は消さない）
ALTER TABLE action_sequences ADD COLUMN status TEXT NOT NULL DEFAULT 'active' CHECK(status IN ('active','dormant'));
ALTER TABLE action_sequences ADD COLUMN status_seq INTEGER;
ALTER TABLE action_sequences ADD COLUMN last_reproduced_seq INTEGER;
ALTER TABLE action_sequences ADD COLUMN recent_repro REAL;
ALTER TABLE action_sequences ADD COLUMN recent_n INTEGER;
CREATE TABLE action_sequence_events(
  event_id INTEGER PRIMARY KEY, sequence_id INTEGER NOT NULL REFERENCES action_sequences, seq INTEGER,
  kind TEXT NOT NULL CHECK(kind IN ('promoted','waiting','dormant','reactivated')), reason TEXT, recent_repro REAL, at_start INTEGER, created_at TEXT
);
-- 2) 分割による読み直し（継承ではない）。直接の経験の表（mode_transitions・experience_stream）は書き換えず、読み直した結果を別に持つ
CREATE TABLE derived_transitions(
  derived_id INTEGER PRIMARY KEY, origin TEXT NOT NULL CHECK(origin IN ('reclassified')),
  from_node TEXT NOT NULL, logical_key TEXT NOT NULL, to_node TEXT NOT NULL, n INTEGER NOT NULL, changed_n INTEGER,
  orig_from TEXT NOT NULL, orig_to TEXT NOT NULL, split_vis INTEGER, created_at TEXT
);
CREATE TABLE stream_reinterpretations(
  seq INTEGER PRIMARY KEY REFERENCES experience_stream(seq), state TEXT NOT NULL, next_state TEXT NOT NULL, split_vis INTEGER, updated_at TEXT
);
-- 3) 弱い事前の推定（コピーではない。仮の観測の回数 strength だけ、出どころのノードの直接の経験の割合を足す）
CREATE TABLE experience_priors(
  node TEXT NOT NULL, source_node TEXT NOT NULL, origin TEXT NOT NULL CHECK(origin IN ('borrowed','parent')),
  strength REAL NOT NULL, evidence TEXT, updated_at TEXT, PRIMARY KEY(node, source_node, origin)
);
-- 4) 後から追える一覧: 直接に経験したもの・読み直したもの・推定に使ったもの
CREATE VIEW experience_provenance AS
  SELECT 'direct' AS origin, from_node AS node, logical_key, to_node, n, NULL AS source FROM mode_transitions
  UNION ALL SELECT origin, from_node, logical_key, to_node, n, orig_from || ' -> ' || orig_to FROM derived_transitions
  UNION ALL SELECT origin, node, NULL, NULL, NULL, source_node FROM experience_priors;
"""),
        new Migration(8, "予測 → 比較 → 修正 → 選択（v0.11.0）: 手ごとの判断の記録（選んだ規則・全候補の点数の内訳・手を送る前の予測とその証拠の出どころ・結果との比較）、予測の外れによる内部モデルの修正の記録、弱い事前の推定の変化の履歴（置き換えても出どころを失わない）", """
-- 1) 判断の記録（隠れた推論ではなく、点数の式の各項と予測の数値）。seq は連続経験履歴の通し番号
CREATE TABLE model_revisions(
  revision_id INTEGER PRIMARY KEY, seq INTEGER, node TEXT NOT NULL, logical_key TEXT NOT NULL,
  weight_before REAL, weight_after REAL, recent_kept INTEGER, reason TEXT, created_at TEXT
);
CREATE INDEX idx_model_revisions_rel ON model_revisions(node, logical_key);
CREATE TABLE decision_records(
  decision_id INTEGER PRIMARY KEY, seq INTEGER, turn_id INTEGER,
  node TEXT NOT NULL, chosen TEXT NOT NULL, composite TEXT,
  rule TEXT NOT NULL CHECK(rule IN ('scored','momentum','chord_probe','fallback','sequence_step')),
  probability REAL, score REAL,
  predicted_next TEXT, p_predicted REAL, p_change REAL, entropy REAL, uncertainty REAL,
  evidence_direct REAL, evidence_reinterpreted REAL, evidence_prior REAL, prior_sources TEXT,
  actual_next TEXT, changed INTEGER, p_actual REAL, surprisal REAL, excess REAL, hit INTEGER,
  revision_id INTEGER REFERENCES model_revisions,
  candidates_json TEXT, created_at TEXT
);
CREATE INDEX idx_decision_records_seq ON decision_records(seq);
CREATE INDEX idx_decision_records_rel ON decision_records(node, chosen);
-- 2) 弱い事前の推定の変化の履歴（experience_priors は今の一覧。置き換えのたびに、加わった・変わった・外れたものをここに残す）
CREATE TABLE experience_prior_events(
  event_id INTEGER PRIMARY KEY, node TEXT NOT NULL, source_node TEXT NOT NULL, origin TEXT NOT NULL CHECK(origin IN ('borrowed','parent')),
  kind TEXT NOT NULL CHECK(kind IN ('added','changed','removed')), strength REAL, evidence TEXT, created_at TEXT
);
"""),
        new Migration(9, "分割の見直しと長期の運用（v0.12.0）: 分割の状態（有効／取り消し）と変化の履歴、読み直しの済み印（v0.10.0 より前の分割を読み直すため）、読み直しの理由（分割・取り消し）、観測の確かさ（手ごとの割り当ての距離・フレームの古さ）、操作の並びを実行したときの予測と結果、判断の記録の要約（元の記録は消さない）、読み直しを速くする索引", """
-- 1) 分割の状態と変化の履歴。取り消しても行は消さない
ALTER TABLE state_splits ADD COLUMN status TEXT NOT NULL DEFAULT 'active' CHECK(status IN ('active','reverted'));
ALTER TABLE state_splits ADD COLUMN reclassified_at TEXT;
ALTER TABLE state_splits ADD COLUMN status_at TEXT;
-- v0.10.0 以降に読み直しを済ませた分割には済み印を付ける（読み直した遷移があるもの）。印のない分割は次の起動で読み直す
UPDATE state_splits SET reclassified_at = (SELECT MIN(d.created_at) FROM derived_transitions d WHERE d.split_vis = state_splits.vis_id)
  WHERE EXISTS (SELECT 1 FROM derived_transitions d WHERE d.split_vis = state_splits.vis_id);
CREATE TABLE state_split_events(
  event_id INTEGER PRIMARY KEY, vis_id INTEGER NOT NULL, coarse_node TEXT NOT NULL, split_node TEXT NOT NULL,
  kind TEXT NOT NULL CHECK(kind IN ('split','reclassified','reverted','resplit')),
  evidence_kind TEXT, distance REAL, log_bayes_factor REAL, moved INTEGER, reason TEXT, created_at TEXT
);
ALTER TABLE derived_transitions ADD COLUMN cause TEXT NOT NULL DEFAULT 'split' CHECK(cause IN ('split','revert'));
ALTER TABLE stream_reinterpretations ADD COLUMN cause TEXT NOT NULL DEFAULT 'split' CHECK(cause IN ('split','revert'));
-- 2) 観測の確かさ（割り当ての距離 ÷ 新しい状態を作る距離。1 に近いほど境目。フレームの古さ）
ALTER TABLE decision_records ADD COLUMN obs_vis_ratio REAL;
ALTER TABLE decision_records ADD COLUMN obs_mode_ratio REAL;
ALTER TABLE decision_records ADD COLUMN frame_age_ms REAL;
ALTER TABLE decision_records ADD COLUMN obs_ambiguous INTEGER;
-- 3) 操作の並びを最後まで実行したときの、実行前の予測と結果
CREATE TABLE sequence_executions(
  exec_id INTEGER PRIMARY KEY, sequence_id INTEGER, sequence_name TEXT NOT NULL, seq_end INTEGER, start_node TEXT NOT NULL, end_node TEXT NOT NULL,
  predicted_end TEXT, p_predicted REAL, hit INTEGER, surprisal REAL, excess REAL, informed INTEGER, surprised INTEGER NOT NULL DEFAULT 0, created_at TEXT
);
CREATE INDEX idx_sequence_executions_name ON sequence_executions(sequence_name, seq_end);
-- 4) 判断の記録の要約（通し番号の区間ごと。元の decision_records は消さない）
CREATE TABLE decision_rollups(
  block_start INTEGER NOT NULL, block_end INTEGER NOT NULL, node TEXT NOT NULL, chosen TEXT NOT NULL, rule TEXT NOT NULL,
  n INTEGER, checked INTEGER, hits INTEGER, sum_surprisal REAL, sum_excess REAL, revisions INTEGER, ambiguous INTEGER,
  PRIMARY KEY(block_start, node, chosen, rule)
);
CREATE TABLE decision_rollup_blocks(block_start INTEGER PRIMARY KEY, block_end INTEGER NOT NULL, rows INTEGER, bytes INTEGER, created_at TEXT);
-- 5) 読み直しを速くする索引（分割・取り消しのたびに全履歴を読まない）
CREATE INDEX idx_stream_state ON experience_stream(state);
CREATE INDEX idx_stream_next ON experience_stream(next_state);
CREATE INDEX idx_reinterp_state ON stream_reinterpretations(state);
CREATE INDEX idx_reinterp_next ON stream_reinterpretations(next_state);
"""),
        new Migration(10, "入力なしの変化と画面ごとの時間的な変化の性質（v0.15.0）: 手と手の間に入力なしで画面が変わった観測（直接の記録。次の手の結果には混ぜない）と、画面の種類ごとの動き続ける所の数（ブロックごと）・判定から外した所の中の変化の量。待つ手（キーを送らない中立の観測）は従来の手と同じ表に action='wait' として入る。行動の出どころ（agent／human）を連続履歴と判断の記録に持つ", """
CREATE TABLE no_input_events(
  event_id INTEGER PRIMARY KEY, run_id INTEGER REFERENCES runs, before_seq INTEGER,
  from_node TEXT NOT NULL, to_node TEXT NOT NULL, transition INTEGER NOT NULL,
  changed_ratio REAL, gap_ms REAL, rechosen INTEGER NOT NULL DEFAULT 0, created_at TEXT
);
CREATE INDEX idx_no_input_from ON no_input_events(from_node);
-- 行動の出どころ（v0.15.0）: agent＝攻略くんが選んだ手、human＝人間が攻略くんの入力経路で送った手（世界の観測としてだけ使う。正解・報酬・推奨にはしない）
ALTER TABLE experience_stream ADD COLUMN action_source TEXT NOT NULL DEFAULT 'agent' CHECK(action_source IN ('agent','human'));
CREATE INDEX idx_stream_source ON experience_stream(action_source);
-- 判断の記録: 規則に human を加え、行動の出どころと入力なしの基準を持つ（CHECK を変えるため作り直す。行はそのまま移す）
CREATE TABLE decision_records_v10(
  decision_id INTEGER PRIMARY KEY, seq INTEGER, turn_id INTEGER,
  node TEXT NOT NULL, chosen TEXT NOT NULL, composite TEXT,
  rule TEXT NOT NULL CHECK(rule IN ('scored','momentum','chord_probe','fallback','sequence_step','human')),
  probability REAL, score REAL,
  predicted_next TEXT, p_predicted REAL, p_change REAL, entropy REAL, uncertainty REAL,
  evidence_direct REAL, evidence_reinterpreted REAL, evidence_prior REAL, prior_sources TEXT,
  actual_next TEXT, changed INTEGER, p_actual REAL, surprisal REAL, excess REAL, hit INTEGER,
  revision_id INTEGER REFERENCES model_revisions,
  candidates_json TEXT, created_at TEXT,
  obs_vis_ratio REAL, obs_mode_ratio REAL, frame_age_ms REAL, obs_ambiguous INTEGER,
  action_source TEXT NOT NULL DEFAULT 'agent' CHECK(action_source IN ('agent','human')),
  p_change_wait REAL, p_action_caused REAL
);
INSERT INTO decision_records_v10(decision_id, seq, turn_id, node, chosen, composite, rule, probability, score, predicted_next, p_predicted, p_change, entropy, uncertainty,
  evidence_direct, evidence_reinterpreted, evidence_prior, prior_sources, actual_next, changed, p_actual, surprisal, excess, hit, revision_id, candidates_json, created_at,
  obs_vis_ratio, obs_mode_ratio, frame_age_ms, obs_ambiguous)
SELECT decision_id, seq, turn_id, node, chosen, composite, rule, probability, score, predicted_next, p_predicted, p_change, entropy, uncertainty,
  evidence_direct, evidence_reinterpreted, evidence_prior, prior_sources, actual_next, changed, p_actual, surprisal, excess, hit, revision_id, candidates_json, created_at,
  obs_vis_ratio, obs_mode_ratio, frame_age_ms, obs_ambiguous FROM decision_records;
DROP TABLE decision_records;
ALTER TABLE decision_records_v10 RENAME TO decision_records;
CREATE INDEX idx_decision_records_seq ON decision_records(seq);
CREATE INDEX idx_decision_records_rel ON decision_records(node, chosen);
-- 出どころ別の手数（後から「誰の手で何を観測したか」を追う）
CREATE VIEW experience_by_source AS SELECT action_source, COUNT(*) AS steps, SUM(changed) AS changed, SUM(action='wait') AS waits FROM experience_stream GROUP BY action_source;
CREATE TABLE screen_motion(
  screen TEXT PRIMARY KEY, width INTEGER NOT NULL, height INTEGER NOT NULL, block INTEGER NOT NULL,
  timeouts INTEGER NOT NULL, counts_json TEXT NOT NULL, activity_turns INTEGER NOT NULL DEFAULT 0, activity_sum REAL NOT NULL DEFAULT 0, updated_at TEXT
);
"""),
        new Migration(11, "自己生成の一時的な目的（v0.16.0）: 目的の作成・達成・失敗・放棄・学習の進みの記録（goal_events。出どころつき）。判断の記録の規則に goal を加える", """
-- 判断の記録: 規則に goal を加える（CHECK を変えるため作り直す。行はそのまま移す）
CREATE TABLE decision_records_v11(
  decision_id INTEGER PRIMARY KEY, seq INTEGER, turn_id INTEGER,
  node TEXT NOT NULL, chosen TEXT NOT NULL, composite TEXT,
  rule TEXT NOT NULL CHECK(rule IN ('scored','momentum','chord_probe','fallback','sequence_step','human','goal')),
  probability REAL, score REAL,
  predicted_next TEXT, p_predicted REAL, p_change REAL, entropy REAL, uncertainty REAL,
  evidence_direct REAL, evidence_reinterpreted REAL, evidence_prior REAL, prior_sources TEXT,
  actual_next TEXT, changed INTEGER, p_actual REAL, surprisal REAL, excess REAL, hit INTEGER,
  revision_id INTEGER REFERENCES model_revisions,
  candidates_json TEXT, created_at TEXT,
  obs_vis_ratio REAL, obs_mode_ratio REAL, frame_age_ms REAL, obs_ambiguous INTEGER,
  action_source TEXT NOT NULL DEFAULT 'agent' CHECK(action_source IN ('agent','human')),
  p_change_wait REAL, p_action_caused REAL
);
INSERT INTO decision_records_v11(decision_id, seq, turn_id, node, chosen, composite, rule, probability, score, predicted_next, p_predicted, p_change, entropy, uncertainty,
  evidence_direct, evidence_reinterpreted, evidence_prior, prior_sources, actual_next, changed, p_actual, surprisal, excess, hit, revision_id, candidates_json, created_at,
  obs_vis_ratio, obs_mode_ratio, frame_age_ms, obs_ambiguous, action_source, p_change_wait, p_action_caused)
SELECT decision_id, seq, turn_id, node, chosen, composite, rule, probability, score, predicted_next, p_predicted, p_change, entropy, uncertainty,
  evidence_direct, evidence_reinterpreted, evidence_prior, prior_sources, actual_next, changed, p_actual, surprisal, excess, hit, revision_id, candidates_json, created_at,
  obs_vis_ratio, obs_mode_ratio, frame_age_ms, obs_ambiguous, action_source, p_change_wait, p_action_caused FROM decision_records;
DROP TABLE decision_records;
ALTER TABLE decision_records_v11 RENAME TO decision_records;
CREATE INDEX idx_decision_records_seq ON decision_records(seq);
CREATE INDEX idx_decision_records_rel ON decision_records(node, chosen);
CREATE TABLE goal_events(
  event_id INTEGER PRIMARY KEY, run_id INTEGER, seq INTEGER, goal_id INTEGER NOT NULL,
  kind TEXT NOT NULL CHECK(kind IN ('reach','retest')), target TEXT NOT NULL, action TEXT,
  status TEXT NOT NULL CHECK(status IN ('achieved','failed','abandoned','reviewed')), reason TEXT,
  steps INTEGER NOT NULL, progress REAL, value_after REAL, origin TEXT NOT NULL, plan TEXT, created_at TEXT
);
CREATE INDEX idx_goal_events_goal ON goal_events(goal_id);
"""),
        new Migration(12, "スキルの芽（v0.18.0）: 操作の並びの候補の出どころ（stream＝連続履歴の反復・goal＝自己生成の目的を同じ道筋で達成した経験）", """
ALTER TABLE action_sequences ADD COLUMN origin TEXT NOT NULL DEFAULT 'stream' CHECK(origin IN ('stream','goal'));
-- 画面ごとのふだんの動きの量（時間切れの手の終わりの区間の、連続するフレームの間の変化率・最大のまとまり）。起動をまたいで引き継ぐ
CREATE TABLE screen_background(screen TEXT PRIMARY KEY, timeouts INTEGER NOT NULL, samples_json TEXT NOT NULL, updated_at TEXT);
"""),
        new Migration(13, "計画の届く手数・小目的・引き直し・目的の結果（v0.20.0）: 目的の記録に Horizon・道筋の手数・引き直し・小目的・結果の量の変化を足し、自己生成の目的の状態を起動をまたいで引き継ぐ", """
ALTER TABLE goal_events ADD COLUMN horizon INTEGER;
ALTER TABLE goal_events ADD COLUMN planned_length INTEGER;
ALTER TABLE goal_events ADD COLUMN replans INTEGER;
ALTER TABLE goal_events ADD COLUMN subgoals INTEGER;
ALTER TABLE goal_events ADD COLUMN subgoals_reached INTEGER;
ALTER TABLE goal_events ADD COLUMN outcome_json TEXT;
-- 自己生成の目的の状態（価値・Horizon・手数ごとの達成の実績・区間の実績・結果の集計）。出どころはすべて goal_events と同じ直接の経験
CREATE TABLE goal_model(key TEXT PRIMARY KEY, json TEXT NOT NULL, updated_at TEXT);
"""),
        new Migration(14, "システムの出来事（v0.21.0 Game Supervisor）: プロセスの終了・ウィンドウの消失・取得の途切れ・再起動・観測し直しなどを、ゲームの経験（状態＋操作→結果）とは別の表に持つ。報酬・価値・予測・並びの材料にせず、連続履歴の前後の通し番号と時刻で時間の上の分析だけに使う", """
CREATE TABLE system_events(
  event_id INTEGER PRIMARY KEY, run_id INTEGER, seq_before INTEGER, kind TEXT NOT NULL CHECK(kind IN (
    'SUPERVISOR_STARTED','OBSERVATION_LOST','WINDOW_LOST','WINDOW_HANDLE_CHANGED','WINDOW_ACQUIRED','WINDOW_RESIZED','CAPTURE_LOST','CAPTURE_RESTORED','INPUT_RECONNECTED',
    'PROCESS_EXITED','SUPERVISOR_RESTART_BEGIN','PROCESS_STARTED','OBSERVATION_RESTORED','EXPLORATION_RESUMED','SUSPECTED_HANG','HANG_CLEARED',
    'RECOVERY_FAILED','RECOVERY_ABORTED','AI_EXCEPTION','DB_CHECKPOINT','FILE_CHANGED','CONFIG_PROBLEM')),
  recovery_id INTEGER, detail TEXT, data_json TEXT, at TEXT NOT NULL
);
CREATE INDEX ix_system_events_run ON system_events(run_id, event_id);
"""),
        new Migration(15, "成長テストの計測（v0.22.0 ブロック B）: 長い実行・起動をまたぐ実行の、Horizon・スキル・引き直し・状態の広がり・目的の価値と結果の集計・短期の達成の推移。記録と健全性の判定だけで、手の選択・報酬には使わない", """
CREATE TABLE growth_snapshots(
  snapshot_id INTEGER PRIMARY KEY, run_id INTEGER, seq INTEGER, step INTEGER, reason TEXT NOT NULL, json TEXT NOT NULL, at TEXT NOT NULL
);
CREATE INDEX ix_growth_snapshots_run ON growth_snapshots(run_id, snapshot_id);
"""),
        new Migration(16, "エピソードの区切りの候補（v0.23.0 ブロック C）: 大きな遷移の後にくり返す流れから統計で見つけた状態。ゲーム固有の区切りの定義は持たず、再起動・起動し直しは区切りにしない。記録と表示だけで、報酬・価値・選択には使わない", """
CREATE TABLE episode_boundary_candidates(
  state TEXT PRIMARY KEY, status TEXT NOT NULL CHECK(status IN ('candidate','withdrawn')), arrivals INTEGER, predecessors INTEGER, large_share REAL,
  flow TEXT, recurrence REAL, predecessors_with_flow INTEGER, context_dependence REAL, first_seq INTEGER, last_seq INTEGER, evidence_json TEXT, updated_at TEXT
);
CREATE TABLE episode_boundary_events(
  event_id INTEGER PRIMARY KEY, state TEXT NOT NULL, kind TEXT NOT NULL CHECK(kind IN ('proposed','withdrawn','reproposed')), seq INTEGER, reason TEXT, at TEXT NOT NULL
);
"""),
        new Migration(17, "構造化した観測（v0.29.0 Observation Layer ブロック 1）: 1 手ごとの層つきの観測（schema_version・provenance つき）。行のない旧経験は legacy coarse observation として区別し、推測で埋めない", """
CREATE TABLE structured_observations(
  obs_id INTEGER PRIMARY KEY, run_id INTEGER, seq INTEGER, step INTEGER, schema_version INTEGER NOT NULL,
  provenance TEXT NOT NULL CHECK(provenance IN ('agent','human','wait')), json TEXT NOT NULL, created_at TEXT NOT NULL
);
CREATE INDEX ix_structured_observations_run ON structured_observations(run_id, step);
INSERT OR REPLACE INTO schema_meta(key, value) VALUES('observation_schema', '1');
"""),
    };


    // ============================================================
    // diagnostics.sqlite（システム診断の記録。U3: 攻略 AI の経験・知識とは完全に分離し、学習の処理からは読まない）
    // ============================================================
    public static readonly IReadOnlyList<Migration> Diagnostics = new[]
    {
        new Migration(1, "診断の記録（分離試験など）", """
CREATE TABLE diag_schema_meta(key TEXT PRIMARY KEY, value TEXT);
CREATE TABLE diag_runs(
  diag_run_id INTEGER PRIMARY KEY, kind TEXT, started_at TEXT, ended_at TEXT, end_reason TEXT,
  input_backend_id TEXT, capture_backend_id TEXT, notes_json TEXT, result_json TEXT
);
CREATE TABLE diag_turns(
  diag_run_id INTEGER REFERENCES diag_runs, turn_index INTEGER, action TEXT, boundary_kind TEXT, latency_ms INTEGER,
  screen_changed INTEGER, game_foreground INTEGER, created_at TEXT,
  PRIMARY KEY(diag_run_id, turn_index)
);
"""),
    };

    // ============================================================
    // app.sqlite（アプリ全体で1つ。memory.sqlite とは独立し、外部キーでつながない）
    // ============================================================
    public static readonly IReadOnlyList<Migration> App = new[]
    {
        new Migration(1, "初期スキーマ（設計書 v2 §5.3 app.sqlite ＋ 導入済み部品）", """
CREATE TABLE app_schema_meta(key TEXT PRIMARY KEY, value TEXT);

CREATE TABLE hardware_snapshots(
  snapshot_id INTEGER PRIMARY KEY, taken_at TEXT,
  os_build TEXT,
  cpu_name TEXT, cores_physical INTEGER, cores_logical INTEGER,
  isa_flags_json TEXT,
  ram_total_mb INTEGER, ram_avail_mb INTEGER,
  gpus_json TEXT,
  cuda_available INTEGER, cuda_driver_version TEXT,
  vulkan_available INTEGER, vulkan_devices_json TEXT,
  disk_free_mb INTEGER
);

CREATE TABLE llm_catalogs(
  catalog_id INTEGER PRIMARY KEY,
  catalog_version TEXT, issued_at TEXT, signature_key_id TEXT,
  source TEXT CHECK(source IN ('bundled','update','manual_import')),
  sha256 TEXT, verified INTEGER, active INTEGER, fetched_at TEXT
);
CREATE TABLE llm_runtimes(
  runtime_id TEXT PRIMARY KEY,
  engine TEXT,
  variant TEXT CHECK(variant IN ('cpu-avx2','cpu-avx512','cuda','vulkan','other')),
  version TEXT, install_path TEXT, size_bytes INTEGER, sha256 TEXT,
  license_spdx TEXT,
  status TEXT CHECK(status IN ('available','downloading','installed','broken','removed')),
  installed_at TEXT, last_used_at TEXT
);
CREATE TABLE llm_models(
  model_id TEXT PRIMARY KEY,
  catalog_id INTEGER REFERENCES llm_catalogs,
  source TEXT CHECK(source IN ('catalog','local_import','external_api')),
  display_name TEXT, family TEXT, params_b REAL,
  quant TEXT,
  file_name TEXT, size_bytes INTEGER, sha256 TEXT,
  context_len INTEGER,
  ram_required_mb INTEGER, vram_required_mb INTEGER,
  supported_variants_json TEXT,
  ja_rating TEXT,
  use_cases_json TEXT,
  license_spdx TEXT, license_url TEXT, license_accepted_at TEXT,
  install_path TEXT,
  status TEXT CHECK(status IN ('available','downloading','installed','broken','removed')),
  installed_at TEXT, last_used_at TEXT
);
CREATE TABLE llm_endpoints(
  endpoint_id INTEGER PRIMARY KEY,
  kind TEXT CHECK(kind IN ('embedded','openai_compatible')),
  base_url TEXT,
  model_name TEXT,
  credential_ref TEXT,
  last_health_at TEXT, last_health_ok INTEGER
);
CREATE TABLE llm_active_config(
  id INTEGER PRIMARY KEY CHECK(id = 1),
  preset TEXT CHECK(preset IN ('light','balanced','reasoning','cpu_first','none','manual')),
  model_id TEXT REFERENCES llm_models, runtime_id TEXT REFERENCES llm_runtimes,
  endpoint_id INTEGER REFERENCES llm_endpoints,
  n_gpu_layers INTEGER, n_threads INTEGER, ctx_size INTEGER,
  run_policy TEXT CHECK(run_policy IN ('between_runs','paused_only','async_low_priority')),
  updated_at TEXT
);
CREATE TABLE llm_switch_events(
  event_id INTEGER PRIMARY KEY,
  profile_id TEXT, run_id INTEGER, at_turn_index INTEGER,
  from_model_id TEXT, to_model_id TEXT,
  state_trace_json TEXT,
  result TEXT CHECK(result IN ('success','rolled_back','disabled_fallback','cancelled')),
  duration_ms INTEGER, error TEXT, created_at TEXT
);

CREATE TABLE backend_probe_results(
  probe_id INTEGER PRIMARY KEY, taken_at TEXT,
  trigger TEXT,
  backend_kind TEXT CHECK(backend_kind IN ('capture','input')),
  backend_id TEXT, backend_version TEXT,
  profile_id TEXT, target_process TEXT, target_window_class TEXT,
  game_window_mode TEXT,
  os_build TEXT, snapshot_id INTEGER REFERENCES hardware_snapshots,
  available INTEGER, unavailable_reason TEXT,
  foreground_ok INTEGER, background_ok INTEGER,
  occluded_ok INTEGER, offscreen_ok INTEGER, other_vdesktop_ok INTEGER, minimized_ok INTEGER,
  fps_p50 REAL, black_frame_ratio REAL,
  latency_ms_p50 INTEGER, latency_ms_p95 INTEGER,
  success_count INTEGER, trial_count INTEGER,
  isolation_level TEXT,
  user_input_interference TEXT,
  requires_admin INTEGER,
  warnings_json TEXT, details_json TEXT
);
CREATE TABLE compat_test_steps(
  probe_id INTEGER REFERENCES backend_probe_results, step_index INTEGER,
  condition TEXT,
  sequence_json TEXT,
  change_score REAL, reverted INTEGER, latency_ms INTEGER,
  result TEXT CHECK(result IN ('accepted','ignored','not_reverted','side_effect','error')),
  frame_before_path TEXT, frame_after_path TEXT,
  PRIMARY KEY(probe_id, step_index)
);
CREATE TABLE backend_selection_history(
  selection_id INTEGER PRIMARY KEY, selected_at TEXT,
  capture_backend_id TEXT, input_backend_id TEXT, placement TEXT,
  mode TEXT CHECK(mode IN ('auto','manual','fallback')),
  user_consent_occupy INTEGER,
  reason TEXT
);

-- 導入済みコンポーネント（manifest.lock と同じ内容の履歴。§4.1.1「manifest.lock / app.sqlite を更新」）
CREATE TABLE component_installs(
  install_id INTEGER PRIMARY KEY,
  component_id TEXT, version TEXT, install_dir TEXT,
  files_json TEXT,
  license_spdx TEXT, license_accepted_at TEXT,
  result TEXT CHECK(result IN ('installed','verified','failed','removed')),
  detail TEXT, created_at TEXT
);
"""),
    };

    // ============================================================
    // experience-vnext.sqlite（S1: 旧 memory.sqlite とは別ファイル。docs/s1-runtime-contract.md）
    // ============================================================
    public const string ExperienceVNextMetaTable = "vnext_schema_meta";
    public static readonly IReadOnlyList<Migration> ExperienceVNext = new[]
    {
        new Migration(1, "S1 Experience vNext 最小スキーマ（契約 docs/s1-runtime-contract.md）", """
CREATE TABLE vnext_schema_meta(key TEXT PRIMARY KEY, value TEXT);
CREATE TABLE experiences(
  experience_id INTEGER PRIMARY KEY,
  seq INTEGER NOT NULL,
  run_id INTEGER,
  created_at TEXT NOT NULL,
  action TEXT NOT NULL,
  action_source TEXT NOT NULL CHECK(action_source IN ('agent','human','wait','unknown')),
  observation_kind TEXT NOT NULL CHECK(observation_kind IN ('structured','legacy_coarse','absent')),
  observation_json TEXT,
  observation_schema_version INTEGER,
  result_changed INTEGER,
  result_next_state TEXT,
  result_change_ratio REAL,
  prediction_json TEXT,
  surprisal REAL,
  excess REAL,
  model_bundle_id TEXT NOT NULL,
  provenance_json TEXT NOT NULL,
  source_db TEXT,
  source_seq INTEGER
);
CREATE INDEX ix_experiences_seq ON experiences(seq);
CREATE INDEX ix_experiences_bundle ON experiences(model_bundle_id);
CREATE TABLE loss_reports(
  report_id INTEGER PRIMARY KEY,
  created_at TEXT NOT NULL,
  source_path TEXT NOT NULL,
  adapter_version TEXT NOT NULL,
  json TEXT NOT NULL
);
CREATE TABLE model_bundles(
  model_bundle_id TEXT PRIMARY KEY,
  created_at TEXT NOT NULL,
  json TEXT NOT NULL
);
INSERT OR REPLACE INTO vnext_schema_meta(key, value) VALUES('contract', 's1-runtime-contract-0.1');
"""),
        new Migration(2, "S2: observation_kind に bundle を追加（Observation Bundle 正規観測面）", """
CREATE TABLE experiences_v2(
  experience_id INTEGER PRIMARY KEY,
  seq INTEGER NOT NULL,
  run_id INTEGER,
  created_at TEXT NOT NULL,
  action TEXT NOT NULL,
  action_source TEXT NOT NULL CHECK(action_source IN ('agent','human','wait','unknown')),
  observation_kind TEXT NOT NULL CHECK(observation_kind IN ('structured','legacy_coarse','absent','bundle')),
  observation_json TEXT,
  observation_schema_version INTEGER,
  result_changed INTEGER,
  result_next_state TEXT,
  result_change_ratio REAL,
  prediction_json TEXT,
  surprisal REAL,
  excess REAL,
  model_bundle_id TEXT NOT NULL,
  provenance_json TEXT NOT NULL,
  source_db TEXT,
  source_seq INTEGER
);
INSERT INTO experiences_v2 SELECT * FROM experiences;
DROP TABLE experiences;
ALTER TABLE experiences_v2 RENAME TO experiences;
CREATE INDEX ix_experiences_seq ON experiences(seq);
CREATE INDEX ix_experiences_bundle ON experiences(model_bundle_id);
INSERT OR REPLACE INTO vnext_schema_meta(key, value) VALUES('contract', 's2-sensor-stack-contract-0.2');
"""),
    };
}
