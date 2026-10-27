using System.Buffers.Binary;
using System.IO.Compression;

namespace GameKouryakuKun.Core.Imaging;

/// <summary>依存ライブラリなしの最小 PNG エンコーダ（RGBA 8bit、行フィルタ Up）。</summary>
public static class Png
{
    private static readonly uint[] CrcTable = BuildCrc();
    private static uint[] BuildCrc()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++) { uint c = n; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1; t[n] = c; }
        return t;
    }
    private static uint Crc(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        uint c = 0xFFFFFFFFu;
        foreach (var b in type) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        foreach (var b in data) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }
    private static void Chunk(Stream s, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> len = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(len, (uint)data.Length); s.Write(len);
        var t = System.Text.Encoding.ASCII.GetBytes(type); s.Write(t); s.Write(data);
        Span<byte> crc = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(crc, Crc(t, data)); s.Write(crc);
    }

    /// <summary>BGRA を受け取り、アルファを不透明化して保存</summary>
    public static void SaveBgra(string path, int w, int h, byte[] bgra)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllBytes(path, Encode(w, h, bgra));
    }

    /// <summary>BGRA → PNG バイト列（RGBA 8bit、アルファは不透明化）</summary>
    public static byte[] Encode(int w, int h, byte[] bgra)
    {
        using var fs = new MemoryStream();
        fs.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0), w); BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), h);
        ihdr[8] = 8; ihdr[9] = 6; ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;
        Chunk(fs, "IHDR", ihdr);
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
        {
            var prev = new byte[w * 4]; var cur = new byte[w * 4]; var line = new byte[w * 4 + 1];
            for (int y = 0; y < h; y++)
            {
                int o = y * w * 4;
                for (int x = 0; x < w; x++)
                {
                    int i = o + x * 4;
                    cur[x * 4] = bgra[i + 2]; cur[x * 4 + 1] = bgra[i + 1]; cur[x * 4 + 2] = bgra[i]; cur[x * 4 + 3] = 255;
                }
                line[0] = 2; // Up filter
                for (int k = 0; k < cur.Length; k++) line[k + 1] = (byte)(cur[k] - prev[k]);
                z.Write(line);
                (prev, cur) = (cur, prev);
            }
        }
        Chunk(fs, "IDAT", ms.ToArray());
        Chunk(fs, "IEND", ReadOnlySpan<byte>.Empty);
        return fs.ToArray();
    }

    /// <summary>
    /// 最小 PNG デコーダ（8bit の RGB / RGBA / グレー、非インターレースのみ）→ BGRA。
    /// テストや「画像ファイルで OCR を試す」用。対応外の形式は NotSupportedException。
    /// </summary>
    public static (int Width, int Height, byte[] Bgra) DecodeBgra(byte[] png)
    {
        if (png.Length < 8 || png[0] != 0x89 || png[1] != 0x50) throw new InvalidDataException("PNG ではありません");
        int pos = 8, w = 0, h = 0, colorType = -1;
        using var idat = new MemoryStream();
        while (pos + 8 <= png.Length)
        {
            int len = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(pos)); string type = System.Text.Encoding.ASCII.GetString(png, pos + 4, 4);
            var data = png.AsSpan(pos + 8, len);
            if (type == "IHDR")
            {
                w = BinaryPrimitives.ReadInt32BigEndian(data); h = BinaryPrimitives.ReadInt32BigEndian(data[4..]);
                if (data[8] != 8 || data[12] != 0) throw new NotSupportedException("8bit・非インターレースの PNG のみ対応");
                colorType = data[9];
            }
            else if (type == "IDAT") idat.Write(data);
            else if (type == "IEND") break;
            pos += 12 + len;
        }
        int ch = colorType switch { 6 => 4, 2 => 3, 0 => 1, 4 => 2, _ => throw new NotSupportedException($"PNG カラー形式 {colorType} は未対応") };
        idat.Position = 0;
        using var z = new ZLibStream(idat, CompressionMode.Decompress);
        int stride = w * ch;
        var raw = new byte[(stride + 1) * h];
        int read = 0; while (read < raw.Length) { int n = z.Read(raw, read, raw.Length - read); if (n <= 0) break; read += n; }
        var prev = new byte[stride]; var cur = new byte[stride];
        var outp = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            int f = raw[y * (stride + 1)];
            Buffer.BlockCopy(raw, y * (stride + 1) + 1, cur, 0, stride);
            for (int i = 0; i < stride; i++)
            {
                int a = i >= ch ? cur[i - ch] : 0, b = prev[i], c = i >= ch ? prev[i - ch] : 0;
                cur[i] = f switch
                {
                    0 => cur[i],
                    1 => (byte)(cur[i] + a),
                    2 => (byte)(cur[i] + b),
                    3 => (byte)(cur[i] + ((a + b) >> 1)),
                    4 => (byte)(cur[i] + Paeth(a, b, c)),
                    _ => throw new InvalidDataException($"PNG フィルタ {f} が不正"),
                };
            }
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 4, s = x * ch;
                byte r, g, bl, al = 255;
                if (ch >= 3) { r = cur[s]; g = cur[s + 1]; bl = cur[s + 2]; if (ch == 4) al = cur[s + 3]; }
                else { r = g = bl = cur[s]; if (ch == 2) al = cur[s + 1]; }
                outp[o] = bl; outp[o + 1] = g; outp[o + 2] = r; outp[o + 3] = al;
            }
            (prev, cur) = (cur, prev);
        }
        return (w, h, outp);
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }
}

