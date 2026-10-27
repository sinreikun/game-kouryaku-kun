using System.Security.Cryptography;
using GameKouryakuKun.Core.Exploration;
using GameKouryakuKun.Core.Ui;
using GameKouryakuKun.Storage;
using Xunit;

namespace GameKouryakuKun.Core.Tests;

/// <summary>v0.12.0: 観測の確かさ・並びの実行の評価・判断の記録の要約・DB の点検・表示の設定の分離</summary>
public class BlockV012Tests
{
    private static string Src(string rel) => File.ReadAllText(Path.Combine(TestPaths.SrcRoot(), rel));

    [Fact]
    public void Ambiguous_observations_do_not_trigger_model_revision_but_are_still_checked()
    {
        // 決まった結果のあと、結果が変わる。境目の近くの観測だけなら作り直さない。確かな観測なら作り直す
        ModelRevision? Run(bool ambiguous)
        {
            var m = new TransitionModel();
            long q = 0; ModelRevision? first = null;
            for (int i = 0; i < 60; i++) m.Observe(++q, "s", "a", "x", true);
            for (int i = 0; i < 60 && first == null; i++) first = m.Observe(++q, "s", "a", "y", true, ambiguous: ambiguous).Revision;
            return first;
        }
        Assert.NotNull(Run(false));
        Assert.Null(Run(true));
        var m2 = new TransitionModel();
        m2.Observe(1, "s", "a", "x", true, ambiguous: true);
        Assert.Equal(1, m2.Checks);
        Assert.Equal(1, m2.AmbiguousSkipped);
    }

    [Fact]
    public void Observation_quality_is_stored_with_each_decision()
    {
        using var d = new TempDir();
        using var s = ExperienceStore.Open(d["p"], "prof", "1", "0.12.0");
        long run = s.BeginRun("1", "b", null, policyVersion: "explore-0.3");
        s.AppendStream(run, null, new StreamStep(1, "s", "a", true, "t", 0.1), 1, Array.Empty<BoundaryReason>());
        s.AddDecision(new DecisionRecord(1, null, "s", "a", null, "scored", 0.5, 1, Array.Empty<CandidateScore>(), null, null, null)
            { ObsVisRatio = 0.81234567, ObsModeRatio = double.NaN, FrameAgeMs = 12.5, Ambiguous = true });
        var row = s.Db.Query("SELECT obs_vis_ratio, obs_mode_ratio, frame_age_ms, obs_ambiguous FROM decision_records", r => (r.GetDouble(0), r.IsDBNull(1), r.GetDouble(2), r.GetInt32(3))).Single();
        Assert.Equal((0.8123, true, 12.5, 1), row);
    }

    private static IEnumerable<StreamStep> Loop(long from, int n)
    {
        // S -a→ X -b→ T -c→ S を繰り返す（並び a > b が S から T へ再現する）
        string[] st = { "S", "X", "T" }, ac = { "a", "b", "c" };
        for (long i = 0; i < n; i++) { int k = (int)((from + i) % 3); yield return new StreamStep(from + i, st[k], ac[k], true, st[(k + 1) % 3], 0.1); }
    }

    [Fact]
    public void Sequences_whose_executions_keep_surprising_go_dormant_and_can_come_back_later()
    {
        var m = new ActionSequenceMiner();
        m.Replay(Loop(0, 300));
        var c = m.Active.First(x => x.Steps.SequenceEqual(new[] { "a", "b" }));
        long q = 300;
        for (int i = 0; i < 2; i++) m.ObserveExecution(c.Id, q, true);
        m.Replay(Loop(q, 3)); q += 3;
        Assert.Equal(SequenceStatus.Active, c.Status);   // 2 回だけでは休止しない
        m.ObserveExecution(c.Id, q, true);
        var evs = new List<SequenceEvent>(); foreach (var st in Loop(q, 3)) evs.AddRange(m.Observe(st)); q += 3;
        Assert.Equal(SequenceStatus.Dormant, c.Status);
        Assert.Contains(evs, e => e.Kind == "dormant" && e.Reason.Contains("実行"));
        // 当たる実行が続けば、外れの割合は下がる（同じ並びの評価が経験で変わる）
        Assert.True(c.RecentExecutionSurprise(q, m.DormantAfterSteps).Rate > 0.4);
        // 古い実行は数えないので、時間がたてば再び候補になれる
        evs.Clear(); foreach (var st in Loop(q, m.DormantAfterSteps + 10)) evs.AddRange(m.Observe(st));
        Assert.Equal(SequenceStatus.Active, c.Status);
        Assert.Contains(evs, e => e.Kind == "reactivated" && e.Candidate.Id == c.Id);
    }

