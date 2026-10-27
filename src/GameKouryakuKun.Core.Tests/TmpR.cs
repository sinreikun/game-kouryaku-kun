using System.Text.Json;
using GameKouryakuKun.Core.Imaging;
using GameKouryakuKun.Core.Observation;
using Xunit;
using Xunit.Abstractions;
namespace GameKouryakuKun.Core.Tests;
public class TmpR
{
    private readonly ITestOutputHelper _o; public TmpR(ITestOutputHelper o) => _o = o;
    private static Frame Up(byte[] g, int w, int h, long id, int k = 8)
    { int W = w * k, H = h * k; var b = new byte[W * H * 4];
      for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) { int i = (y * W + x) * 4; var v = g[(y / k) * w + x / k]; b[i] = b[i + 1] = b[i + 2] = v; b[i + 3] = 255; }
      return new Frame(id, W, H, b, 0); }
    [Theory][InlineData("130")][InlineData("149")][InlineData("150")][InlineData("153")][InlineData("154")]
    public void D(string file)
    {
        var path = Path.Combine("/workspace/game-kouryaku-kun/src/GameKouryakuKun.Core.Tests/Data", $"shift-samples-{file}.json");
        foreach (var s in JsonDocument.Parse(File.ReadAllText(path)).RootElement.EnumerateArray())
        {
            int w = s.GetProperty("w").GetInt32(), h = s.GetProperty("h").GetInt32(); var line = $"R {file} {s.GetProperty("step")} {s.GetProperty("action").GetString()}";
            foreach (var wp in new[] { false, true })
            { var t = new EntityTracker { Motion = new CommonMotion { Warp = wp } };
              var a = Up(Convert.FromBase64String(s.GetProperty("prev").GetString()!), w, h, 0); var c = Up(Convert.FromBase64String(s.GetProperty("cur").GetString()!), w, h, 1);
              t.Update(FineGrid.Of(a, t.Stabilizer), a); var e = t.Update(FineGrid.Of(c, t.Stabilizer), c);
              line += $" | {t.Motion.LastReason} {(e.CommonShift == null ? "-" : string.Join(",", e.CommonShift.Select(x => Math.Round(x * 4, 1))))} [{(wp ? t.Motion.LastWarpNote : "")}]"; }
            _o.WriteLine(line);
        }
    }
}
