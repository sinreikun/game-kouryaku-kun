namespace GameKouryakuKun.Core.Imaging;

/// <summary>
/// ウィンドウ画像からクライアント領域（タイトルバー・枠を除いたゲーム描画部分）を切り出す位置の計算。
/// WGC の画像は DWM の拡張フレーム（タイトルバー込み）、PrintWindow は GetWindowRect 全体なので、
/// 画像の左上（スクリーン座標）とクライアント領域の左上（スクリーン座標）の差が切り出し位置になる。
/// </summary>
public static class ClientCrop
{
    /// <summary>切り出し矩形。画像からはみ出す・小さすぎる・画像全体と同じときは null（切り出さない）</summary>
    public static (int X, int Y, int W, int H)? Compute(int frameW, int frameH, int frameScreenX, int frameScreenY,
        int clientScreenX, int clientScreenY, int clientW, int clientH, int minSide = 64)
    {
        int x = clientScreenX - frameScreenX, y = clientScreenY - frameScreenY;
        if (x < 0 || y < 0 || clientW < minSide || clientH < minSide) return null;
        int w = Math.Min(clientW, frameW - x), h = Math.Min(clientH, frameH - y);
        // 画像が縮んでいる（スケール違い・取得途中のリサイズ）ときに大きく欠けるなら切らない
        if (w < clientW - 2 || h < clientH - 2 || w < minSide || h < minSide) return null;
        if (x == 0 && y == 0 && w == frameW && h == frameH) return null;
        return (x, y, w, h);
    }
}
