using System.Diagnostics;
using Xunit.Abstractions;

namespace RexRemote.Protocol.Tests;

public class FramebufferScalerTests
{
    private readonly ITestOutputHelper _output;

    public FramebufferScalerTests(ITestOutputHelper output) => _output = output;

    private static void SetPixel(Framebuffer fb, int x, int y, uint rgb)
    {
        int i = (y * fb.Width + x) * 4;
        fb.Pixels[i] = (byte)rgb;
        fb.Pixels[i + 1] = (byte)(rgb >> 8);
        fb.Pixels[i + 2] = (byte)(rgb >> 16);
    }

    private static uint GetPixel(byte[] buffer, int stride, int x, int y)
    {
        int i = y * stride + x * 4;
        return (uint)(buffer[i + 2] << 16 | buffer[i + 1] << 8 | buffer[i]);
    }

    [Theory]
    [InlineData(0x000000u, 0x000000u, 0x000000u, 0x000000u, 0x000000u)]
    [InlineData(0xFFFFFFu, 0xFFFFFFu, 0xFFFFFFu, 0xFFFFFFu, 0xFFFFFFu)]
    [InlineData(0xFF0000u, 0x00FF00u, 0x0000FFu, 0x000000u, 0x404040u)] // 255/4 = 63.75 → 64
    [InlineData(0x102030u, 0x102030u, 0x304050u, 0x304050u, 0x203040u)]
    [InlineData(0x010101u, 0x000000u, 0x000000u, 0x000000u, 0x000000u)] // 0.25 → 0
    [InlineData(0x020202u, 0x000000u, 0x000000u, 0x000000u, 0x010101u)] // 0.5 → 1（切り上げ）
    public void Average_PerChannelRounded(uint a, uint b, uint c, uint d, uint expected) =>
        Assert.Equal(expected, FramebufferScaler.Average(a, b, c, d));

    [Fact]
    public void FullImage_AveragesEach2x2Block()
    {
        var fb = new Framebuffer(4, 2);
        SetPixel(fb, 0, 0, 0xFF0000); SetPixel(fb, 1, 0, 0xFF0000);
        SetPixel(fb, 0, 1, 0x0000FF); SetPixel(fb, 1, 1, 0x0000FF);
        SetPixel(fb, 2, 0, 0x00FF00); SetPixel(fb, 3, 0, 0x00FF00);
        SetPixel(fb, 2, 1, 0x00FF00); SetPixel(fb, 3, 1, 0x00FF00);
        var half = new byte[2 * 1 * 4];

        RfbRectangle written = FramebufferScaler.DownscaleHalf(fb, new RfbRectangle(0, 0, 4, 2), half, 8);

        Assert.Equal(new RfbRectangle(0, 0, 2, 1), written);
        Assert.Equal(0x800080u, GetPixel(half, 8, 0, 0)); // 赤 2 + 青 2 → (128, 0, 128)
        Assert.Equal(0x00FF00u, GetPixel(half, 8, 1, 0));
    }

    [Fact]
    public void OddSize_LastColumnAndRowUseEdgePixels()
    {
        var fb = new Framebuffer(3, 3);
        SetPixel(fb, 2, 0, 0x646464); SetPixel(fb, 2, 1, 0x646464); // 右端の列
        SetPixel(fb, 0, 2, 0xC8C8C8); SetPixel(fb, 1, 2, 0xC8C8C8); // 下端の行
        SetPixel(fb, 2, 2, 0xFFFFFF);                              // 右下の角
        Assert.Equal(2, FramebufferScaler.HalfSize(3));
        var half = new byte[2 * 2 * 4];

        RfbRectangle written = FramebufferScaler.DownscaleHalf(fb, new RfbRectangle(0, 0, 3, 3), half, 8);

        Assert.Equal(new RfbRectangle(0, 0, 2, 2), written);
        Assert.Equal(0x646464u, GetPixel(half, 8, 1, 0)); // 右端は 1 列 x 2 行の平均（同じ値）
        Assert.Equal(0xC8C8C8u, GetPixel(half, 8, 0, 1)); // 下端は 2 列 x 1 行の平均
        Assert.Equal(0xFFFFFFu, GetPixel(half, 8, 1, 1)); // 角は 1 画素そのまま
    }

    [Fact]
    public void DirtyRect_AtOddCoordinates_UpdatesCoveringBlocksOnly()
    {
        var fb = new Framebuffer(8, 8);
        var half = new byte[4 * 4 * 4];
        Array.Fill(half, (byte)0xEE); // 書かれなかった場所が分かるように

        SetPixel(fb, 3, 3, 0xFFFFFF);
        RfbRectangle written = FramebufferScaler.DownscaleHalf(fb, new RfbRectangle(3, 3, 2, 2), half, 16);

        // (3,3)-(4,4) はブロック (1,1)-(2,2) にまたがる
        Assert.Equal(new RfbRectangle(1, 1, 2, 2), written);
        Assert.Equal(0x404040u, GetPixel(half, 16, 1, 1));        // 白 1 + 黒 3 → 64
        Assert.Equal(0x000000u, GetPixel(half, 16, 2, 2));
        Assert.Equal(0xEEEEEEu, GetPixel(half, 16, 0, 0));        // 範囲外は触らない
        Assert.Equal(0xEEEEEEu, GetPixel(half, 16, 3, 3));
    }

    [Fact]
    public void RegionOutsideFramebuffer_WritesNothing()
    {
        var fb = new Framebuffer(4, 4);
        var half = new byte[2 * 2 * 4];
        Assert.Equal(0, FramebufferScaler.DownscaleHalf(fb, new RfbRectangle(10, 10, 5, 5), half, 8).Width);
    }

    [Fact]
    public void Retina4K_FullFrameTiming()
    {
        var fb = new Framebuffer(3840, 2160);
        new Random(1).NextBytes(fb.Pixels);
        int halfWidth = FramebufferScaler.HalfSize(3840);
        var half = new byte[halfWidth * FramebufferScaler.HalfSize(2160) * 4];

        FramebufferScaler.DownscaleHalf(fb, new RfbRectangle(0, 0, 3840, 2160), half, halfWidth * 4); // ウォームアップ
        var sw = Stopwatch.StartNew();
        RfbRectangle written = FramebufferScaler.DownscaleHalf(fb, new RfbRectangle(0, 0, 3840, 2160), half, halfWidth * 4);
        _output.WriteLine($"3840x2160 → 1920x1080: {sw.Elapsed.TotalMilliseconds:F1} ms");

        Assert.Equal(new RfbRectangle(0, 0, 1920, 1080), written);
    }
}
