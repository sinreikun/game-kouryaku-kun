using System.Buffers.Binary;
using System.IO.Compression;

namespace GameKouryakuKun.Phase0;

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
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var fs = File.Create(path);
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
    }
}
