namespace GameKouryakuKun.Core.Imaging;

/// <summary>
/// v0.29.2: 区画ごとの画素のハッシュを、取り込みのノイズで変わらないようにする。
/// 区画の間引いた点のどれかが、前に「変わった」とした時の点から FrameDiff の画素のしきい値（既存・24）を超えて違うときだけ、その区画のハッシュを新しくする。
/// 超えなければ前のハッシュのまま（＝変わっていない）。少しずつのずれも基準からの差で数えるので、たまれば変わったとする。
/// 実機 #110: 4 ビットに丸めたハッシュの完全一致では、画面が落ち着いた手でも 192 区画のうち中央値 191 区画が「変わった」になり、
/// 文字の継承・一部の読み直し・変化領域・局所パターンの候補が働いていなかった。
/// </summary>
public sealed class CellStabilizer
{
    private readonly int _cols, _rows, _samples;
    public int Threshold { get; init; } = new DiffOptions().PixelThreshold;   // 根拠: 既存の画素の変化のしきい値と同じ（新しい値を置かない）
    private byte[]? _ref; private ulong[]? _hash; private int _w, _h;
    public long Stabilized { get; private set; }
    public long Changed { get; private set; }

    public CellStabilizer(int cols, int rows, int samplesPerAxis) { _cols = cols; _rows = rows; _samples = samplesPerAxis; }

    /// <summary>区画ごとの点（間引き）と、その点の 4 ビットに丸めたハッシュ</summary>
    public (byte[] Samples, ulong[] Hash) Sample(Frame f)
    {
        int n = _samples * _samples * 3; var s = new byte[_cols * _rows * n]; var h = new ulong[_cols * _rows];
        for (int gy = 0; gy < _rows; gy++)
        for (int gx = 0; gx < _cols; gx++)
        {
            int c = gy * _cols + gx, x0 = gx * f.Width / _cols, x1 = Math.Max(x0 + 1, (gx + 1) * f.Width / _cols), y0 = gy * f.Height / _rows, y1 = Math.Max(y0 + 1, (gy + 1) * f.Height / _rows);
            ulong v = 14695981039346656037UL; int k = c * n;
            for (int sy = 0; sy < _samples; sy++)
            for (int sx = 0; sx < _samples; sx++)
            {
                int x = Math.Min(f.Width - 1, x0 + (x1 - x0) * sx / _samples), y = Math.Min(f.Height - 1, y0 + (y1 - y0) * sy / _samples);
                int i = (y * f.Width + x) * 4;
                s[k++] = f.Bgra[i]; s[k++] = f.Bgra[i + 1]; s[k++] = f.Bgra[i + 2];
                v ^= (ulong)((f.Bgra[i] >> 4) | ((f.Bgra[i + 1] >> 4) << 4) | ((f.Bgra[i + 2] >> 4) << 8)); v *= 1099511628211UL;
            }
            h[c] = v;
        }
        return (s, h);
    }

    /// <summary>ノイズでは変わらない区画のハッシュ（大きさが変われば作り直す）</summary>
    public ulong[] Apply(Frame f)
    {
        var (s, h) = Sample(f);
        if (_ref == null || _w != f.Width || _h != f.Height) { _ref = s; _hash = h; _w = f.Width; _h = f.Height; return (ulong[])h.Clone(); }
        int n = _samples * _samples * 3;
        for (int c = 0; c < h.Length; c++)
        {
            bool ch = false;
            for (int k = c * n, e = k + n; k < e; k++) if (Math.Abs(s[k] - _ref[k]) > Threshold) { ch = true; break; }
            if (ch) { Array.Copy(s, c * n, _ref, c * n, n); _hash![c] = h[c] == _hash[c] ? h[c] ^ 0x9E3779B97F4A7C15UL : h[c]; Changed++; }
            else if (h[c] != _hash![c]) Stabilized++;
        }
        return (ulong[])_hash!.Clone();
    }
}
