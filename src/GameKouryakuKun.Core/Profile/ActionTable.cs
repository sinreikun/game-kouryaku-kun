using GameKouryakuKun.Core.Exploration;
using GameKouryakuKun.Core.Input;

namespace GameKouryakuKun.Core.Profile;

/// <summary>
/// 起動時の入力の診断（v0.26.0）: Profile から作る全操作（単独キー・chords）を、キーを送らずに確かめる。
/// 各操作の物理キー・VK・禁止キーの検査・PostMessage の押す/離す計画（同時押しは N キーの Down→Up）を作れるかを表にする。
/// </summary>
public static class ActionTable
{
    public sealed record Row(string Action, string Group, string PhysicalKeys, string Vks, int Events, bool Ok, string? Problem);

    public static List<Row> Build(GameProfile p)
    {
        var map = p.KeyMap(); var guard = p.Guard();
        var rows = new List<Row>();
        foreach (var a in Explorer.FromProfile(map.All.Keys, p.Input.Chords).Where(a => !a.IsWait))
        {
            try
            {
                var keys = a.Keys.Select(map.Resolve).ToList();
                var why = guard.Check(keys);
                var plan = InputPlanner.Plan(keys, p.Input.DefaultHoldMs, PressMode.Tap);
                bool shape = plan.Count == 2 * keys.Count && plan.Take(keys.Count).All(e => e.Type == KeyEventType.Down) && plan.Skip(keys.Count).All(e => e.Type == KeyEventType.Up);
                rows.Add(new Row(a.Name, a.Group.ToString(), string.Join("+", keys.Select(k => k.Name)), string.Join("+", keys.Select(k => $"0x{k.Vk:X2}")), plan.Count,
                    why == null && shape, why ?? (shape ? null : "押す/離す計画が同時押しの形になりません")));
            }
            catch (Exception e) { rows.Add(new Row(a.Name, a.Group.ToString(), "", "", 0, false, e.Message)); }
        }
        return rows;
    }
}
