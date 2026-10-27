using GameKouryakuKun.Core.Exploration;
using Xunit;

namespace GameKouryakuKun.Core.Tests;

/// <summary>
/// 手探り探索のテスト。対象ゲームの流れを写した仮想ゲームは使わない（最上位原則 1・2）。
/// 乱数で作るゲーム非依存の状態遷移グラフだけで、汎用の性質（進み続ける・可逆性を学ぶ・同時押しを見つける・入力不達の判定）を確かめる。
/// </summary>
public class ExplorerTests
{
    private static readonly string[] Keys = { "dpad_up", "dpad_down", "dpad_left", "dpad_right", "button1", "button2", "button3", "button4", "button5", "button6" };

    /// <summary>乱数で作る状態遷移グラフ（各ノード・各キーの結果: 変化なし／同じノードで画面だけ変化／別のノードへ）。強連結になるよう環状の辺を1本足す</summary>
    private sealed class RandomWorld
    {
        private readonly Dictionary<(int, string), (int To, bool Changed)> _t = new();
        public int Node;
        public RandomWorld(int nodes, int seed)
        {
            var r = new Random(seed);
            for (int n = 0; n < nodes; n++)
            {
                foreach (var k in Keys)
                {
                    double p = r.NextDouble();
                    _t[(n, k)] = p < 0.45 ? (n, false) : p < 0.65 ? (n, true) : (r.Next(nodes), true);
                }
                _t[(n, Keys[r.Next(Keys.Length)])] = ((n + 1) % nodes, true);   // 強連結にする
            }
        }
        public (string Node, bool Changed) Apply(ExploreAction a)
        {
            var (to, ch) = a.Group == ActionGroup.Chord ? (Node, false) : _t[(Node, a.Keys[0])];
            if (to != Node) ch = true;
            Node = to;
            return ("m" + Node, ch);
        }
    }

    private static Explorer New(int seed = 7) => new(Explorer.FromLogicalKeys(Keys), seed);

    [Fact]
    public void Logical_keys_become_neutral_actions_grouped_only_by_body_structure()
    {
        var acts = Explorer.FromLogicalKeys(Keys);
        Assert.Equal(Keys.Length, acts.Count);
        Assert.Equal(4, acts.Count(a => a.Group == ActionGroup.Dpad));
        Assert.Equal(6, acts.Count(a => a.Group == ActionGroup.Button));
        Assert.DoesNotContain(acts, a => a.Group == ActionGroup.Chord);   // 同時押しは事前に与えず、探索で見つける
    }

    [Fact]
    public void Keeps_exploring_random_worlds_and_covers_most_states()
    {
        for (int seed = 1; seed <= 6; seed++)
        {
            var w = new RandomWorld(20, seed); var ex = New(seed);
            var seen = new HashSet<string>();
            for (int i = 0; i < 3000; i++)
            {
                string from = "m" + w.Node;
                var c = ex.Choose(from);
                var (to, ch) = w.Apply(c.Action);
                ex.Observe(from, c.Action, to, ch);
                seen.Add(to);
                Assert.True(ex.NoChangeStreak < 300, $"seed {seed}");
            }
            Assert.True(seen.Count >= 18, $"seed {seed}: 訪れたノード {seen.Count}/20");
        }
    }

    [Fact]
    public void Without_evidence_no_key_is_preferred_or_deferred()
    {
        // 事前の重みなし: 初めての画面では、まだ試していない操作を順に試す（どのキーも特別扱いしない）
        var ex = New(11);
        var first = new HashSet<string>();
        for (int i = 0; i < Keys.Length * 3 && first.Count < Keys.Length; i++)
        {
            var c = ex.Choose("m1");
            if (c.Action.Group != ActionGroup.Chord) first.Add(c.Action.Name);
            ex.Observe("m1", c.Action, "m1", false);
        }
        Assert.Equal(Keys.Length, first.Count);
        Assert.All(Keys, k => Assert.Equal(0.5, ex.ReversibilityByAction.TryGetValue(k, out var r) ? r.Rate : 0.5, 3));
    }

