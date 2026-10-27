using System.Text.RegularExpressions;
using GameKouryakuKun.Core.Profile;
using Xunit;

namespace GameKouryakuKun.Core.Tests;

/// <summary>
/// 制約遵守の静的検査（design.md 最上位原則「攻略知識の外部注入禁止」・phase2-design.md §13）。ForbiddenApiTests（禁止API）に加えて、
///  - 本体のコード・UI・Profile の文字列に、対象ゲーム固有の語が無い（UI も対象。最上位原則 1）
///  - 識別子・文字列に、ゲームの意味や構造を前提にした語（死亡・敵・メニュー・決定・自分のマス など）が無い
///  - Core の数値の既定値には「根拠:」の注記がある（実機で見たゲームの値を混ぜない）
///  - 診断（既知操作を使う）の記録・処理は学習 DB・探索から完全に分離されている（U3）
///  - 人向けの名前（pattern_labels）は説明層だけが触る（U1）
///  - 評価用の正解データを本体が読まない（U5）
///  - 今の版の自己監査記録（src/audit/v<版>.md）がある
/// を確かめる。
/// </summary>
public class ConstraintAuditTests
{
    private static string SrcRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "GameKouryakuKun.sln"))) d = d.Parent;
        return d?.FullName ?? throw new DirectoryNotFoundException("GameKouryakuKun.sln が見つかりません");
    }

    private static bool IsBuildOutput(string p) =>
        p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") || p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}");

    private static readonly string[] ProductProjects = { "GameKouryakuKun.Core", "GameKouryakuKun.Storage", "GameKouryakuKun.Ocr", "GameKouryakuKun.App", "GameKouryakuKun.Windows", "GameKouryakuKun.Setup" };

    /// <summary>本体のソース（認識・記録・判断・UI・診断ハーネス）。テストは対象外</summary>
    private static IEnumerable<string> ProductSources(string pattern = "*.cs")
    {
        var root = SrcRoot();
        foreach (var proj in ProductProjects)
        {
            var dir = Path.Combine(root, proj);
            if (!Directory.Exists(dir)) continue;
            foreach (var f in Directory.EnumerateFiles(dir, pattern, SearchOption.AllDirectories))
                if (!IsBuildOutput(f)) yield return f;
        }
    }

    private static IEnumerable<string> AiSideSources() => ProductSources("*.cs").Concat(ProductSources("*.xaml"));

    /// <summary>
    /// Schemas.cs のうち、過去の版で作られた記録用の移行（Memory の v1〜v3）は、既存の DB を移行するために残すもので検査から除く。
    /// v4 以降の DROP・RENAME の行（意味を前提にした表・列を消す側）も除く
    /// </summary>
    private static string Scannable(string path)
    {
        var src = File.ReadAllText(path);
        if (Path.GetFileName(path) != "Schemas.cs") return src;
        int a = src.IndexOf("IReadOnlyList<Migration> Memory", StringComparison.Ordinal);
        int b = src.IndexOf("new Migration(4,", a, StringComparison.Ordinal);
        if (a < 0 || b < 0) throw new InvalidOperationException("Schemas.cs の構造が変わりました（検査の除外範囲を見直してください）");
        var kept = src[..a] + new string('\n', src[a..b].Count(c => c == '\n')) + src[b..];
        return string.Join("\n", kept.Split('\n').Select(l => Regex.IsMatch(l, @"^\s*(DROP |ALTER TABLE \w+ (DROP|RENAME) )") ? "" : l));
    }

    /// <summary>対象ゲームの画面に出る語（設計者が実機の画面で見たもの）。AI 側のコードの文字列に現れたら不合格</summary>
    private static readonly string[] GameVocabulary =
    {
        @"\bHP\b", "満腹", @"\bLv\b", "レベル", "トルネコ", "階段", "ダンジョン", "ゴースト", "スライム", "ちから", "経験値", "最深階",
        "倒され", "剣の強さ", "盾の強さ", "道具", "足元", "ワナ", "罠", "巻物", "杖", "壺", "おにぎり", "Torneko",
        // ゲームの意味・構造を前提にした語（最上位原則・U1・U4）。人向けの説明でも、意味は経験から付ける（説明層の pattern_labels）
        "死亡", "死んだ", "倒れ", "脱出", "敵", "モンスター", "アイテム", "攻撃", "自分のマス", "自キャラ", "主人公", "メニュー", "決定キー", "取消キー", "フロア", "階層", "ゲームオーバー", "足踏み",
    };

    /// <summary>コメントを除いた文字列リテラル（"..." と生文字列 """...""" と @"..."）を取り出す</summary>
    private static IEnumerable<(int Line, string Text)> StringLiterals(string path)
    {
        var src = Scannable(path);
        if (path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
        {
            // XAML は属性値と要素の中身（コメントを除く）
            var x = Regex.Replace(src, "<!--.*?-->", m => new string('\n', m.Value.Count(c => c == '\n')), RegexOptions.Singleline);
            foreach (Match m in Regex.Matches(x, "\"([^\"]*)\"|>([^<]+)<")) yield return (LineOf(x, m.Index), m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);
            yield break;
        }
        // 生文字列（SQL など）
        foreach (Match m in Regex.Matches(src, "\"\"\"(.*?)\"\"\"", RegexOptions.Singleline))
            foreach (var l in StripSqlComments(m.Groups[1].Value)) yield return (LineOf(src, m.Index), l);
        var noRaw = Regex.Replace(src, "\"\"\"(.*?)\"\"\"", m => new string('\n', m.Value.Count(c => c == '\n')), RegexOptions.Singleline);
        var lines = noRaw.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var code = StripLineComment(lines[i]);
            foreach (Match m in Regex.Matches(code, "@?\\$?\"((?:[^\"\\\\]|\\\\.)*)\"")) yield return (i + 1, m.Groups[1].Value);
        }
    }

    private static IEnumerable<string> StripSqlComments(string sql) => sql.Split('\n').Select(l => { int k = l.IndexOf("--", StringComparison.Ordinal); return k >= 0 ? l[..k] : l; });
    private static int LineOf(string s, int idx) => s.Take(idx).Count(c => c == '\n') + 1;

    private static string StripLineComment(string line)
    {
        bool inStr = false;
        for (int i = 0; i < line.Length - 1; i++)
        {
            if (line[i] == '"' && (i == 0 || line[i - 1] != '\\')) inStr = !inStr;
            if (!inStr && line[i] == '/' && line[i + 1] == '/') return line[..i];
        }
        return line;
    }

    [Fact]
    public void Product_code_and_ui_have_no_game_specific_vocabulary_in_strings()
    {
        var files = AiSideSources().ToList();
        Assert.True(files.Count >= 40, "検査対象が少なすぎます: " + files.Count);
        var hits = new List<string>();
        foreach (var f in files)
            foreach (var (line, text) in StringLiterals(f))
                foreach (var w in GameVocabulary)
                    if (Regex.IsMatch(text, w)) hits.Add($"{Path.GetFileName(f)}:{line}: 「{w}」 in \"{text}\"");
        Assert.True(hits.Count == 0, "AI 側のコードにゲーム固有の語があります（攻略知識の混入の恐れ）:\n" + string.Join("\n", hits));
    }

    [Fact]
    public void Vocabulary_check_itself_detects_a_planted_word()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "gkk-audit-" + Guid.NewGuid().ToString("N")[..8] + ".cs");
        File.WriteAllText(tmp, "// HP はコメントなので対象外\nclass X { string a = \"HP\"; string b = \"ok\"; }\n");
        try
        {
            var lits = StringLiterals(tmp).ToList();
            Assert.Contains(lits, l => l.Text == "HP");
            Assert.DoesNotContain(lits, l => l.Text.Contains("コメント"));
        }
        finally { File.Delete(tmp); }
    }

    /// <summary>識別子（コメント・文字列を除いたコード）に現れてはいけない、ゲームの意味・構造を前提にした語（camelCase・snake_case を分けた語の単位）</summary>
    private static readonly HashSet<string> SemanticIdentifierWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "death", "deaths", "dead", "died", "die", "enemy", "enemies", "monster", "monsters", "stairs", "stair", "hazard", "attack",
        "hunger", "satiety", "hp", "dungeon", "menu", "player", "hero", "weapon", "trap", "endkind",
        // 学習の区切り・操作対象を前提にした語（2026-10-18 09:36: エピソード境界・主要状態・操作対象は与えない）
        "excursion", "excursions", "episode", "episodes", "anchor", "adventure",
    };

    /// <summary>キーの意味を前提にした文字列（論理キー名として使われていた語）</summary>
    private static readonly Regex SemanticKeyLiteral = new("^(menu|cancel|confirm|arrow|map|diagonal_assist|up|down|left|right|attack|death|ended_by_death|escape)$");

    private static IEnumerable<(int Line, string Word)> IdentifierWords(string path)
    {
        var src = Scannable(path);
        src = Regex.Replace(src, "\"\"\"(.*?)\"\"\"", m => new string('\n', m.Value.Count(c => c == '\n')), RegexOptions.Singleline);
        src = Regex.Replace(src, @"/\*.*?\*/", m => new string('\n', m.Value.Count(c => c == '\n')), RegexOptions.Singleline);
        var lines = src.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var code = Regex.Replace(StripLineComment(lines[i]), "@?\\$?\"((?:[^\"\\\\]|\\\\.)*)\"", "\"\"");
            code = Regex.Replace(code, "'(?:[^'\\\\]|\\\\.)'", "''");
            foreach (Match m in Regex.Matches(code, "[A-Za-z_][A-Za-z0-9_]*"))
                foreach (var w in Regex.Split(Regex.Replace(m.Value, "([a-z0-9])([A-Z])", "$1_$2"), "_+"))
                    if (w.Length > 0) yield return (i + 1, w);
        }
    }

    [Fact]
    public void Identifiers_and_key_literals_carry_no_game_meaning()
    {
        var hits = new List<string>();
        foreach (var f in ProductSources("*.cs"))
        {
            // Win32 の構造体・API の引数名（lpszMenuName など）は OS の名前で、ゲームの意味ではない
            bool osInterop = Path.GetFileName(f) == "Native.cs";
            foreach (var (line, w) in IdentifierWords(f))
                if (!(osInterop && w.Equals("menu", StringComparison.OrdinalIgnoreCase)))
                if (SemanticIdentifierWords.Contains(w)) hits.Add($"{Path.GetFileName(f)}:{line}: 識別子の語「{w}」");
            foreach (var (line, text) in StringLiterals(f))
                if (SemanticKeyLiteral.IsMatch(text)) hits.Add($"{Path.GetFileName(f)}:{line}: 意味を前提にしたキー名らしき文字列 \"{text}\"");
        }
        Assert.True(hits.Count == 0, "ゲームの意味・構造を前提にした語があります:\n" + string.Join("\n", hits.Distinct()));
    }

    [Fact]
    public void Identifier_check_itself_detects_a_planted_word()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "gkk-audit-" + Guid.NewGuid().ToString("N")[..8] + ".cs");
        File.WriteAllText(tmp, "// death はコメントなので対象外\nclass X { int EnemyCount; string s = \"death\"; System.Threading.CancellationToken ct; }\n");
        try
        {
            var words = IdentifierWords(tmp).Select(x => x.Word).ToList();
            Assert.Contains("Enemy", words);
            Assert.DoesNotContain("death", words);
            Assert.Contains(StringLiterals(tmp), l => SemanticKeyLiteral.IsMatch(l.Text));
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void Profile_and_ui_text_are_scanned_too()
    {
        // Profile は display_name・process_name・class_name（接続条件）以外に、ゲーム固有の語を書かない
        var root = SrcRoot();
        foreach (var p in Directory.EnumerateFiles(Path.Combine(root, "profiles"), "profile.json", SearchOption.AllDirectories))
        {
            var lines = File.ReadAllLines(p).Where(l => !Regex.IsMatch(l, "\"(display_name|process_name|class_name|title_regex)\"")).ToList();
            foreach (var l in lines)
                foreach (var w in GameVocabulary)
                    Assert.False(Regex.IsMatch(l, w), $"{p}: 「{w}」 in {l}");
        }
        Assert.Contains(AiSideSources(), f => f.EndsWith("MainWindow.xaml", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("\"lifecycle\": { \"start_run\": [] }")]
    [InlineData("\"layout\": { \"regions\": {} }")]
    [InlineData("\"regions\": {}")]
    [InlineData("\"grid\": { \"cell_px\": [48, 48] }")]
    [InlineData("\"idle_safe\": true")]
    [InlineData("\"end_detection\": { \"type\": \"image-hint\" }")]
    [InlineData("\"entities\": { \"a\": \"b\" }")]
    [InlineData("\"roles\": { \"button1\": \"x\" }")]
    public void Profile_rejects_out_of_adventure_procedures_layout_and_knowledge(string extra)
    {
        var json = "{ \"schema_version\": 2, \"profile_id\": \"x\", \"profile_version\": \"1\", \"display_name\": \"x\"," +
                   " \"window\": { \"process_name\": \"p\" }, \"input\": { \"keys\": { \"button1\": { \"key\": \"Num0\" }, \"button2\": { \"key\": \"Num2\" } } }, " + extra + " }";
        Assert.Throws<ProfileValidationException>(() => GameProfileLoader.Parse(json));
    }

    [Fact]
    public void Shipped_profile_contains_only_body_and_connection()
    {
        var root = SrcRoot();
        foreach (var p in Directory.EnumerateFiles(Path.Combine(root, "profiles"), "profile.json", SearchOption.AllDirectories))
        {
            var prof = GameProfileLoader.Load(p);
            Assert.Equal(2, prof.SchemaVersion);
            var text = File.ReadAllText(p);
            foreach (var key in new[] { "\"lifecycle\"", "\"regions\"", "\"grid\"", "\"idle_safe\"", "\"end_detection\"", "\"layout\"", "\"label\"" })
                Assert.DoesNotContain(key, text);
        }
    }

    [Fact]
    public void Core_numeric_defaults_state_their_basis()
    {
        // Core の調整値（init・const・static readonly の数値の既定）には「根拠:」を書く。区分: 汎用／合成テスト／実機性能評価(U5)／自己調整／診断の合格条件
        var root = SrcRoot();
        var re = new Regex(@"(\{\s*get;\s*(init|set);\s*\}|\bconst\s+(int|double|float|long)\s+\w+|static\s+readonly\s+(int|double|float|long)\s+\w+)\s*=\s*-?[0-9][0-9_.]*[dDfFLmM]?\s*;");
        var hits = new List<string>();
        foreach (var f in Directory.EnumerateFiles(Path.Combine(root, "GameKouryakuKun.Core"), "*.cs", SearchOption.AllDirectories).Where(f => !IsBuildOutput(f)))
        {
            var lines = File.ReadAllLines(f);
            for (int i = 0; i < lines.Length; i++)
            {
                if (!re.IsMatch(lines[i])) continue;
                bool ok = lines[i].Contains("根拠:") || (i > 0 && lines[i - 1].Contains("根拠:")) || (i > 1 && lines[i - 2].Contains("根拠:"));
                if (!ok) hits.Add($"{Path.GetFileName(f)}:{i + 1}: {lines[i].Trim()}");
            }
        }
        Assert.True(hits.Count == 0, "数値の既定値に「根拠:」がありません（ゲームで見た値の混入を防ぐため、出典を書く）:\n" + string.Join("\n", hits));
    }

    private static string MethodBody(string src, string signature)
    {
        int k = src.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(k >= 0, "見つからない: " + signature);
        int open = src.IndexOf('{', k), depth = 0;
        for (int i = open; i < src.Length; i++)
        {
            if (src[i] == '{') depth++;
            else if (src[i] == '}' && --depth == 0) return src[open..(i + 1)];
        }
        throw new InvalidOperationException("本体の終わりが見つからない: " + signature);
    }

    [Fact]
    public void Diagnostics_are_fully_separated_from_learning()
    {
        var root = SrcRoot();
        var session = File.ReadAllText(Path.Combine(root, "GameKouryakuKun.App", "Session.cs"));
        // v0.15.0: 既知操作の往復を使う診断（分離試験・互換性テスト）は廃止した。残っていないこと
        Assert.DoesNotContain("RunSeparationDiagnostic", session);
        Assert.DoesNotContain("CompatTestRunner", session);
        // 探索の処理は診断用の既知操作を読まない
        var explore = MethodBody(session, "private string RunExplore(");
        Assert.DoesNotContain("Diagnostics", explore);
        Assert.DoesNotContain("Toggle", explore);
        // Core の探索・分類は診断を参照しない。診断の保存は学習 DB を参照しない
        foreach (var f in Directory.EnumerateFiles(Path.Combine(root, "GameKouryakuKun.Core", "Exploration"), "*.cs"))
            Assert.DoesNotContain("Diagnostics", File.ReadAllText(f));
        var diag = File.ReadAllText(Path.Combine(root, "GameKouryakuKun.Storage", "DiagnosticStore.cs"));
        Assert.DoesNotContain("ExperienceStore", diag);
        Assert.DoesNotContain("memory.sqlite", diag);
        // 学習 DB は診断の方針名を受け付けない（実行時の保証は SchemaV4AndDiagnosticsTests）
        Assert.Contains("IsDiagnosticPolicy(policyVersion)", File.ReadAllText(Path.Combine(root, "GameKouryakuKun.Storage", "ExperienceStore.cs")));
    }

    [Fact]
    public void Pattern_labels_are_touched_only_by_the_explanation_layer()
    {
        var allowed = new[] { "Schemas.cs", "ExperienceStore.cs", "MainWindow.xaml.cs", "DiagnosticExport.cs" };
        var hits = ProductSources("*.cs").Where(f => !allowed.Contains(Path.GetFileName(f)))
            .Where(f => Regex.IsMatch(File.ReadAllText(f), "pattern_labels|PatternSummaryForDisplay|SetPatternLabelForDisplay")).ToList();
        Assert.True(hits.Count == 0, "人向けの名前を説明層以外が参照しています: " + string.Join(", ", hits));
        var store = File.ReadAllText(Path.Combine(SrcRoot(), "GameKouryakuKun.Storage", "ExperienceStore.cs"));
        // 学習に読み込む関数は名前の表を読まない
        foreach (var sig in new[] { "LoadSequencePatterns()", "LoadStream(int limit)", "LoadReversibility()", "LoadTransitions()" })
        {
            int k = store.IndexOf(sig, StringComparison.Ordinal);
            Assert.True(k >= 0, sig);
            var line = store[k..store.IndexOf('\n', k)];
            var next = store[k..Math.Min(store.Length, k + 400)];
            Assert.DoesNotContain("pattern_labels", next.Split("public ", 3).Take(2).Aggregate("", (a, b) => a + b));
        }
    }

    [Fact]
    public void Product_never_reads_evaluation_ground_truth()
    {
        var root = SrcRoot();
        var re = new Regex(@"ground[_-]?truth|eval[-_]labels|annotations?\.json|eval-tmp", RegexOptions.IgnoreCase);
        var hits = ProductSources("*.cs").Where(f => re.IsMatch(File.ReadAllText(f))).ToList();
        Assert.True(hits.Count == 0, "本体が評価用の正解データを参照しています: " + string.Join(", ", hits));
    }

    [Fact]
    public void No_save_state_rewind_or_rng_access()
    {
        var re = new Regex(@"SaveState|LoadState|savestate|Rewind\(|RngSeedFromGame|ReadRandomState", RegexOptions.IgnoreCase);
        var hits = ProductSources("*.cs").Where(f => re.IsMatch(File.ReadAllText(f))).ToList();
        Assert.True(hits.Count == 0, "セーブステート・巻き戻し・乱数取得らしき記述: " + string.Join(", ", hits));
    }

    [Fact]
    public void Explore_mode_keeps_going_and_stops_only_when_input_delivery_fails()
    {
        // 方針（2026-10-18・v0.15.0）: 画面の状況では止めない。止まるのは時間・手数（指定時）・停止・入力の送信そのもの（A）の失敗だけ
        var session = File.ReadAllText(Path.Combine(SrcRoot(), "GameKouryakuKun.App", "Session.cs"));
        var body = MethodBody(session, "private string RunExplore(");
        Assert.Contains("Delivery.Record(receipt)", body);
        Assert.DoesNotContain("Unreachable", body);
        Assert.DoesNotContain("NoChangeRunGuard", body);
        Assert.DoesNotContain("IsBlackout", body);
        // 送る操作は探索の選んだもの（Profile の中立の論理キー）だけ。キー名を直書きしない
        Assert.DoesNotMatch("Tap\\(\"(dpad_|button)", body);
    }

    [Fact]
    public void Self_audit_record_exists_for_the_current_version()
    {
        var root = SrcRoot();
        var csproj = File.ReadAllText(Path.Combine(root, "GameKouryakuKun.App", "GameKouryakuKun.App.csproj"));
        var ver = Regex.Match(csproj, "<Version>([^<]+)</Version>").Groups[1].Value;
        Assert.False(string.IsNullOrEmpty(ver));
        var path = Path.Combine(root, "audit", $"v{ver}.md");
        Assert.True(File.Exists(path), $"自己監査記録 {path} がありません（最上位原則 9）");
        var text = File.ReadAllText(path);
        for (int i = 1; i <= 9; i++) Assert.Contains($"| {i} |", text);
        Assert.Contains("実機データ", text);
    }

    [Fact]
    public void Sequence_learning_gets_no_episode_or_run_boundary()
    {
        // 2026-10-18 09:36: 「1回の冒険」「終了から再開」などの区切りを与えない。区切りも反復構造も連続した経験から見つける
        var root = SrcRoot();
        var learner = File.ReadAllText(Path.Combine(root, "GameKouryakuKun.Core", "Exploration", "SequenceLearning.cs"));
        var code = string.Join("\n", learner.Split('\n').Select(StripLineComment).Where(l => !l.TrimStart().StartsWith("///", StringComparison.Ordinal)));
        Assert.DoesNotMatch("(?i)\\brun|runid|episode|excursion|adventure|session", code);
        Assert.False(File.Exists(Path.Combine(root, "GameKouryakuKun.Core", "Exploration", "TransitionPatterns.cs")), "旧来の区切り依存の分類が残っています");
        // 探索は連続履歴を実行単位で絞らずに読み、学習器には実行単位を渡さない
        var session = MethodBody(File.ReadAllText(Path.Combine(root, "GameKouryakuKun.App", "Session.cs")), "private string RunExplore(");
        Assert.Contains("seqLearner.Replay(_env.Memory.LoadStream(", session);
        Assert.DoesNotMatch("seqLearner\\.[A-Za-z]+\\([^)]*RunId", session);
        var store = File.ReadAllText(Path.Combine(root, "GameKouryakuKun.Storage", "ExperienceStore.cs"));
        var load = MethodBody(store.Replace("LoadStream(int limit) =>", "LoadStream(int limit) {"), "LoadStream(int limit) {");
        Assert.DoesNotContain("run_id", load);
        // runs は実行単位としてだけ残る（学習の集計用の見方 learning_runs は作らない）
        Assert.DoesNotContain("CREATE VIEW learning_runs", Scannable(Path.Combine(root, "GameKouryakuKun.Storage", "Schemas.cs"))[Scannable(Path.Combine(root, "GameKouryakuKun.Storage", "Schemas.cs")).IndexOf("new Migration(5,", StringComparison.Ordinal)..]);
    }

    [Fact]
    public void New_generic_mechanisms_get_no_boundary_meaning_or_prior_value()
    {
        // v0.9.0（2026-10-18 10:49）: 操作の並び・見た目の状態・反応の対応表にも、実行単位などの区切りを渡さない。
        // 並びの名前は中立（ActionSequence #n）で、事前の価値（未試行の単独の操作より大きい点数）を付けない。反応の対応表は情報の量としてだけ選択に使う（v0.10.0）
        var root = SrcRoot();
        foreach (var rel in new[] { "Exploration/ActionSequences.cs", "Exploration/VisualStates.cs", "Perception/ReactionMap.cs" })
        {
            var src = File.ReadAllText(Path.Combine(root, "GameKouryakuKun.Core", rel));
            var code = string.Join("\n", src.Split('\n').Select(StripLineComment).Where(l => !l.TrimStart().StartsWith("///", StringComparison.Ordinal)));
            Assert.DoesNotMatch("(?i)\\brun|runid|episode|excursion|adventure|session", code);
        }
        var seqSrc = File.ReadAllText(Path.Combine(root, "GameKouryakuKun.Core", "Exploration", "ActionSequences.cs"));
        Assert.Contains("$\"ActionSequence #{Id}\"", seqSrc);
        var explorer = File.ReadAllText(Path.Combine(root, "GameKouryakuKun.Core", "Exploration", "Explorer.cs"));
        Assert.DoesNotContain("ActionGroup.Sequence ?", MethodBody(explorer, "public ExploreChoice Choose("));   // 並びだけに点数を足す分岐を作らない
        var session = MethodBody(File.ReadAllText(Path.Combine(root, "GameKouryakuKun.App", "Session.cs")), "private string RunExplore(");
        // 反応の対応表は「まだ分かっていない関係の情報の量」としてだけ選択に使う（v0.10.0）。反応する区画の数・変化の大きさは選択に渡さない
        Assert.DoesNotMatch("reactions\\.(Reactive|Summary)", session);
        Assert.Contains("reactions.InformationGain(", session);
        // 未試行かどうかは直接の経験だけで決める（借りた推定・読み直しで「試した」ことにしない）
        Assert.Contains("double novelty = st.Tries == 0 ? 2.0", MethodBody(explorer, "public ExploreChoice Choose("));
        // 出どころの種類は DB の制約で決まった値だけ（直接・読み直し・推定を区別して後から追える）
        var schemas = File.ReadAllText(Path.Combine(root, "GameKouryakuKun.Storage", "Schemas.cs"));
        Assert.Contains("CHECK(origin IN ('borrowed','parent'))", schemas);
        Assert.Contains("CHECK(origin IN ('reclassified'))", schemas);
        Assert.Contains("CREATE VIEW experience_provenance", schemas);
        // 読み直しは直接の記録を書き換えない（experience_stream・mode_transitions を UPDATE しない）
        var store = File.ReadAllText(Path.Combine(root, "GameKouryakuKun.Storage", "ExperienceStore.cs"));
        var re = MethodBody(store, "public IReadOnlyList<Reclassified> ReclassifyForSplit(");
        Assert.DoesNotMatch("UPDATE (experience_stream|mode_transitions)|DELETE FROM (experience_stream|mode_transitions)", re);
        var exp = File.ReadAllText(Path.Combine(root, "GameKouryakuKun.Core", "Exploration", "Explorer.cs"));
        Assert.DoesNotContain("ReactionMap", exp);
    }

    [Fact]
    public void Prediction_loop_is_generic_traceable_and_changes_selection_only_through_uncertainty()
    {
        // v0.11.0: 予測 → 比較 → 修正 → 選択。内部モデルにも実行単位などの区切りを渡さない。修正は作業用の重みだけで、直接の記録を消さない。
        // 判断の記録は点数の式の各項と予測の数値（隠れた推論ではない）。説明の文は説明層（DecisionText）だけが作り、選択はそれを読まない
        var root = SrcRoot();
        foreach (var rel in new[] { "Exploration/TransitionModel.cs", "Exploration/DecisionText.cs" })
        {
            var src = File.ReadAllText(Path.Combine(root, "GameKouryakuKun.Core", rel));
            var code = string.Join("\n", src.Split('\n').Select(StripLineComment).Where(l => !l.TrimStart().StartsWith("///", StringComparison.Ordinal)));
            Assert.DoesNotMatch("(?i)\\brun|runid|episode|excursion|adventure|session", code);
        }
        var model = File.ReadAllText(Path.Combine(root, "GameKouryakuKun.Core", "Exploration", "TransitionModel.cs"));
        Assert.DoesNotMatch("(?i)sqlite|ExperienceStore|DELETE", model);   // 内部モデルは DB を触らない（修正の記録は呼び出し側が追記するだけ）
        foreach (var rel in new[] { "Exploration/Explorer.cs", "Exploration/TransitionModel.cs" })
            Assert.DoesNotContain("DecisionText", File.ReadAllText(Path.Combine(root, "GameKouryakuKun.Core", rel)));
        var session = MethodBody(File.ReadAllText(Path.Combine(root, "GameKouryakuKun.App", "Session.cs")), "private string RunExplore(");
        Assert.Contains("model.Predict(curNode, ca.Name)", session);              // 手を送る前に予測する
        Assert.Contains("model.Observe(st.Seq, curNode, ca.Name, node2, changedNow, ambiguous: ambiguous)", session);   // 結果と比べる
        Assert.Contains("explorer.UncertaintyValue = (node, a) => model.Uncertainty(node, a.Name)", session);   // 修正が次の選択に効く唯一の経路
        Assert.Contains("_env.Memory.AddDecision(", session);
        Assert.DoesNotMatch("model\\.[A-Za-z]+\\([^)]*RunId", session);
        var store = File.ReadAllText(Path.Combine(root, "GameKouryakuKun.Storage", "ExperienceStore.cs"));
        foreach (var m in new[] { "public long AddModelRevision(", "public long AddDecision(" })
            Assert.DoesNotMatch("UPDATE|DELETE", MethodBody(store, m));   // 追記だけ
        var schemas = File.ReadAllText(Path.Combine(root, "GameKouryakuKun.Storage", "Schemas.cs"));
        Assert.Contains("CHECK(rule IN ('scored','momentum','chord_probe','fallback','sequence_step'))", schemas);
        Assert.Contains("CREATE TABLE experience_prior_events(", schemas);
    }

    [Fact]
    public void Diagnostic_zip_carries_no_screen_images()
    {
        // 2026-10-18 09:36: 実機確認は診断 zip だけで行い、画像は開かない。診断 zip には画像を入れない
        var root = SrcRoot();
        var app = File.ReadAllText(Path.Combine(root, "GameKouryakuKun.App", "AppEnv.cs"));
        var body = MethodBody(app, "public DiagnosticExport.Result ExportDiagnostics(");
        Assert.DoesNotMatch("files\\.Add\\([^;]*\\.png", body);
        Assert.Contains("HealthReport.Build(", body);
        Assert.True(GameKouryakuKun.Storage.DiagnosticExport.IsImage("profile/last_screen_1.png"));
    }
}
