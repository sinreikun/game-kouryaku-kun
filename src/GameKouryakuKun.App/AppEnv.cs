using System.Text;
using System.Reflection;
using GameKouryakuKun.Core.Diagnostics;
using GameKouryakuKun.Core.Profile;
using GameKouryakuKun.Setup;
using GameKouryakuKun.Storage;

namespace GameKouryakuKun.App;

/// <summary>アプリ全体で共有する環境（データフォルダ・DB・部品管理・ログ）</summary>
public sealed class AppEnv : IDisposable
{
    public const string ProfileId = "torneko-remaster";
    public static string Version => Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    public string DataRoot { get; }
    /// <summary>v0.32.5（t106u）: 自律探索で使ってよい入力（人が決める。画面の右側の欄で切り替え、ui-settings.json に保存）</summary>
    public GameKouryakuKun.Core.Exploration.ActionAvailability Availability { get; } = new();
    public string DataRootReason { get; }
    public string ExeDir { get; }
    public JsonlWriter Log { get; }
    public GameProfile Profile { get; }
    public AppStore AppDb { get; }
    public ExperienceStore Memory { get; }
    private DiagnosticStore? _diagnostics;
    /// <summary>システム診断の記録（diagnostics.sqlite）。学習 DB（Memory）とは別ファイル（U3）。v0.14.1 までの分離試験の記録。v0.15.0 以降は診断の書き出しで読むだけ</summary>
    public DiagnosticStore Diagnostics => _diagnostics ??= DiagnosticStore.Open(ProfileDataDir, Version);
    public ComponentManifest Manifest { get; }
    public ComponentManager Components { get; }
    public List<string> StartupNotes { get; } = new();

    public string ProfileDataDir => Path.Combine(DataRoot, "profiles", ProfileId);
    private RecordingStore? _recordings;
    /// <summary>v0.11 までの「画面を保存」で残った画像（data/profiles/&lt;id&gt;/recordings）。v0.12.0 で保存の機能は廃止。残りは保存期間（7日）で自動削除する</summary>
    public RecordingStore Recordings => _recordings ??= new RecordingStore(ProfileDataDir);

    private AppEnv(string root, string reason)
    {
        ExeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        DataRoot = root; DataRootReason = reason;
        Directory.CreateDirectory(Path.Combine(root, "logs"));
        Log = new JsonlWriter(Path.Combine(root, "logs", $"app-{DateTime.Now:yyyyMMdd-HHmmss}.jsonl"));
        Log.Write("start", ("version", Version), ("data_root", root), ("reason", reason), ("os", Environment.OSVersion.VersionString),
            ("framework", System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription),
            ("runtime_dir", System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory()),
            ("via_launcher", Environment.GetEnvironmentVariable("GKK_LAUNCHER") != null),
            ("dotnet_root", Environment.GetEnvironmentVariable("DOTNET_ROOT")));

        Profile = LoadProfile();
        AppDb = AppStore.Open(root, Version);
        if (AppDb.RebuiltFrom != null) StartupNotes.Add($"app.sqlite が壊れていたため作り直しました（旧ファイル: {Path.GetFileName(AppDb.RebuiltFrom)}）");
        Memory = ExperienceStore.Open(ProfileDataDir, Profile.ProfileId, Profile.ProfileVersion, Version);
        int dangling = Memory.CloseDanglingRuns();
        if (dangling > 0) StartupNotes.Add($"前回、正常に終了しなかった探索 {dangling} 件を「crash」として閉じました");
        foreach (var b in AppDb.Db.BackupsMade.Concat(Memory.Db.BackupsMade)) StartupNotes.Add($"DB を自動バックアップしました: {Path.GetFileName(b)}");
        // 過程データの自動削除（phase2-design.md §10.5、決定 D8）: 終わった冒険の手ごとの画像、保存期間（7日）・上限（2GB）を超えた、以前の版の「画面を保存」の画像
        try
        {
            int shots = Memory.PurgeEndedRunsProcessImages();
            int recs = Recordings.Cleanup();
            if (shots > 0 || recs > 0) StartupNotes.Add($"不要になった過程データを削除しました（手ごとの画像 {shots} 枚・以前の版の「画面を保存」の記録 {recs} 件）");
        }
        catch (Exception e) { StartupNotes.Add("過程データの削除に失敗（次回に再試行）: " + e.Message); }

        Manifest = ManifestLoader.Parse(ReadResource("components/manifest.json"));
        Components = ComponentManager.CreateDefault(root, Manifest, Version);
        Components.Installed += (spec, dir, ok) =>
        {
            var st = Components.Check(spec);
            AppDb.RecordComponentInstall(new ComponentInstallRecord(spec.Id, spec.Version, spec.InstallDir,
                Json.Arr(spec.Files.Select(f => (object?)new Dictionary<string, object?> { ["path"] = f.Path, ["sha256"] = f.Sha256, ["size"] = f.SizeBytes })),
                spec.License.Spdx, st.LicenseAcceptedAtUtc is DateTime d ? SqliteDb.Iso(d) : null, ok ? "installed" : "failed", dir));
            Log.Write("component_install", ("id", spec.Id), ("version", spec.Version), ("ok", ok), ("dir", dir));
        };
        RecordHardware();
    }