    [Fact]
    public void Keys_that_often_led_to_no_return_are_deferred_until_others_were_tried()
    {
        var ex = new Explorer(Explorer.FromLogicalKeys(Keys), 5) { Momentum = 0, StagnationTurns = 1000 };
        ex.LoadReversibility(new[] { ("button3", 0, 6, 0) });   // 観測: button3 の後は 6 回とも元の画面へ戻れなかった
        var tried = new Dictionary<string, int>();
        int firstB3 = -1;
        for (int i = 0; i < 60 && firstB3 < 0; i++)
        {
            var c = ex.Choose("m7");
            if (c.Action.Name == "button3") { firstB3 = i; break; }
            tried[c.Action.Name] = tried.GetValueOrDefault(c.Action.Name) + 1;
            ex.Observe("m7", c.Action, "m7", false);
        }
        Assert.True(firstB3 > 0, "後回しの操作が最後まで試されない（禁止ではなく後回しのはず）");
        Assert.All(Keys.Where(k => k != "button3"), k => Assert.True(tried.GetValueOrDefault(k) >= 2, $"{k} を2回試す前に button3 を押した"));
    }

    [Fact]
    public void Deferred_keys_are_released_when_the_screen_stagnates()
    {
        // 後回しは禁止ではない: ほかの操作で何も進まなければ試す（未知の画面で止まらない）
        var ex = new Explorer(Explorer.FromLogicalKeys(new[] { "button1", "button2" }), 2) { Momentum = 0, DeferUntilOthersTried = 1000 };
        ex.LoadReversibility(new[] { ("button2", 0, 6, 0) });
        bool pressed = false;
        for (int i = 0; i < 100 && !pressed; i++)
        {
            var c = ex.Choose("m1");
            pressed = c.Action.Name == "button2";
            ex.Observe("m1", c.Action, "m1", true);
        }
        Assert.True(pressed);
    }

    [Fact]
    public void Reversibility_and_undo_are_learned_from_observation()
    {
        var ex = new Explorer(Explorer.FromLogicalKeys(Keys), 3) { Momentum = 0 };
        var b1 = ex.Actions.First(a => a.Name == "button1"); var b2 = ex.Actions.First(a => a.Name == "button2"); var b4 = ex.Actions.First(a => a.Name == "button4"); var b5 = ex.Actions.First(a => a.Name == "button5");
        for (int i = 0; i < 5; i++)
        {
            ex.Observe("mA", b1, "mB", true);   // A → B
            ex.Observe("mB", b2, "mA", true);   // B → A（戻す操作）
        }
        for (int i = 0; i < 5; i++)
        {
            ex.Observe("mA", b4, $"mX{i}", true);   // A → 新しい画面（戻らない）
            for (int k = 0; k < 7; k++) ex.Observe($"mX{i}", b2, $"mX{i}", false);
            ex.Observe($"mX{i}", b5, "mA", true);
        }
        var rev = ex.ReversibilityByAction;
        Assert.True(rev["button1"].Rate > 0.7);
        Assert.True(rev["button2"].UndoHits >= 5);
        Assert.True(rev["button4"].Rate < 0.35);
    }

    [Fact]
    public void Chords_are_never_invented_or_promoted_by_exploration()
    {
        // v0.26.0: 同時押しは Profile の chords だけ。探索が勝手に組を作ったり、画面を変えた組を候補に加えたりしない
        var ex = new Explorer(Explorer.FromLogicalKeys(Keys), 9);
        for (int i = 0; i < 2000; i++)
        {
            var c = ex.Choose("m1");
            Assert.NotEqual(ActionGroup.Chord, c.Action.Group);
            ex.Observe("m1", c.Action, "m1", true);
        }
        ex.Observe("m1", Explorer.ChordOf(new[] { "dpad_up", "dpad_right" }), "m2", true);
        Assert.DoesNotContain(ex.Actions, a => a.Group == ActionGroup.Chord);
    }