    [Fact]
    public void Sequence_executions_are_stored_and_reload_into_the_miner()
    {
        using var d = new TempDir();
        using var s = ExperienceStore.Open(d["p"], "prof", "1", "0.12.0");
        var m = new ActionSequenceMiner(); m.Replay(Loop(0, 300));
        s.SaveActionSequences(m.Candidates);
        var c = s.LoadActionSequences().First(x => x.Steps.SequenceEqual(new[] { "a", "b" }));
        var p = new PredictedOutcome("S", c.Name, new[] { ("T", 0.9) }, 1, 0.1, 5, 0, 0, Array.Empty<string>(), 0.1);
        for (int i = 0; i < 3; i++) s.AddSequenceExecution(c.Id, c.Name, 300 + i, "S", "X", new PredictionCheck(p, "X", true, 0.05, 4.3, 3.2, false, 0), true, true);
        var m2 = new ActionSequenceMiner(); m2.Load(s.LoadActionSequences());
        foreach (var (sid, sq, sur) in s.LoadSequenceExecutions()) m2.ObserveExecution(sid, sq, sur);
        var c2 = m2.Candidates.First(x => x.Id == c.Id);
        Assert.Equal(3, c2.Executions.Count);
        Assert.Equal(3, Convert.ToInt32(s.Db.Scalar("SELECT SUM(surprised) FROM sequence_executions")));
    }