    /// <summary>
    /// データの置き場所: ホーム（ランチャーのフォルダ。AppHome 参照）に portable.flag があるか、書き込めるならホームの data\。
    /// そうでなければ %LOCALAPPDATA%\GameKouryakuKun。
    /// </summary>
    public static AppEnv Create()
    {
        string exeDir = Core.AppHome.Resolve(Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory,
            Environment.GetEnvironmentVariable(Core.AppHome.EnvVar), File.Exists, Directory.Exists);
        string local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameKouryakuKun");
        if (File.Exists(Path.Combine(exeDir, "portable.flag"))) return new AppEnv(Path.Combine(exeDir, "data"), "portable.flag があるため exe の隣");
        if (CanWrite(exeDir)) return new AppEnv(Path.Combine(exeDir, "data"), "exe のフォルダに書き込めるため exe の隣");
        return new AppEnv(local, "exe のフォルダに書き込めないため %LOCALAPPDATA%");
    }

    private static bool CanWrite(string dir)
    {
        try
        {
            var p = Path.Combine(dir, $".gkk-write-test-{Guid.NewGuid():N}");
            File.WriteAllText(p, "x"); File.Delete(p);
            return true;
        }
        catch { return false; }
    }

    public static string ReadResource(string name)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name) ?? throw new FileNotFoundException("埋め込みリソースがありません: " + name);
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    /// <summary>Profile は data\profiles\torneko-remaster\profile.json（無ければ同梱版を書き出す）。中身はキー割当とウィンドウ条件だけで攻略知識はない。</summary>
    private GameProfile LoadProfile()
    {
        var path = Path.Combine(ProfileDataDir, "profile.json");
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(ProfileDataDir);
            File.WriteAllText(path, ReadResource($"profiles/{ProfileId}/profile.json"));
            StartupNotes.Add($"同梱の Profile を書き出しました: {path}");
        }
        try
        {
            var mr = ProfileRetiredKeys.Migrate(path);
            if (mr.Migrated)
            {
                Log.Write("profile_retired_keys_removed", ("keys", mr.Removed.ToList()), ("backup", Path.GetFileName(mr.BackupPath)));
                StartupNotes.Add($"Profile から廃止した欄（{string.Join(", ", mr.Removed)}）を取り除きました。元のファイルは {Path.GetFileName(mr.BackupPath)} に退避しました");
            }
        }
        catch (Exception e) { Log.Write("profile_retired_keys_error", ("detail", e.Message)); }
        try
        {
            var ur = ProfileUpgrade.Migrate(path, ReadResource($"profiles/{ProfileId}/profile.json"));
            if (ur.Upgraded)
            {
                Log.Write("profile_upgraded", ("mode", ur.Mode), ("from", ur.FromVersion), ("to", ur.ToVersion), ("chords", ur.Chords), ("backup", Path.GetFileName(ur.BackupPath)));
                StartupNotes.Add(ur.Mode == "replaced_with_shipped"
                    ? $"Profile を同梱の {ur.ToVersion} に更新しました（button9=Tab の追加・同時押し {ur.Chords} 件）。元のファイルは {Path.GetFileName(ur.BackupPath)} に退避しました"
                    : $"Profile に同梱の同時押し {ur.Chords} 件を足しました（キーの割当は変えていません）。元のファイルは {Path.GetFileName(ur.BackupPath)} に退避しました");
            }
        }
        catch (Exception e) { Log.Write("profile_upgrade_error", ("detail", e.Message)); }
        try { var p = GameProfileLoader.Load(path); Log.Write("profile_loaded", ("schema", p.SchemaVersion)); return p; }
        catch (ProfileValidationException) when (File.ReadAllText(path).Contains("\"schema_version\": 1", StringComparison.Ordinal))
        {
            // 旧スキーマ v1（意味を含むキー名・表示名）: 退避して同梱の v2（中立のキー名・診断専用の既知操作）に置き換える
            var bak = path + $".v1-{DateTime.Now:yyyyMMdd-HHmmss}.bak";
            File.Move(path, bak);
            File.WriteAllText(path, ReadResource($"profiles/{ProfileId}/profile.json"));
            StartupNotes.Add($"Profile を v2（中立のキー名）に更新しました。旧版は {Path.GetFileName(bak)} に退避しました");
            var p = GameProfileLoader.Load(path);
            Log.Write("profile_migrated", ("from", 1), ("to", p.SchemaVersion), ("backup", Path.GetFileName(bak)));
            return p;
        }
    }

    private void RecordHardware()
    {
        try
        {
            var gc = GC.GetGCMemoryInfo();
            long? total = gc.TotalAvailableMemoryBytes > 0 ? gc.TotalAvailableMemoryBytes / 1024 / 1024 : null;
            long? free = null;
            try { free = new DriveInfo(Path.GetPathRoot(DataRoot)!).AvailableFreeSpace / 1024 / 1024; } catch { }
            AppDb.RecordHardware(new HardwareSnapshot(Environment.OSVersion.VersionString, null, Environment.ProcessorCount, total, null, free,
                Json.Obj(("x64", Environment.Is64BitProcess), ("avx2", System.Runtime.Intrinsics.X86.Avx2.IsSupported), ("avx512f", System.Runtime.Intrinsics.X86.Avx512F.IsSupported))));
        }
        catch (Exception e) { Log.Write("hardware_error", ("error", e.Message)); }
    }

    public string ComponentDir(string id) => Manifest.Find(id) is ComponentSpec c ? Components.InstallPath(c) : Path.Combine(DataRoot, "components", id);

    /// <summary>診断データ（DB の一貫コピー・ログ・部品の導入記録・直近の停止時画面）を zip に書き出す</summary>
    public DiagnosticExport.Result ExportDiagnostics(IEnumerable<string>? extraSummary = null)
    {
        var dir = Path.Combine(DataRoot, "exports");
        var zip = Path.Combine(dir, $"diag-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        var files = new List<(string, string)>();
        var logDir = Path.Combine(DataRoot, "logs");
        if (Directory.Exists(logDir))
            foreach (var f in Directory.GetFiles(logDir, "*.jsonl").OrderByDescending(File.GetLastWriteTimeUtc).Take(10))
                files.Add(("logs/" + Path.GetFileName(f), f));
        if (Directory.Exists(logDir))
            foreach (var f in Directory.GetFiles(logDir, "*.log").OrderByDescending(File.GetLastWriteTimeUtc).Take(3))
                files.Add(("logs/" + Path.GetFileName(f), f));   // launcher.log（起動ツールの .NET 準備の記録）
        files.Add(("components/manifest.lock", Components.LockPath));
        var rtDir = LauncherRuntimeDir();
        if (rtDir != null && Directory.Exists(Path.Combine(rtDir, ".gkk")))
            foreach (var f in Directory.GetFiles(Path.Combine(rtDir, ".gkk"), "*.lock"))
                files.Add(("runtime/" + Path.GetFileName(f), f));
        files.Add(("profile/profile.json", Path.Combine(ProfileDataDir, "profile.json")));   // キー割当とウィンドウ条件だけ（攻略知識はない）
        try
        {
            var vpath = ExperienceVNextStore.DefaultPath(ProfileDataDir);
            if (File.Exists(vpath)) files.Add(("experience-vnext.sqlite", vpath));
        }
        catch { }
        // 画面の画像は入れない（2026-10-18: 実機確認は診断 zip だけで行い、画像は開かない）。名前と大きさとハッシュだけを一覧にする
        var imageList = new List<string>();
        if (Directory.Exists(ProfileDataDir))
            foreach (var f in Directory.GetFiles(ProfileDataDir, "last_screen_*.png").OrderByDescending(File.GetLastWriteTimeUtc).Take(3))
                try { imageList.Add($"画像（zip には含めない）: {Path.GetFileName(f)} {new FileInfo(f).Length} bytes sha256={Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(f))).ToLowerInvariant()}"); } catch { }
        var summary = new StringBuilder();
        summary.AppendLine($"ゲーム攻略くん {Version} 診断データ {DateTime.Now:yyyy-MM-dd HH:mm:ss}（PC の現地時刻）");
        summary.AppendLine($"データフォルダ: {DataRoot}（{DataRootReason}）");
        summary.AppendLine($"実行環境: {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}（{System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory()}）"
            + (Environment.GetEnvironmentVariable("GKK_LAUNCHER") != null ? " ゲーム攻略くん.exe 経由で起動" : " 本体を直接起動"));
        foreach (var st in Components.CheckAll()) summary.AppendLine($"部品 {st.Spec.Id}@{st.Spec.Version}: {st.Summary}");
        // v0.33.4: dual-append の有効状態を zip だけで検証できるように記録（env 優先 → UI → OFF）
        RuntimeFlags.ApplyEffective();
        summary.AppendLine(RuntimeFlags.StatusText());
        summary.AppendLine($"vnext_dual_append effective={RuntimeFlags.UseExperienceVNextWriter} source={RuntimeFlags.EffectiveSource} ui={RuntimeFlags.UiEnabled} env={RuntimeFlags.EnvForcesOn}");
        RuntimeFlags.ApplyBundleEffective();
        summary.AppendLine(RuntimeFlags.BundleStatusText());
        summary.AppendLine($"observation_bundle_attach effective={RuntimeFlags.AttachObservationBundle} env={RuntimeFlags.BundleEnvForcesOn}");
        try
        {
            var vpath = ExperienceVNextStore.DefaultPath(ProfileDataDir);
            if (File.Exists(vpath))
            {
                var fi = new FileInfo(vpath);
                summary.AppendLine($"experience-vnext.sqlite: あり {fi.Length} bytes（最終更新 {fi.LastWriteTime:yyyy-MM-dd HH:mm:ss}）");
            }
            else summary.AppendLine("experience-vnext.sqlite: なし（まだ dual-append で書かれていないか、フラグ OFF）");
        }
        catch (Exception e) { summary.AppendLine("experience-vnext.sqlite: 確認失敗 " + e.Message); }
        foreach (var r in Memory.Runs(20)) summary.AppendLine($"探索 #{r.RunId}: 開始 {r.StartedAt} 終了 {r.EndedAt ?? "（未記録）"} 理由 {r.EndReason ?? "-"}{(r.EndDetail != null ? "／" + r.EndDetail : "")} ターン {r.TurnsTotal}");
        try { foreach (var r in Recordings.List()) summary.AppendLine($"以前の版の「画面を保存」の残り #{r.RunId}（7日で自動削除）: 画面 {r.Frames} 枚 {r.Bytes / (1024 * 1024)} MB（最終更新 {r.LastWriteUtc.ToLocalTime():yyyy-MM-dd HH:mm}）"); } catch { }
        if (extraSummary != null) foreach (var l in extraSummary) summary.AppendLine(l);
        foreach (var l in imageList) summary.AppendLine(l);
        var dbs = new List<(string, SqliteDb)> { ("memory.sqlite", Memory.Db), ("app.sqlite", AppDb.Db) };
        if (_diagnostics != null || File.Exists(Path.Combine(ProfileDataDir, DiagnosticStore.FileName))) dbs.Add((DiagnosticStore.FileName, Diagnostics.Db));
        var texts = new List<(string, string)>();
        try
        {
            var logs = Directory.Exists(logDir) ? Directory.GetFiles(logDir, "app-*.jsonl").OrderByDescending(File.GetLastWriteTimeUtc).ToList() : new List<string>();
            var hr = HealthReport.Build(Version, Memory.Db, AppDb.Db, dbs.Count > 2 ? Diagnostics.Db : null, logs, Profile.SchemaVersion, null);
            texts.Add(("health.txt", hr.Text)); texts.Add(("health.json", hr.Json));
            summary.Insert(0, $"健全性: {hr.Overall}（詳細は health.txt）\n");
        }
        catch (Exception e) { texts.Add(("health.txt", "健全性レポートを作れませんでした: " + e)); }
        var res = DiagnosticExport.Write(zip, dbs.ToArray(), files, summary.ToString(), texts);
        Log.Write("diagnostics_export", ("zip", res.ZipPath), ("bytes", res.Bytes), ("entries", res.Entries.ToList()));
        return res;
    }

    /// <summary>起動ツールが用意した .NET の場所（DOTNET_ROOT。無ければホームの runtime\）。</summary>
    private string? LauncherRuntimeDir()
    {
        var env = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrEmpty(env)) return env;
        var home = Core.AppHome.Resolve(ExeDir, Environment.GetEnvironmentVariable(Core.AppHome.EnvVar), File.Exists, Directory.Exists);
        return Path.Combine(home, "runtime");
    }

    public void Dispose()
    {
        try { Memory.Dispose(); } catch { }
        try { _diagnostics?.Dispose(); } catch { }
        try { AppDb.Dispose(); } catch { }
        Log.Write("exit");
        Log.Dispose();
    }
}