    [Fact]
    public void No_change_streak_is_only_recorded_and_resets_on_change()
    {
        // v0.15.0: 画面が反応しない（B）ことは停止の条件ではない。連続数は観測として数えるだけ
        var ex = New();
        for (int i = 0; i < 300; i++) { var c = ex.Choose("m1"); ex.Observe("m1", c.Action, "m1", false); }
        Assert.Equal(300, ex.NoChangeStreak);
        ex.Observe("m1", ex.Actions[0], "m1", true);
        Assert.Equal(0, ex.NoChangeStreak);
    }

    [Fact]
    public void Same_seed_gives_same_choices_and_transitions_can_be_reloaded()
    {
        var a = New(3); var b = New(3);
        for (int i = 0; i < 50; i++)
        {
            var ca = a.Choose("m1"); var cb = b.Choose("m1");
            Assert.Equal(ca.Action.Name, cb.Action.Name);
            a.Observe("m1", ca.Action, i % 7 == 0 ? "m2" : "m1", true); b.Observe("m1", cb.Action, i % 7 == 0 ? "m2" : "m1", true);
        }
        var c = New(); c.Load(a.Transitions());
        Assert.Equal(a.Transitions().Sum(t => t.N), c.Transitions().Sum(t => t.N));
        var d = New(); d.Load(new[] { ("m1", "old_name", "m2", 3) });   // 今の Profile にないキー名の記録は使わない
        Assert.Empty(d.Transitions());
    }
}

/// <summary>連続経験の系列学習（2026-10-18 09:36）。区切り（1件の範囲・冒険・実行単位）は与えず、反復・循環・変化点・予測の外れから見つける</summary>
public class SequenceLearnerTests
{
    private static IEnumerable<StreamStep> Stream(IEnumerable<(string S, string A, string N, double R)> xs, long from = 1)
    {
        long q = from;
        foreach (var (s, a, n, r) in xs) yield return new StreamStep(q++, s, a, s != n, n, r);
    }

    private static IEnumerable<(string, string, string, double)> Noise(Random rnd, int count, string[] states, string[] acts, double ratio)
    {
        string cur = states[0];
        for (int i = 0; i < count; i++)
        {
            var a = acts[rnd.Next(acts.Length)]; var n = states[rnd.Next(states.Length)];
            yield return (cur, a, n, ratio * (0.5 + rnd.NextDouble()));
            cur = n;
        }
    }

    [Fact]
    public void Repeated_motif_and_cycle_are_found_without_any_boundary_given()
    {
        var rnd = new Random(1);
        var L = new SequenceLearner();
        var st = new[] { "s1", "s2", "s3", "s4", "s5", "s6" }; var ac = new[] { "button1", "button2", "dpad_up", "dpad_down" };
        var xs = new List<(string, string, string, double)>();
        for (int k = 0; k < 8; k++)
        {
            xs.AddRange(Noise(rnd, 20, st, ac, 0.05));
            // 埋め込んだ循環 a → b → c → a（入力も固定）
            xs.Add(("a", "dpad_left", "b", 0.1)); xs.Add(("b", "button3", "c", 0.1)); xs.Add(("c", "button4", "a", 0.1));
        }
        foreach (var s in Stream(xs)) L.Observe(s);
        Assert.Contains(L.Patterns, p => p.Kind == SequencePatternKind.Cycle && p.Signature == SequenceLearner.CanonicalRotation(new[] { "a", "b", "c" }) && p.Count >= 3);
        Assert.Contains(L.Patterns, p => p.Kind == SequencePatternKind.Motif && p.Signature.Contains("a|dpad_left|+ b|button3|+") && p.Count >= 3);
        // ID は 1 からの通し番号（意味は持たない）
        Assert.Equal(Enumerable.Range(1, L.Patterns.Count), L.Patterns.Select(p => p.Id).OrderBy(x => x));
    }

