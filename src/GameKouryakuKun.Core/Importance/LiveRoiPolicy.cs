namespace GameKouryakuKun.Core.Importance;

/// <summary>
/// S4 ライブ ROI（docs/s4-importance-attention-contract.md §3.1）。
/// OCR エンジン呼び出し強度のみを可逆に下げる。GT / Track / capture は触らない。
/// 永久削除禁止・欠測ゼロ: deferred は Inherited（前行を継承）、not_observed にしない。
/// </summary>
public static class LiveRoiPolicy
{
    /// <summary>環境変数。未設定時は Importance ON ならライブ ON。"0" で advisory のみ強制。</summary>
    public const string LiveRoiEnvName = "GKK_LIVE_ROI";

    /// <summary>根拠: 床付近でも低頻度再確認（§6.3）。重み floor のとき最大この手数でエンジンを強制。</summary>
    public const int MaxDeferStepsAtFloor = 8;
    /// <summary>根拠: 高重要度でも稀に deferred を挟める上限（ほぼ毎回読む）。</summary>
    public const int MaxDeferStepsAtHigh = 2;
    /// <summary>根拠: この重み以上は原則 defer しない（ContextHelps 級）。</summary>
    public const double HighOcrWeightNoDefer = 0.85;

    public static bool LiveRoiEnvForcesOff =>
        string.Equals(Environment.GetEnvironmentVariable(LiveRoiEnvName), "0", StringComparison.Ordinal);

    public static bool LiveRoiEnvForcesOn =>
        string.Equals(Environment.GetEnvironmentVariable(LiveRoiEnvName), "1", StringComparison.Ordinal);

    /// <summary>ライブ OCR 強度制御が有効か。Importance OFF または GKK_LIVE_ROI=0 なら false。</summary>
    public static bool IsLiveEnabled(bool importanceAttached)
    {
        if (!importanceAttached) return false;
        if (LiveRoiEnvForcesOff) return false;
        return true; // 未設定または =1 → Importance ON に追随
    }

    public static string StatusText(bool importanceAttached)
    {
        if (!importanceAttached)
            return "Live ROI: OFF（Importance 既定 OFF）";
        if (LiveRoiEnvForcesOff)
            return "Live ROI: OFF（GKK_LIVE_ROI=0・advisory のみ）";
        return "Live ROI: ON（OCR エンジン defer / rare recheck。GT/Track 不変）";
    }

    /// <summary>
    /// OCR エンジン（full/region）を今手 defer してよいか。
    /// Plan が既に reuse/cache のときは呼び出し側でスキップすること。
    /// </summary>
    public static bool ShouldDeferEngineRead(double ocrWeight, int stepsSinceEngineRead, out string reason)
    {
        double floor = AttentionWeights.RareRecheckFloor;
        double w = Math.Clamp(ocrWeight, floor, 1.0);
        // maxDefer: w=floor → MaxDeferStepsAtFloor, w=1 → MaxDeferStepsAtHigh
        double t = (w - floor) / Math.Max(1e-9, 1.0 - floor);
        int maxDefer = (int)Math.Round(MaxDeferStepsAtFloor + t * (MaxDeferStepsAtHigh - MaxDeferStepsAtFloor));
        maxDefer = Math.Clamp(maxDefer, MaxDeferStepsAtHigh, MaxDeferStepsAtFloor);

        if (stepsSinceEngineRead >= maxDefer)
        {
            reason = "rare_recheck_floor";
            return false;
        }
        if (w >= HighOcrWeightNoDefer)
        {
            reason = "high_ocr_weight";
            return false;
        }
        reason = "defer_low_ocr_weight";
        return true;
    }
}
