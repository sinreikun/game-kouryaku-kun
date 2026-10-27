using GameKouryakuKun.Core.Capture;
using GameKouryakuKun.Core.Diagnostics;
using GameKouryakuKun.Core.Imaging;
using GameKouryakuKun.Core.Input;

namespace GameKouryakuKun.Core.Probe;

/// <summary>前面ウィンドウの状態</summary>
public enum ForegroundRole
{
    /// <summary>状態A: ゲームが前面（基準値）</summary>
    GameForeground,
    /// <summary>状態B: 攻略くん側の窓が前面でゲームは背面（分離動作の本命）</summary>
    OtherForeground,
}

public sealed record InputSelection(string? BackendId, IsolationLevel? Level, bool NeedsConsent, string Reason);

public static class InputBackendSelector
{
    /// <summary>
    /// 選択ポリシー（v0.15.0）: 送達（A＝OS・入力方式として対象のウィンドウへ送れる）が確かめられた方式を、分離等級の高い順に選ぶ。
    /// 画面の反応（B）は条件にしない（特定の状態でキーが効くことを正常の条件にしない）。占有モード（SendInput）は同意があるときだけ Probe が利用可を返す
    /// </summary>
    public static InputSelection SelectByDelivery(IEnumerable<(IInputBackend Backend, bool Deliverable)> candidates)
    {
        var c = candidates.ToList();
        foreach (var level in new[] { IsolationLevel.Isolated, IsolationLevel.DeviceGlobal, IsolationLevel.ForegroundShared })
            foreach (var (b, ok) in c.Where(x => x.Backend.Isolation.Level == level))
                if (ok) return new(b.Id, level, level == IsolationLevel.ForegroundShared, $"{b.DisplayName} が送信可能（{b.Isolation.Badge}）。画面の反応は探索の各手で観測します");
        return new(null, null, false, "どの入力方式も対象のウィンドウへ送れません");
    }
}


public sealed record InputProbeResultRow(string BackendId, string DisplayName, string Isolation, bool Available, string Detail);