    [Fact]
    public void Boundaries_appear_near_regime_switches_from_statistics_only()
    {
        var rnd = new Random(2);
        var L = new SequenceLearner();
        var A = new[] { "a1", "a2", "a3" }; var B = new[] { "b1", "b2", "b3" }; var ac = new[] { "button1", "dpad_up" };
        var xs = new List<(string, string, string, double)>();
        var switches = new List<int>();
        for (int k = 0; k < 6; k++)
        {
            xs.AddRange(Noise(rnd, 120, A, ac, 0.02));
            switches.Add(xs.Count + 1);
            xs.AddRange(Noise(rnd, 120, B, ac, 0.6));
            switches.Add(xs.Count + 1);
        }
        var hits = new List<long>();
        foreach (var s in Stream(xs)) if (L.Observe(s).Boundary.Count > 0) hits.Add(s.Seq);
        Assert.NotEmpty(hits);
        int near = switches.Take(switches.Count - 1).Count(sw => hits.Any(h => h >= sw && h < sw + 15));
        Assert.True(near >= switches.Count / 2, $"切り替え {switches.Count - 1} 回のうち近くに境界 {near}");
    }

    [Fact]
    public void Prediction_error_is_measured_against_the_usual_uncertainty_of_each_state_and_key()
    {
        // いつも同じ結果になる組（x+dpad_left → y）がまれに外れたときは境界、ふだんから結果がばらつく組（n*+button*）は境界にしない。
        // 予測しやすい組と予測できない組が混ざる所で、v0.8.1 の文脈差の方式では外れがほとんど出なかった（実機診断）の再現を含む
        var rnd = new Random(5);
        var L = new SequenceLearner();
        var acts = new[] { "button1", "button2", "button3" };
        long q = 1; var violations = new List<long>(); var hits = new List<long>(); var lateNoisy = 0;
        string cur = "x";
        void Step(string a, string n, bool noisy)
        {
            var o = L.Observe(new StreamStep(q, cur, a, cur != n, n, 0.1));
            if (o.Boundary.Contains(BoundaryReason.Surprisal)) { hits.Add(q); if (noisy && q > 500) lateNoisy++; }
            q++; cur = n;
        }
        for (int b = 0; b < 300; b++)
        {
            cur = "x";
            bool violate = b > 40 && b % 15 == 0;
            if (violate) violations.Add(q);
            Step("dpad_left", violate ? "w" : "y", false);
            cur = "n" + rnd.Next(3);
            for (int i = 0; i < 4; i++) Step(acts[rnd.Next(acts.Length)], "n" + rnd.Next(3), true);
        }
        int found = violations.Count(v => hits.Contains(v));
        Assert.True(found >= violations.Count * 0.8, $"いつも同じ結果の組の外れ {violations.Count} 回のうち境界 {found}");
        // 学び始め（各組の観測がまだ数回）は、ばらつく組でも偏った回数から外れが出ることがある（統計として正しい）。観測がたまった後は出ない
        Assert.True(lateNoisy == 0, $"観測がたまった後の、結果がばらつく組での境界 {lateNoisy}（全 {hits.Count}）");
    }

    [Fact]
    public void Unseen_state_key_pairs_are_not_counted_as_prediction_error()
    {
        // 初めての組ばかり（新しい状態が次々に出る）の系列では、外れは 0 で境界も出ない（未知は外れではない）
        var L = new SequenceLearner(); int hits = 0;
        for (int i = 1; i <= 600; i++)
        {
            var o = L.Observe(new StreamStep(i, "u" + i, "button1", true, "u" + (i + 1), 0.1));
            Assert.True(Math.Abs(o.Excess) < 1e-9, $"未知の組の外れ {o.Excess}");
            if (o.Boundary.Contains(BoundaryReason.Surprisal) || o.Boundary.Contains(BoundaryReason.PredictionShift)) hits++;
        }
        Assert.Equal(0, hits);
    }

