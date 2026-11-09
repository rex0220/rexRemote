namespace RexRemote.Protocol;

/// <summary>
/// クライアント側のフレームバッファ。形式は <see cref="PixelFormat.Bgr32"/>（1 ピクセル 4 バイト、B G R x）。
/// 受信ループのスレッドだけが書き換える。UI は受信ループが更新通知を await している間に読む。
/// 範囲外の矩形は切り詰める（例外にはしない）。
/// </summary>
public sealed class Framebuffer
{
    public const int BytesPerPixel = 4;

    public Framebuffer(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(width);
        ArgumentOutOfRangeException.ThrowIfNegative(height);
        Width = width;
        Height = height;
        Pixels = new byte[checked(width * height * BytesPerPixel)];
    }

    public int Width { get; private set; }

    public int Height { get; private set; }

    public int Stride => Width * BytesPerPixel;

    /// <summary>ピクセル配列。<see cref="Resize"/> で差し替わる。</summary>
    public byte[] Pixels { get; private set; }

    /// <summary>サイズを変える。重なる部分の内容は残し、新しい部分は黒にする。</summary>
    public void Resize(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(width);
        ArgumentOutOfRangeException.ThrowIfNegative(height);
        if (width == Width && height == Height)
        {
            return;
        }

        var pixels = new byte[checked(width * height * BytesPerPixel)];
        int copyWidth = Math.Min(width, Width) * BytesPerPixel;
        int copyHeight = Math.Min(height, Height);
        for (int row = 0; row < copyHeight; row++)
        {
            Buffer.BlockCopy(Pixels, row * Stride, pixels, row * width * BytesPerPixel, copyWidth);
        }

        Pixels = pixels;
        Width = width;
        Height = height;
    }

    /// <summary>ピクセル (x, y) の値を 0x00RRGGBB で返す（テスト・デバッグ用）。</summary>
    public uint GetPixel(int x, int y)
    {
        int i = (y * Width + x) * BytesPerPixel;
        return (uint)(Pixels[i + 2] << 16 | Pixels[i + 1] << 8 | Pixels[i]);
    }

    /// <summary>
    /// <paramref name="source"/>（1 行 <paramref name="sourceStride"/> バイト）を (x, y) に書き込む。
    /// </summary>
    public void WriteRect(int x, int y, int width, int height, ReadOnlySpan<byte> source, int sourceStride)
    {
        if (!Clip(x, y, ref width, ref height))
        {
            return;
        }

        int rowBytes = width * BytesPerPixel;
        Span<byte> pixels = Pixels;
        for (int row = 0; row < height; row++)
        {
            source.Slice(row * sourceStride, rowBytes).CopyTo(pixels.Slice((y + row) * Stride + x * BytesPerPixel, rowBytes));
        }
    }

    /// <summary>フレームバッファ内の矩形をコピーする（重なりがあっても正しく動く）。</summary>
    public void CopyRect(int sourceX, int sourceY, int x, int y, int width, int height)
    {
        // コピー元・コピー先の両方に収まる範囲に切り詰める
        width = Math.Min(width, Math.Min(Width - sourceX, Width - x));
        height = Math.Min(height, Math.Min(Height - sourceY, Height - y));
        if (width <= 0 || height <= 0 || sourceX < 0 || sourceY < 0 || x < 0 || y < 0)
        {
            return;
        }

        int rowBytes = width * BytesPerPixel;
        Span<byte> pixels = Pixels;

        // 下方向へのコピーで重なる場合は下の行から処理する。行内の重なりは Span.CopyTo が正しく扱う
        bool bottomUp = y > sourceY;
        for (int i = 0; i < height; i++)
        {
            int row = bottomUp ? height - 1 - i : i;
            pixels.Slice((sourceY + row) * Stride + sourceX * BytesPerPixel, rowBytes)
                  .CopyTo(pixels.Slice((y + row) * Stride + x * BytesPerPixel, rowBytes));
        }
    }

    /// <summary>行 <paramref name="row"/>（フレームバッファ座標）の x から width ピクセル分の領域。範囲外なら空。</summary>
    internal Memory<byte> GetRowMemory(int x, int row, int width)
    {
        if (row < 0 || row >= Height || x < 0 || x >= Width)
        {
            return Memory<byte>.Empty;
        }

        width = Math.Min(width, Width - x);
        return Pixels.AsMemory(row * Stride + x * BytesPerPixel, width * BytesPerPixel);
    }

    private bool Clip(int x, int y, ref int width, ref int height)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height || width <= 0 || height <= 0)
        {
            return false;
        }

        width = Math.Min(width, Width - x);
        height = Math.Min(height, Height - y);
        return true;
    }
}
