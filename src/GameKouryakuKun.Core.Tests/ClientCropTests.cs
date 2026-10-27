using GameKouryakuKun.Core.Imaging;
using Xunit;

namespace GameKouryakuKun.Core.Tests;

public class ClientCropTests
{
    [Fact]
    public void Wgc_frame_with_title_bar_is_cropped_to_client()
    {
        // 実機: WGC 1468x857（拡張フレーム = タイトルバー込み）、クライアント 1466x826 が (1,31) から
        var c = ClientCrop.Compute(1468, 857, 100, 50, 101, 81, 1466, 826);
        Assert.Equal((1, 31, 1466, 826), c);
    }

    [Fact]
    public void PrintWindow_frame_with_invisible_borders_is_cropped()
    {
        // GetWindowRect 1482x864（見えない枠7px込み）
        var c = ClientCrop.Compute(1482, 864, 93, 50, 101, 81, 1466, 826);
        Assert.Equal((8, 31, 1466, 826), c);
    }

    [Theory]
    [InlineData(1466, 826, 101, 81, 101, 81, 1466, 826)]   // すでにクライアントだけ（枠なし全画面など）
    [InlineData(1468, 857, 100, 50, 90, 81, 1466, 826)]    // 原点の逆転（不整合）
    [InlineData(800, 450, 100, 50, 101, 81, 1466, 826)]    // 画像が小さい（スケール違い）
    [InlineData(1468, 857, 100, 50, 101, 81, 10, 10)]      // クライアントが極小
    public void Returns_null_when_crop_is_unnecessary_or_inconsistent(int fw, int fh, int ox, int oy, int cx, int cy, int cw, int ch)
        => Assert.Null(ClientCrop.Compute(fw, fh, ox, oy, cx, cy, cw, ch));
}