    [Fact]
    public void Bayesian_change_point_finds_a_switch_of_the_transition_table()
    {
        // 状態と入力の組の結果の決まり方（遷移の表）が途中で入れ替わる。画面の変化量は一定で、手がかりは予測の外れ方だけ
        var rnd = new Random(8);
        var st = new[] { "s0", "s1", "s2", "s3", "s4" }; var ac = new[] { "button1", "button2", "dpad_up" };
        // 区間ごとに新しい表（各 (状態, 入力) の行き先を無作為に決める）。前と同じ表に戻る切り替えは、外れ方が変わらないので数えない
        var tables = Enumerable.Range(0, 4).Select(_ => st.SelectMany(x => ac.Select(a => (x, a))).ToDictionary(k => k, _ => st[rnd.Next(st.Length)])).ToArray();
        string Next(int table, string s0, string a) => tables[table][(s0, a)];
        var L = new SequenceLearner(); var shifts = new List<long>(); var switches = new List<long>();
        string cur = "s0"; long q = 1;
        for (int seg = 0; seg < 4; seg++)
        {
            int table = seg; if (seg > 0) switches.Add(q);
            for (int i = 0; i < 400; i++)
            {
                var a = ac[rnd.Next(ac.Length)]; var n = rnd.NextDouble() < 0.05 ? st[rnd.Next(st.Length)] : Next(table, cur, a);
                if (L.Observe(new StreamStep(q, cur, a, cur != n, n, 0.1)).Boundary.Contains(BoundaryReason.PredictionShift)) shifts.Add(q);
                q++; cur = n;
            }
        }
        // どの切り替えも直後（20 手以内）に見つかる
        foreach (var sw in switches) Assert.True(shifts.Any(h => h >= sw && h < sw + 20), $"切り替え {sw} の直後に変化点なし（{string.Join(",", shifts)}）");
        // 切り替えの後、新しい表を覚えるにつれ外れの率が下がるのも率の変化なので、そこで出る変化点は数えない。
        // 区間の後半（表を覚え終えて率が一定の所）での変化点は少ない
        int quiet = shifts.Count(h => (h - 1) % 400 >= 200);
        Assert.True(quiet <= 2, $"区間の後半の変化点 {quiet}（{string.Join(",", shifts)}）");
    }

    [Fact]
    public void Bayesian_change_point_stays_quiet_on_stationary_values()
    {
        var rnd = new Random(9); var d = new BayesianChangePoint(); int n = 0;
        for (int i = 0; i < 3000; i++) if (d.Observe(rnd.NextDouble() < 0.08)) n++;
        Assert.True(n <= 6, $"率が一定の系列での変化点 {n}");   // 500 手に 1 回未満
        for (int i = 0; i < 200; i++) if (d.Observe(rnd.NextDouble() < 0.6) && i < 30) n += 100;
        Assert.True(n >= 100, "率の大きな変化を見つけない");
    }

    [Fact]
    public void Change_point_detects_mean_shift_and_not_stationary_noise()
    {
        var rnd = new Random(3);
        var d = new ChangePointDetector();
        int early = 0;
        for (int i = 0; i < 300; i++) if (d.Observe(0.1 + 0.01 * rnd.NextDouble())) early++;
        int at = -1;
        for (int i = 0; i < 100 && at < 0; i++) if (d.Observe(0.5 + 0.01 * rnd.NextDouble())) at = i;
        Assert.True(early <= 1, $"定常で {early} 回");
        Assert.InRange(at, 0, 30);
    }

    [Fact]
    public void Surprisal_drops_for_learned_transitions()
    {
        var p = new TransitionPredictor();
        double first = p.Surprisal("x", "button1", "y");
        for (int i = 0; i < 20; i++) p.Observe("x", "button1", "y");
        Assert.True(p.Surprisal("x", "button1", "y") < first);
        Assert.True(p.Surprisal("x", "button1", "z") > p.Surprisal("x", "button1", "y"));
    }

