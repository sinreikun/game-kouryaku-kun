namespace GameKouryakuKun.Core.Observation;

/// <summary>
/// v0.32.0（P-6）: 追跡（Entity）ごとの経験。同じ追跡番号の上で、見た目・手・動き・関係・文字と数字の変化を結び付けて数える（記録だけ。手の選び方・学習には使わない）。
/// 同一性は追跡（TrackEvidence）が決め、見た目は表の中身の 1 つ（見た目が変わっても Entity は切れない）。
/// 予測は数える前に出し、その時点の番号が安定していたか（EntityCandidate.StableIdentity）で分けて当たりを数える
/// </summary>
public sealed class EntityExperience
{
    public static readonly string[] Tables = { "appearance", "movement", "relation", "text_number" };
    public const int MaxKeys = 200000;   // 根拠: 記憶の上限（他の表の上限と同じ桁）。超えたら新しい組は数えない（数えなかった数を残す）
    private readonly Dictionary<(int Track, string Ctx, string Action, int Table), Dictionary<string, int>> _c = new();
    public readonly long[,] Predicted = new long[4, 2], Hits = new long[4, 2];   // [表, 0=安定した番号 1=不安定]
    public long Observed { get; private set; }
    public long Dropped { get; private set; }
    public int Keys => _c.Count;

    public static string Move(EntityCandidate c) => c.Moved ? $"{Math.Sign(Math.Round(c.Dx))},{Math.Sign(Math.Round(c.Dy))}" : "0,0";

    // v0.32.1（P-6 追従）: 組 (A, B) ごとに「前の手に A が向き d へ動いた」ときと「そうでない」ときに、B が今の手で同じ向き d へ動いたかを数える
    private readonly Dictionary<(int A, int B), long[]> _follow = new();   // [A が前に動いた手の数, そのうち B が同じ向きに動いた, A が前に動かなかった手の数, そのうち B が（何かの向きに）動いた]
    private Dictionary<int, string> _prevMoves = new();

    /// <summary>追従の組: A が動いた次の手に B が同じ向きに動く率が、A が動かなかった手の B の動く率より高い（ベイズ因子 ≥ ln10・最小 3 回）</summary>
    public IReadOnlyList<(int A, int B, long Follow, long Trials, double LogBf)> Follows()
    {
        var res = new List<(int, int, long, long, double)>();
        foreach (var ((a, b), v) in _follow)
        {
            if (v[1] < RegionCovariation.MinChanges) continue;   // 根拠: 2 値の数え上げで差が出る最小（RegionCovariation と同じ）
            double r1 = (double)v[1] / Math.Max(1, v[0]), r0 = (double)v[3] / Math.Max(1, v[2]);
            if (r1 <= r0) continue;
            double sep = GameKouryakuKun.Core.Exploration.RegionCausality.LogBeta((int)v[1], (int)(v[0] - v[1])) + GameKouryakuKun.Core.Exploration.RegionCausality.LogBeta((int)v[3], (int)(v[2] - v[3]));
            double pooled = GameKouryakuKun.Core.Exploration.RegionCausality.LogBeta((int)(v[1] + v[3]), (int)(v[0] - v[1] + v[2] - v[3]));
            if (sep - pooled >= Math.Log(10)) res.Add((a, b, v[1], v[0], Math.Round(sep - pooled, 2)));   // 根拠: RevertMinLogBayesFactor と同じ
        }
        return res.OrderByDescending(x => x.Item5).ToList();
    }

    private void ObserveFollow(EntityLayer cur)
    {
        var now = cur.Candidates.Where(c => c.StableIdentity).GroupBy(c => c.Track).ToDictionary(g => g.Key, g => Move(g.First()));
        foreach (var (b, mb) in now)
            foreach (var (a, ma) in _prevMoves)
            {
                if (a == b) continue;
                if (!_follow.TryGetValue((a, b), out var v)) { if (_follow.Count >= MaxKeys) { Dropped++; continue; } _follow[(a, b)] = v = new long[4]; }
                if (ma != "0,0") { v[0]++; if (mb == ma) v[1]++; }
                else { v[2]++; if (mb != "0,0") v[3]++; }
            }
        _prevMoves = now;
    }

