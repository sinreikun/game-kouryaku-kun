namespace GameKouryakuKun.Core.Observation;

/// <summary>重ね表示の 1 つの要素（座標は画面の画素）。Kind: region・entity・move・relation・text。Style は表示の種類（意味のラベルではない）</summary>
public sealed record OverlayItem(string Kind, double X, double Y, double W, double H, string Label, string Style, double X2 = 0, double Y2 = 0);

/// <summary>重ね表示の全体: 図形と、横に出す表の行</summary>
public sealed record OverlayModel(IReadOnlyList<OverlayItem> Items, IReadOnlyList<string> Lines);

/// <summary>
/// v0.30.0（P-1）: 構造化した観測だけから「攻略くんが何を見ているか」の重ね表示を作る（監査用。選択・学習には使わない）。
/// 名前は観測の種類だけ（track・appearance_state・causality など）。人間が意味を教える入力はない
/// </summary>
public static class ObservationOverlay
{
    public static OverlayModel Build(StructuredObservation o, int width, int height, IReadOnlyList<string>? extra = null)
    {
        var items = new List<OverlayItem>(); var lines = new List<string>();
        double rcw = width / 16.0, rch = height / 12.0;   // 画素の区画（TextReader.GridCols×GridRows）
        foreach (var r in o.Regions.Regions)
            items.Add(new OverlayItem("region", r.X0 * rcw, r.Y0 * rch, (r.X1 - r.X0 + 1) * rcw, (r.Y1 - r.Y0 + 1) * rch,
                $"R{r.Track}{(r.NewTrack ? "*" : "")} {r.Causality} 自然 {r.NaturalRate:0.00}", "region_" + r.Causality));
        var el = o.EntityLayer;
        double fcw = width / (double)FineGrid.DefaultCols, fch = height / (double)FineGrid.DefaultRows;
        var centers = new Dictionary<int, (double X, double Y)>();
        if (el != null)
        {
            foreach (var c in el.Candidates)
            {
                double x = c.X0 * fcw, y = c.Y0 * fch, w = (c.X1 - c.X0 + 1) * fcw, h = (c.Y1 - c.Y0 + 1) * fch;
                string st = c.New ? "entity_new" : c.Moved ? "entity_moved" : c.PeriodicInPlace ? "entity_periodic" : c.AppearanceChanged ? "entity_appearance" : "entity";
                items.Add(new OverlayItem("entity", x, y, w, h, $"#{c.Track} {c.AppearanceState}{(c.PeriodicInPlace ? " その場の周期" : "")} 確信 {c.Confidence:0.00} 見えた {c.Age} 手／{c.Lifetime} 手{(c.New ? "" : c.StableIdentity ? " 同一性 安定" : " 同一性 弱い")}", st));
                double cx = (c.Cx + 0.5) * fcw, cy = (c.Cy + 0.5) * fch; centers[c.Track] = (cx, cy);
                // v0.31.6: 同じ追跡の最近の中心の軌跡（同じ番号が続いているかを目で確かめる）
                for (int k = 1; k < c.Trail.Count; k++)
                    items.Add(new OverlayItem("trail", (c.Trail[k - 1].X + 0.5) * fcw, (c.Trail[k - 1].Y + 0.5) * fch, 0, 0, k == c.Trail.Count - 1 ? $"#{c.Track}" : "", "trail", (c.Trail[k].X + 0.5) * fcw, (c.Trail[k].Y + 0.5) * fch));
                if (c.Moved) items.Add(new OverlayItem("move", cx - c.Dx * fcw, cy - c.Dy * fch, 0, 0, $"({c.Dx:+0.#;-0.#;0},{c.Dy:+0.#;-0.#;0})", "move", cx, cy));
            }
            foreach (var st in el.Static)   // v0.31.3（P-5）: 静止したまま存続している候補（細い枠）
            {
                double x = st.X0 * fcw, y = st.Y0 * fch, w = (st.X1 - st.X0 + 1) * fcw, h = (st.Y1 - st.Y0 + 1) * fch;
                items.Add(new OverlayItem("entity", x, y, w, h, $"#{st.Track} {st.AppearanceState} 静止 {st.StillSteps} 手", "entity_static"));
                centers.TryAdd(st.Track, (x + w / 2, y + h / 2));
            }
            foreach (var gr in el.Groups)
                items.Add(new OverlayItem("group", gr.X0 * fcw - 2, gr.Y0 * fch - 2, (gr.X1 - gr.X0 + 1) * fcw + 4, (gr.Y1 - gr.Y0 + 1) * fch + 4,
                    $"G{gr.Id}（{string.Join(",", gr.Tracks.Select(t => "#" + t))}・いっしょ {gr.CoSteps} 回）", "group"));
            foreach (var r in el.Relations)
                if (centers.TryGetValue(r.TrackA, out var a) && centers.TryGetValue(r.TrackB, out var b))
                    items.Add(new OverlayItem("relation", a.X, a.Y, 0, 0, $"{r.Change}{(r.SameMove ? "・同じ動き" : "")}{(r.Overlap ? "・重なり" : r.Adjacent ? "・隣接" : "")} 距離 {r.Distance:0.#}", "relation_" + r.Change, b.X, b.Y));
        }
        foreach (var t in o.Text.Lines)
            items.Add(new OverlayItem("text", t.X, t.Y, t.W, t.H, $"{t.Text}（{t.Confidence:0.00}・{t.Change}）", t.Status == LayerStatus.Inherited ? "text_inherited" : "text_observed"));

        lines.Add($"手 {o.Step} {o.Action}（{o.Provenance}）  {o.Before} → {o.After}");
        lines.Add($"大域 {o.Global.Mode}・配置 {o.Global.Layout}・見た目の状態 {o.Global.VisualState}");
        lines.Add($"層: 区画 {o.Regions.Status}（変わった {o.Regions.ChangedCells}）・候補 {o.Entities}（{el?.Candidates.Count ?? 0}・消えた {el?.Disappeared.Count ?? 0}）・関係 {el?.Relations.Count ?? 0}{(el != null && el.RelationPairs > el.Relations.Count ? $"（全部の組 {el.RelationPairs} から近い組に絞った）" : "")}・文字 {o.Text.Status}（{o.Text.ReadKind}・観測 {o.Text.Observed}／継承 {o.Text.Inherited}・差 {o.Text.DiffKind}・手が起こした割合 {o.Text.ActionCaused:0.00}）");
        if (o.Regions.CommonShift != null) lines.Add($"区画は共通のずれを差し引いた後の変化（差し引く前 {o.Regions.RawChangedCells}）");
        if (el?.ShiftProbe is { } sp && el.CommonShift == null) lines.Add($"共通のずれは無し（調べたずれ ({sp[0]},{sp[1]})・全体の差の比 {sp[2]}・良くなった区画 {sp[3]:0.00}）");
        if (el?.CommonShift is { } cs) lines.Add($"画面全体の共通のずれ ({cs[0]:+0.##;-0.##;0},{cs[1]:+0.##;-0.##;0}) 区画（差し引いてから追跡。候補の動きはこのずれに対する動き）");
        lines.Add($"時間: 静止まで {o.Temporal.BoundaryMs:0} ms・変化 {(o.Temporal.Changed ? "あり" : "なし")}・入力なしの変化の率 {(o.Temporal.NaturalChange is double n ? n.ToString("0.00") : "—")}");
        foreach (var nb in o.Text.Numbers.Where(x => x.Series.Count > 1).Take(4)) lines.Add($"数字の系列（区画 {nb.Cell}）: {string.Join("→", nb.Series.TakeLast(6))}");
        if (o.Prediction is { } p)
        {
            var occ = p.Occurred.Select(i => LocalEventModel.Events[i]).ToList();
            lines.Add($"起きた出来事: {(occ.Count == 0 ? "なし" : string.Join(",", occ))}");
            if (p.P != null)
                lines.Add("予測（確率／驚き）: " + string.Join("・", LocalEventModel.Events.Select((e, i) => $"{e} {p.P[i]:0.00}" + (p.Surprise.Count > i ? $"/{p.Surprise[i]:0.00}" : ""))));
            else lines.Add("予測: この画面×手の局所の観測がまだない");
            if (p.PredictedMove is { } m) lines.Add($"予測した移動 ({m[0]},{m[1]})");
            if (p.WorldNext != null) lines.Add($"World Model: 次の画面 {p.WorldNext}（{p.WorldP:0.00}）→ {(p.WorldHit == true ? "当たり" : "外れ")}");
        }
        if (extra != null) lines.AddRange(extra);
        return new OverlayModel(items, lines);
    }
}