    [Fact]
    public void Replay_with_loaded_patterns_keeps_ids_stable()
    {
        var xs = Stream(Enumerable.Range(0, 30).SelectMany(_ => new[] { ("a", "button1", "b", 0.1), ("b", "button2", "a", 0.1) })).ToList();
        var L1 = new SequenceLearner();
        foreach (var s in xs) L1.Observe(s);
        var saved = L1.Patterns.Select(p => new SequencePattern { Id = p.Id + 10, Kind = p.Kind, Signature = p.Signature, Length = p.Length, Count = p.Count, FirstSeq = p.FirstSeq, LastSeq = p.LastSeq }).ToList();
        var L2 = new SequenceLearner();
        L2.LoadPatterns(saved);
        L2.Replay(xs.Skip(20));
        foreach (var p in saved) Assert.Contains(L2.Patterns, q => q.Kind == p.Kind && q.Signature == p.Signature && q.Id == p.Id && q.Count >= p.Count);
        var o = L2.Observe(new StreamStep(1000, "c", "button3", true, "d", 0.1));
        Assert.All(o.NewPatterns, p => Assert.True(p.Id > saved.Max(s => s.Id)));
    }

    [Fact]
    public void Learner_api_has_no_run_or_episode_parameter()
    {
        foreach (var t in new[] { typeof(SequenceLearner), typeof(StreamStep), typeof(SequencePattern), typeof(SequenceObservation) })
        {
            foreach (var m in t.GetMembers())
                Assert.DoesNotMatch("(?i)run|episode|adventure|excursion", m.Name);
            foreach (var m in t.GetMethods())
                foreach (var prm in m.GetParameters()) Assert.DoesNotMatch("(?i)run|episode", prm.Name ?? "");
        }
    }
}

public class ScreenModeBookTests
{
    [Fact]
    public void Similar_screens_share_a_mode_and_different_ones_get_new_modes()
    {
        var book = new ScreenModeBook();
        var a1 = ScreenSignature.Of(Canvas.Checker(160, 90, 8, 0, 0).ToFrame(0));
        var a2 = ScreenSignature.Of(Canvas.Checker(160, 90, 8, 3, 0).ToFrame(0));   // 同じ見た目がずれただけ
        var dark = ScreenSignature.Of(new Canvas(160, 90, 5).ToFrame(0));
        var green = ScreenSignature.Of(new Canvas(160, 90, 60).Rect(0, 0, 160, 90, 20, 200, 30).ToFrame(0));
        var (m1, n1, _) = book.Assign(a1); var (m2, n2, _) = book.Assign(a2); var (m3, n3, _) = book.Assign(dark); var (m4, n4, _) = book.Assign(green);
        Assert.True(n1); Assert.False(n2); Assert.Equal(m1, m2);
        Assert.True(n3); Assert.True(n4); Assert.NotEqual(m3, m4);
        var reloaded = new ScreenModeBook(); reloaded.Load(book.Modes.Select(m => (m.Id, m.Prototype, m.Seen)));
        Assert.Equal(m3, reloaded.Assign(dark).Mode);
    }

    [Fact]
    public void Node_name_includes_coarse_panel_layout()
    {
        Assert.Equal("m3", ScreenModeBook.NodeName(3, ""));
        var lay = ScreenModeBook.PanelLayout(new[] { new Imaging.PanelRect(130, 400, 600, 80) }, 1200, 600);
        Assert.Equal("m3|1,4,3", ScreenModeBook.NodeName(3, lay));
        Assert.True(ScreenModeBook.HasPanel("m3|1,4,3"));
    }
}

public class AdaptiveTurnTimeoutTests
{
    [Fact]
    public void Limit_follows_measured_settle_times_within_bounds()
    {
        var t = new Turn.AdaptiveTurnTimeout();
        Assert.Equal(8000, t.Current);
        for (int i = 0; i < 30; i++) t.Add(400 + i);
        Assert.Equal(1500, t.Current);   // 3×p90 ≈ 1280 → 下限
        for (int i = 0; i < 200; i++) t.Add(1000);
        Assert.Equal(3000, t.Current, 0);
        for (int i = 0; i < 200; i++) t.Add(5000);
        Assert.Equal(8000, t.Current);
    }