public static class FramePngExtensions
{
    public static void SavePng(this Frame f, string path) => Png.SaveBgra(path, f.Width, f.Height, f.Bgra);

    /// <summary>factor 分の1に縮小（面積平均）。サムネイル用（設計書 §5.4: 基準解像度の1/4）</summary>
    public static Frame Downscale(this Frame f, int factor)
    {
        if (factor <= 1) return f;
        int w = Math.Max(1, f.Width / factor), h = Math.Max(1, f.Height / factor);
        var o = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int sb = 0, sg = 0, sr = 0, n = 0;
            for (int dy = 0; dy < factor; dy++)
            {
                int yy = y * factor + dy; if (yy >= f.Height) break;
                for (int dx = 0; dx < factor; dx++)
                {
                    int xx = x * factor + dx; if (xx >= f.Width) break;
                    int i = (yy * f.Width + xx) * 4; sb += f.Bgra[i]; sg += f.Bgra[i + 1]; sr += f.Bgra[i + 2]; n++;
                }
            }
            int k = (y * w + x) * 4; o[k] = (byte)(sb / n); o[k + 1] = (byte)(sg / n); o[k + 2] = (byte)(sr / n); o[k + 3] = 255;
        }
        return new Frame(f.Id, w, h, o, f.TimestampMs, f.Source + "/thumb");
    }

    /// <summary>矩形を切り出す（範囲外は切り詰め）</summary>
    public static Frame Crop(this Frame f, int x, int y, int w, int h)
    {
        int x0 = Math.Clamp(x, 0, f.Width - 1), y0 = Math.Clamp(y, 0, f.Height - 1);
        int x1 = Math.Clamp(x + w, x0 + 1, f.Width), y1 = Math.Clamp(y + h, y0 + 1, f.Height);
        int cw = x1 - x0, chh = y1 - y0;
        var o = new byte[cw * chh * 4];
        for (int yy = 0; yy < chh; yy++) Buffer.BlockCopy(f.Bgra, ((y0 + yy) * f.Width + x0) * 4, o, yy * cw * 4, cw * 4);
        return new Frame(f.Id, cw, chh, o, f.TimestampMs, f.Source + "/crop");
    }

    public static Frame LoadPng(string path)
    {
        var (w, h, bgra) = Png.DecodeBgra(File.ReadAllBytes(path));
        return new Frame(Frame.NextId(), w, h, bgra, 0, Path.GetFileName(path));
    }
}