    [Fact]
    public void Decision_rollups_are_idempotent_and_leave_original_records_untouched()
    {
        using var d = new TempDir();
        using var s = ExperienceStore.Open(d["p"], "prof", "1", "0.12.0");
        long run = s.BeginRun("1", "b", null, policyVersion: "explore-0.3");
        for (long i = 0; i < 25; i++)
        {
            s.AppendStream(run, null, new StreamStep(i, "s", i % 2 == 0 ? "a" : "b", true, "t", 0.1), 1, Array.Empty<BoundaryReason>());
            s.AddDecision(new DecisionRecord(i, null, "s", i % 2 == 0 ? "a" : "b", null, "scored", 0.5, 1, Array.Empty<CandidateScore>(), null, null, null));
        }
        string Hash() => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join("|", s.Db.Query("SELECT * FROM decision_records ORDER BY decision_id", r => string.Join(",", Enumerable.Range(0, r.FieldCount).Select(k => r.IsDBNull(k) ? "-" : r.GetValue(k).ToString())))))));
        string before = Hash();
        Assert.Equal(2, s.RollupDecisions(10));   // 0〜9・10〜19（20〜29 は終わっていない）
        Assert.Equal(0, s.RollupDecisions(10));
        Assert.Equal(before, Hash());
        Assert.Equal(25, Convert.ToInt32(s.Db.Scalar("SELECT COUNT(*) FROM decision_records")));
        Assert.Equal(20, Convert.ToInt32(s.Db.Scalar("SELECT SUM(n) FROM decision_rollups")));
        Assert.Equal(4, Convert.ToInt32(s.Db.Scalar("SELECT COUNT(*) FROM decision_rollups")));
        Assert.DoesNotMatch(@"(?i)\b(DELETE|UPDATE)\b", MethodBodyOf(Src("GameKouryakuKun.Storage/ExperienceStore.cs"), "public int RollupDecisions("));
    }

    [Fact]
    public void Db_inspector_is_read_only_and_never_migrates()
    {
        using var d = new TempDir();
        string path;
        using (var s = ExperienceStore.Open(d["p"], "prof", "1", "0.12.0"))
        {
            long run = s.BeginRun("1", "b", null, policyVersion: "explore-0.3");
            s.AppendStream(run, null, new StreamStep(1, "s", "a", true, "t", 0.1), 1, Array.Empty<BoundaryReason>());
            path = Path.Combine(d["p"], "memory.sqlite");
        }
        var h0 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        using (var ins = new DbInspector(path))
        {
            foreach (var c in DbInspector.Commands) Assert.False(string.IsNullOrWhiteSpace(ins.Run(c, c == "provenance" ? new[] { "s" } : Array.Empty<string>())));
            Assert.Contains("mode_transitions", ins.Run("summary", Array.Empty<string>()));
        }
        Assert.Equal(h0, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
        var src = Src("GameKouryakuKun.Storage/DbInspector.cs");
        Assert.Contains("SqliteOpenMode.ReadOnly", src);
        Assert.DoesNotMatch(@"(?i)\b(INSERT|UPDATE|DELETE|DROP|ALTER|CREATE)\s", src.Replace("type='table'", ""));
        Assert.DoesNotContain("ExperienceStore", src);
        Assert.DoesNotContain("Migrat", src);
    }

    [Fact]
    public void Ui_settings_round_trip_and_tolerate_a_broken_file()
    {
        using var d = new TempDir();
        var u = UiSettings.Load(d["ui"]);
        Assert.False(u.IsCollapsed("候補と評価"));
        u.SetCollapsed("候補と評価", true); u.SetCollapsed("結果", false);
        var u2 = UiSettings.Load(d["ui"]);
        Assert.True(u2.IsCollapsed("候補と評価"));
        Assert.False(u2.IsCollapsed("結果"));
        Assert.False(File.Exists(Path.Combine(d["ui"], UiSettings.FileName + ".tmp")));
        File.WriteAllText(Path.Combine(d["ui"], UiSettings.FileName), "{ broken");
        var u3 = UiSettings.Load(d["ui"]);
        Assert.NotNull(u3.LoadProblem);
        Assert.False(u3.IsCollapsed("候補と評価"));
        Assert.Single(Directory.GetFiles(d["ui"], UiSettings.FileName + ".broken-*"));
    }

    [Fact]
    public void Ui_settings_are_display_only_and_isolated_from_learning_and_records()
    {
        // 表示の設定は学習・記録・行動の選択から参照されない。表示の設定は学習 DB に触れない
        foreach (var f in new[] { "GameKouryakuKun.App/Session.cs", "GameKouryakuKun.Core/Exploration/Explorer.cs", "GameKouryakuKun.Core/Exploration/TransitionModel.cs",
                     "GameKouryakuKun.Core/Exploration/ActionSequences.cs", "GameKouryakuKun.Core/Exploration/VisualStates.cs", "GameKouryakuKun.Storage/ExperienceStore.cs",
                     "GameKouryakuKun.Storage/HealthReport.cs", "GameKouryakuKun.Storage/Schemas.cs" })
        {
            var src = Src(f);
            Assert.DoesNotContain("UiSettings", src);
            Assert.DoesNotContain("ui-settings", src);
        }
        var ui = Src("GameKouryakuKun.Core/Ui/UiSettings.cs");
        foreach (var w in new[] { "ExperienceStore", "SqliteDb", "Sqlite", "Explorer", "TransitionModel", "Session" }) Assert.DoesNotContain(w, ui);
        // Viewer では、畳む・開くは表示（Visibility）と設定の保存だけ
        var mw = Src("GameKouryakuKun.App/MainWindow.xaml.cs");
        var body = MethodBodyOf(mw, "private void AddPanelCore(");   // v0.30.2: 欄の本体は AddPanelCore
        Assert.Contains("OnPanelToggled(title, c)", body);
        Assert.DoesNotMatch(@"_session|_env\.Memory|Explorer|Log\.Write", body);
        var toggled = MethodBodyOf(mw, "private void OnPanelToggled(");
        Assert.Contains("_ui!.SetCollapsed(title, collapsed)", toggled);
        Assert.DoesNotMatch(@"_session|_env\.Memory|Explorer|Session|Memory\.", toggled);
    }

    [Fact]
    public void Revert_and_reinterpretation_never_modify_direct_records()
    {
        var src = Src("GameKouryakuKun.Storage/ExperienceStore.cs");
        foreach (var sig in new[] { "public IReadOnlyList<Reclassified> ReclassifyForSplit(", "public IReadOnlyList<Reclassified> ReclassifyForRevert(", "private IReadOnlyList<Reclassified> Reinterpret(" })
        {
            // 式の本体（=>）のメソッドは次のメソッドの手前まで
            int k = src.IndexOf(sig, StringComparison.Ordinal); Assert.True(k >= 0, sig);
            int arrow = src.IndexOf("=>", k), brace = src.IndexOf('{', k);
            var b = arrow >= 0 && arrow < brace && !src[k..arrow].Contains('\n') ? src[k..src.IndexOf(";\n", arrow)] : MethodBodyOf(src, sig);
            Assert.DoesNotMatch(@"(?i)(UPDATE|DELETE\s+FROM|INSERT\s+(OR\s+\w+\s+)?INTO)\s+(experience_stream|mode_transitions)\b", b);
        }
    }

    [Fact]
    public void Health_report_judges_step_overhead_and_view_settings_from_logs_by_itself()
    {
        using var d = new TempDir();
        using var mem = ExperienceStore.Open(d["p"], "prof", "1", "0.12.0");
        using var app = AppStore.Open(d["a"], "0.12.0");
        Directory.CreateDirectory(d["logs"]);
        string Log(string name, double gapMs, bool withUi)
        {
            var t0 = new DateTimeOffset(2026, 10, 18, 14, 0, 0, TimeSpan.FromHours(9));
            var lines = Enumerable.Range(0, 30).Select(i => $"{{\"t\":\"{t0.AddMilliseconds(i * (500 + gapMs)):yyyy-MM-ddTHH:mm:ss.fffzzz}\",\"type\":\"turn\",\"boundary_ms\":500}}").ToList();
            if (withUi) { lines.Add("{\"type\":\"view_settings_loaded\",\"panels\":12,\"collapsed\":3,\"problem\":\"-\"}"); lines.Add("{\"type\":\"view_settings_saved\",\"panel\":\"x\",\"collapsed\":true}"); }
            var path = Path.Combine(d["logs"], name); File.WriteAllLines(path, lines); return path;
        }
        var old = Log("app-1.jsonl", 160, false);
        var ok = HealthReport.Build("0.12.0", mem.Db, app.Db, null, new[] { Log("app-2.jsonl", 170, true), old }, 2, null);
        var oh = ok.Checks.Single(c => c.Name.StartsWith("1手あたりの処理時間"));
        Assert.Equal(HealthReport.Level.Ok, oh.Level);
        Assert.Contains("170 ms", oh.Detail);
        var ui = ok.Checks.Single(c => c.Area == "表示");
        Assert.Equal(HealthReport.Level.Ok, ui.Level);
        Assert.Contains("3／12", ui.Detail);
        // 前回の起動で 2 つ畳んで保存 → 今回 0 個しか戻っていなければ注意
        string Ui(string name, int loaded, params (string P, bool C)[] saves)
        {
            var lines = new List<string> { $"{{\"type\":\"view_settings_loaded\",\"panels\":12,\"collapsed\":{loaded},\"problem\":\"-\"}}" };
            lines.AddRange(saves.Select(x => $"{{\"type\":\"view_settings_saved\",\"panel\":\"{x.P}\",\"collapsed\":{(x.C ? "true" : "false")}}}"));
            var path = Path.Combine(d["logs"], name); File.WriteAllLines(path, lines); return path;
        }
        var prev = Ui("app-4.jsonl", 0, ("a", true), ("b", true), ("c", true), ("c", false));
        Assert.Equal(HealthReport.Level.Ok, HealthReport.Build("0.12.0", mem.Db, app.Db, null, new[] { Ui("app-5.jsonl", 2), prev }, 2, null).Checks.Single(c => c.Area == "表示").Level);
        Assert.Equal(HealthReport.Level.Warn, HealthReport.Build("0.12.0", mem.Db, app.Db, null, new[] { Ui("app-6.jsonl", 0), prev }, 2, null).Checks.Single(c => c.Area == "表示").Level);
        var slow = HealthReport.Build("0.12.0", mem.Db, app.Db, null, new[] { Log("app-3.jsonl", 400, false), old }, 2, null);
        Assert.Equal(HealthReport.Level.Warn, slow.Checks.Single(c => c.Name.StartsWith("1手あたりの処理時間")).Level);
        // v0.19.7: 人間の手（押す間隔を含む）は処理時間から除く。source の無い古いログは control_switch から判断する
        string HLog(string name, bool withSource)
        {
            var t0 = new DateTimeOffset(2026, 10, 18, 15, 0, 0, TimeSpan.FromHours(9));
            var lines = new List<string> { "{\"type\":\"control_switch\",\"step\":1,\"human\":true}" };
            lines.AddRange(Enumerable.Range(1, 30).Select(i => $"{{\"t\":\"{t0.AddMilliseconds(i * 3000):yyyy-MM-ddTHH:mm:ss.fffzzz}\",\"type\":\"turn\",\"step\":{i},\"boundary_ms\":500{(withSource ? ",\"source\":\"human\"" : "")}}}"));
            var path = Path.Combine(d["logs"], name); File.WriteAllLines(path, lines); return path;
        }
        foreach (var ws in new[] { true, false })
            Assert.DoesNotContain(HealthReport.Build("0.19.7", mem.Db, app.Db, null, new[] { HLog($"app-h{ws}.jsonl", ws), old }, 2, null).Checks, c => c.Name.StartsWith("1手あたりの処理時間"));
        // v0.19.2: 開始の設定・グラフ・ウィンドウの復元
        HealthReport.Level R(string name, string body) { var path = Path.Combine(d["logs"], name); File.WriteAllLines(path, new[] { "{\"type\":\"view_settings_restored\"," + body + "}" }); return HealthReport.Build("0.19.2", mem.Db, app.Db, null, new[] { path }, 2, null).Checks.Single(c => c.Name.StartsWith("開始の設定・グラフ・ウィンドウの復元")).Level; }
        Assert.Equal(HealthReport.Level.Ok, R("app-7.jsonl", "\"run_seconds\":\"600\",\"max_steps\":\"0\",\"graph\":\"all\",\"window\":\"restored\",\"saved_run_seconds\":\"600\",\"saved_max_steps\":\"0\",\"saved_graph\":\"all\""));
        Assert.Equal(HealthReport.Level.Warn, R("app-8.jsonl", "\"run_seconds\":\"0\",\"max_steps\":\"0\",\"graph\":\"all\",\"window\":\"restored\",\"saved_run_seconds\":\"600\",\"saved_max_steps\":\"-\",\"saved_graph\":\"-\""));
        Assert.Equal(HealthReport.Level.NotTested, R("app-9.jsonl", "\"run_seconds\":\"0\",\"max_steps\":\"0\",\"graph\":\"x\",\"window\":\"none\",\"saved_run_seconds\":\"-\",\"saved_max_steps\":\"-\",\"saved_graph\":\"-\""));
    }

    internal static string MethodBodyOf(string src, string signature)
    {
        int k = src.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(k >= 0, "見つからない: " + signature);
        int open = src.IndexOf('{', k), depth = 0;
        for (int i = open; i < src.Length; i++)
        {
            if (src[i] == '{') depth++;
            else if (src[i] == '}' && --depth == 0) return src[open..(i + 1)];
        }
        throw new InvalidOperationException(signature);
    }

}
public class UiSettingsV0191Tests
{
    [Fact]
    public void Values_and_window_round_trip_and_keep_collapsed()
    {
        var d = Path.Combine(Path.GetTempPath(), "gkk-ui-" + Guid.NewGuid().ToString("N"));
        var u = UiSettings.Load(d);
        u.SetCollapsed("目的", true); u.SetValue("run_seconds", "600"); u.SetValue("graph", "all");
        u.SetWindow(new WindowPlacement { Left = 10, Top = 20, Width = 800, Height = 600, Maximized = true });
        var u2 = UiSettings.Load(d);
        Assert.Null(u2.LoadProblem); Assert.True(u2.IsCollapsed("目的"));
        Assert.Equal("600", u2.GetValue("run_seconds")); Assert.Equal("all", u2.GetValue("graph")); Assert.Null(u2.GetValue("none"));
        Assert.Equal(800, u2.Window!.Width); Assert.True(u2.Window.Maximized);
    }