    [Fact]
    public void Damping_reduces_oscillation_and_never_waits_less_than_the_statistical_target()
    {
        // 応答の遅れが二峰（多くは速く、約5%だけ遅い）だと、95 パーセンタイルが速い側と遅い側を行き来する（v0.8.0 の実機診断）
        var rnd = new Random(11);
        var t = new Turn.AdaptiveTurnTimeout();
        int rawFlips = 0, applied = 0; double prevRaw = t.RawNoChange, prevApplied = t.NoChangeCurrent;
        for (int i = 0; i < 2000; i++)
        {
            t.AddFirstChange(rnd.NextDouble() < 0.05 ? 560 + rnd.Next(40) : 200 + rnd.Next(100));
            // 実機の記録（turn_timeout_adapted）と同じく 10% を超える変化を数える
            if (i >= 20 && Math.Abs(t.RawNoChange - prevRaw) > 0.1 * prevRaw) rawFlips++;
            if (i >= 20 && Math.Abs(t.NoChangeCurrent - prevApplied) > 0.1 * prevApplied) applied++;
            prevRaw = t.RawNoChange; prevApplied = t.NoChangeCurrent;
            Assert.True(t.NoChangeCurrent >= t.RawNoChange - 1e-9, "使う値が目標値より短い");
        }
        Assert.True(rawFlips >= 20, $"再現できていない: 目標値の大きな変化 {rawFlips}");
        Assert.True(applied * 5 <= rawFlips, $"使う値の変化 {applied}・目標値の変化 {rawFlips}");
    }

    [Fact]
    public void Damping_raises_immediately_and_lowers_only_after_sustained_evidence()
    {
        var t = new Turn.AdaptiveTurnTimeout();
        for (int i = 0; i < 30; i++) t.AddFirstChange(300);
        Assert.Equal(600, t.NoChangeCurrent);                  // 初期値から統計の値へはすぐ
        for (int i = 0; i < 30; i++) t.AddFirstChange(1500);   // 遅くなった → すぐ上げる
        Assert.Equal(3000, t.NoChangeCurrent);
        for (int i = 0; i < 199; i++) t.AddFirstChange(300);   // 窓（200）から遅い標本が抜けきるまでは下げない
        Assert.Equal(3000, t.NoChangeCurrent);
        for (int i = 0; i < 60; i++) t.AddFirstChange(300);    // 低い目標が続いたことを確かめてから下げる
        Assert.Equal(600, t.NoChangeCurrent);
    }

    [Fact]
    public void Turn_limit_never_drops_below_the_nochange_wait()
    {
        var t = new Turn.AdaptiveTurnTimeout();
        for (int i = 0; i < 40; i++) { t.Add(400); t.AddFirstChange(200); }   // 速く落ち着く画面
        for (int i = 0; i < 30; i++) t.AddLateChange(t.NoChangeCurrent);           // 遅い変化で「変化なし」の待ちが伸びる
        Assert.True(t.Current >= t.NoChangeCurrent * 1.25 - 1e-9 || t.Current == t.MaxMs, $"{t.Current} {t.NoChangeCurrent}");
    }

    [Fact]
    public void Late_changes_lift_the_nochange_wait_instead_of_letting_it_sink_to_the_floor()
    {
        // 実測できた遅れ（早い応答）だけだと待ち時間は下限に張り付き、遅い応答を「変化なし」と誤る（打ち切りの偏り。v0.7.0 の実機診断）
        var t = new Turn.AdaptiveTurnTimeout();
        for (int i = 0; i < 100; i++) t.AddFirstChange(100);
        Assert.Equal(600, t.NoChangeCurrent);
        for (int i = 0; i < 10; i++) t.AddLateChange(t.NoChangeCurrent);   // 判定の後に変化した（遅れは待ち時間より長い）
        Assert.True(t.NoChangeCurrent > 600, $"{t.NoChangeCurrent}");
        Assert.Equal(10, t.LateChanges);
        Assert.True(t.NoChangeCurrent <= t.NoChangeMaxMs);
    }
}