    public void Observe(EntityLayer? prev, EntityLayer cur, string action, bool textChanged, bool numberChanged)
    {
        ObserveFollow(cur);
        if (prev == null) return;
        var before = prev.Candidates.GroupBy(c => c.Track).ToDictionary(g => g.Key, g => g.First());
        foreach (var c in cur.Candidates)
        {
            if (c.New || !before.TryGetValue(c.Track, out var b)) continue;
            int st = c.StableIdentity ? 0 : 1;
            // v0.32.2: 関係は相手ごとに数える（v0.32.1 までは「いちばん近い相手」の変化を相手を区別せずに数えていて、相手が手ごとに入れ替わり当たりが 0.33 にとどまった）。
            //          相手が新しい追跡の組（change = new）は前の関係がないので数えない
            var rels = cur.Relations.Where(r => (r.TrackA == c.Track || r.TrackB == c.Track) && r.Change != "new").ToList();
            string ev = c.Moved ? "moved" : c.AppearanceChanged ? "appearance" : "same";
            Count(0, c.Track, b.Appearance, action, c.Appearance, st);           // 見た目 → 次の見た目
            Count(1, c.Track, Move(b), action, Move(c), st);                     // 前の動き → 次の動き
            foreach (var r in rels) Count(2, c.Track, "#" + (r.TrackA == c.Track ? r.TrackB : r.TrackA), action, r.Change, st);   // 相手ごとの関係の変化
            Count(3, c.Track, ev, action, (textChanged ? "t" : "-") + (numberChanged ? "n" : "-"), st);   // この Entity の出来事と同じ手の文字・数字の変化
            Observed++;
        }
    }

    private void Count(int table, int track, string ctx, string action, string outcome, int st)
    {
        var k = (track, ctx, action, table);
        if (_c.TryGetValue(k, out var d) && d.Count > 0)
        {
            var best = d.OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.Ordinal).First().Key;
            Predicted[table, st]++; if (best == outcome) Hits[table, st]++;
        }
        if (d == null) { if (_c.Count >= MaxKeys) { Dropped++; return; } _c[k] = d = new(); }
        d[outcome] = d.GetValueOrDefault(outcome) + 1;
    }

    public string? Predict(int table, int track, string ctx, string action) =>
        _c.TryGetValue((track, ctx, action, table), out var d) && d.Count > 0 ? d.OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.Ordinal).First().Key : null;

    /// <summary>
    /// S2: Bundle 付帯用の読み取り専用スナップショット。表・追従の集計をコピーするだけ（学習・しきい値・同一性は変えない）。
    /// サイズビン（TrackQuality）は人間診断用のため含めない。
    /// </summary>
    public EntityExperienceSnapshot Snapshot(int maxFollows = 32)
    {
        // 根拠: Bundle 付帯の追従組上限（診断 zip 肥大化防止。全組が必要なら後続でページング）
        var tables = new List<EntityExperienceTableStats>(4);
        for (int ti = 0; ti < Tables.Length; ti++)
        {
            tables.Add(new EntityExperienceTableStats(
                Tables[ti],
                Predicted[ti, 0], Hits[ti, 0],
                Predicted[ti, 1], Hits[ti, 1]));
        }
        var follows = Follows().Take(Math.Max(0, maxFollows))
            .Select(f => new FollowPairSnapshot(f.A, f.B, f.Follow, f.Trials, f.LogBf))
            .ToList();
        return new EntityExperienceSnapshot
        {
            Observed = Observed,
            Keys = Keys,
            Dropped = Dropped,
            Tables = tables,
            Follows = follows,
        };
    }

    public string Summary()
    {
        static string R(long h, long p) => p == 0 ? "-" : $"{(double)h / p:0.00}";
        return string.Join("／", Enumerable.Range(0, 4).Select(t =>
            $"{Tables[t]}: 安定した番号 {Hits[t, 0]}/{Predicted[t, 0]}（{R(Hits[t, 0], Predicted[t, 0])}）・不安定 {Hits[t, 1]}/{Predicted[t, 1]}（{R(Hits[t, 1], Predicted[t, 1])}）"))
            + $"／追従の組（v0.32.1） {Follows().Count}（{string.Join(",", Follows().Take(3).Select(f => $"#{f.A}→#{f.B} {f.Follow}/{f.Trials}"))}）"
            + $"／数えた {Observed}・組 {Keys}・上限で数えなかった {Dropped}";
    }
}