    [Fact]
    public void Old_v1_file_without_new_fields_loads()
    {
        var d = Path.Combine(Path.GetTempPath(), "gkk-ui-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(d, UiSettings.FileName), "{\"Version\":1,\"Collapsed\":{\"a\":true}}");
        var u = UiSettings.Load(d);
        Assert.Null(u.LoadProblem); Assert.True(u.IsCollapsed("a")); Assert.Null(u.Window); Assert.Null(u.GetValue("graph"));
    }

    static readonly ScreenArea[] Two = { new(0, 0, 1920, 1040), new(1920, 0, 1280, 984) };

    [Fact]
    public void Fit_keeps_visible_window_and_clamps_partly_offscreen()
    {
        var r = WindowRestore.Fit(new WindowPlacement { Left = 2000, Top = 100, Width = 900, Height = 700 }, Two, 400, 300)!;
        Assert.Equal(2000, r.Left); Assert.Equal(100, r.Top);
        r = WindowRestore.Fit(new WindowPlacement { Left = 2800, Top = 500, Width = 900, Height = 700 }, Two, 400, 300)!;   // 2 台目からはみ出す
        Assert.Equal(3200 - 900, r.Left); Assert.Equal(984 - 700, r.Top);
    }

    [Fact]
    public void Fit_moves_offscreen_window_to_primary_and_shrinks_oversize()
    {
        var r = WindowRestore.Fit(new WindowPlacement { Left = 5000, Top = 3000, Width = 800, Height = 600 }, Two, 400, 300)!;   // 外したモニター
        Assert.Equal((1920 - 800) / 2.0, r.Left); Assert.Equal((1040 - 600) / 2.0, r.Top);
        r = WindowRestore.Fit(new WindowPlacement { Left = 0, Top = 0, Width = 5000, Height = 3000, Maximized = true }, Two, 400, 300)!;
        Assert.True(r.Width <= 1920 && r.Height <= 1040); Assert.True(r.Maximized);
        Assert.Null(WindowRestore.Fit(new WindowPlacement { Width = double.NaN, Height = 5 }, Two, 400, 300));
        Assert.Null(WindowRestore.Fit(null, Two, 400, 300));
    }
}
