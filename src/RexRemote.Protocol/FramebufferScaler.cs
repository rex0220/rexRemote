using System.Runtime.InteropServices;

namespace RexRemote.Protocol;

/// <summary>
/// フレームバッファの 1/2 縮小（2x2 ピクセルの平均）。Retina の Mac（例: 3840x2160）を
/// 「見た目の解像度」（1920x1080）でくっきり表示するために使う。整数倍の縮小なので文字がにじまない。
/// 更新された矩形の分だけ縮小できる。
/// </summary>
public static class FramebufferScaler
{
    /// <summary>1/2 縮小後の長さ（奇数は切り上げ。端の 1 列・1 行はその画素だけで作る）。</summary>
    public static int HalfSize(int length) => (length + 1) / 2;

    /// <summary>
    /// <paramref name="source"/> の <paramref name="region"/> を含む範囲を 1/2 に縮小して
    /// <paramref name="destination"/>（幅 HalfSize(Width)、1 行 <paramref name="destinationStride"/> バイト、Bgr32）に書く。
    /// </summary>
    /// <returns>書き込んだ縮小後の矩形。範囲外なら幅 0。</returns>
    public static RfbRectangle DownscaleHalf(Framebuffer source, RfbRectangle region, Span<byte> destination, int destinationStride)
    {
        int width = source.Width;
        int height = source.Height;
        int x0 = Math.Clamp(region.X, 0, width);
        int y0 = Math.Clamp(region.Y, 0, height);
        int x1 = Math.Clamp(region.X + region.Width, 0, width);
        int y1 = Math.Clamp(region.Y + region.Height, 0, height);
        if (x1 <= x0 || y1 <= y0)
        {
            return default;
        }

        // 2x2 ブロック単位に広げる
        int dx0 = x0 / 2;
        int dy0 = y0 / 2;
        int dx1 = (x1 + 1) / 2;
        int dy1 = (y1 + 1) / 2;
        int lastX = width - 1;
        int lastY = height - 1;
        int halfWidth = HalfSize(width);

        ReadOnlySpan<uint> pixels = MemoryMarshal.Cast<byte, uint>(source.Pixels.AsSpan());
        for (int dy = dy0; dy < dy1; dy++)
        {
            int sy0 = dy * 2;
            int sy1 = Math.Min(sy0 + 1, lastY);
            ReadOnlySpan<uint> row0 = pixels.Slice(sy0 * width, width);
            ReadOnlySpan<uint> row1 = pixels.Slice(sy1 * width, width);
            Span<uint> output = MemoryMarshal.Cast<byte, uint>(destination.Slice(dy * destinationStride, halfWidth * Framebuffer.BytesPerPixel));

            for (int dx = dx0; dx < dx1; dx++)
            {
                int sx0 = dx * 2;
                int sx1 = Math.Min(sx0 + 1, lastX);
                output[dx] = Average(row0[sx0], row0[sx1], row1[sx0], row1[sx1]);
            }
        }

        return new RfbRectangle(dx0, dy0, dx1 - dx0, dy1 - dy0);
    }

    /// <summary>
    /// B G R x の 4 画素を色ごとに平均する（四捨五入）。R と B は 1 つの uint の別レーンでまとめて足す
    /// （4 画素の合計は 10bit に収まるので隣のレーンにあふれない）。
    /// </summary>
    internal static uint Average(uint a, uint b, uint c, uint d)
    {
        uint rb = ((a & 0xFF00FF) + (b & 0xFF00FF) + (c & 0xFF00FF) + (d & 0xFF00FF) + 0x020002) >> 2;
        uint g = ((a & 0x00FF00) + (b & 0x00FF00) + (c & 0x00FF00) + (d & 0x00FF00) + 0x000200) >> 2;
        return (rb & 0xFF00FF) | (g & 0x00FF00);
    }
}
