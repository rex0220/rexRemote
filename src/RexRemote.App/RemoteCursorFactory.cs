using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32.SafeHandles;
using RexRemote.Protocol;

namespace RexRemote.App;

/// <summary>
/// Mac から受け取ったカーソル形状を、表示倍率に合わせて縮小した Windows のカーソルにする。
/// Mac のカーソルは Retina のピクセル単位（2 倍の大きさ）で届くので、画面と同じ倍率で縮めると Mac の見た目どおりの大きさになる。
/// </summary>
internal static class RemoteCursorFactory
{
    /// <param name="scale">リモート 1 ピクセルあたりのデバイスピクセル数（例: ウィンドウに合わせる ≒ 0.59、50% = 0.5）。</param>
    /// <returns>作ったカーソル（呼び出し側で Dispose する）。隠すときは <see cref="Cursors.None"/>（共有なので Dispose しない）。</returns>
    public static Cursor Create(CursorShape shape, double scale)
    {
        if (shape.IsHidden)
        {
            return Cursors.None;
        }

        int width = shape.Width;
        int height = shape.Height;
        byte[] pixels = shape.Bgra;

        if (Math.Abs(scale - 1.0) > 0.01)
        {
            var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, shape.Bgra, width * 4);
            var scaled = new FormatConvertedBitmap(
                new TransformedBitmap(source, new ScaleTransform(scale, scale)), PixelFormats.Bgra32, null, 0);
            width = Math.Max(scaled.PixelWidth, 1);
            height = Math.Max(scaled.PixelHeight, 1);
            pixels = new byte[width * height * 4];
            scaled.CopyPixels(pixels, width * 4, 0);

            // 縮小で半透明になった縁は、くっきり見えるよう透明か不透明に寄せる
            for (int i = 3; i < pixels.Length; i += 4)
            {
                pixels[i] = pixels[i] >= 128 ? (byte)255 : (byte)0;
            }
        }

        int hotspotX = Math.Clamp((int)Math.Round(shape.HotspotX * (double)width / shape.Width), 0, width - 1);
        int hotspotY = Math.Clamp((int)Math.Round(shape.HotspotY * (double)height / shape.Height), 0, height - 1);
        return CreateFromBgra(width, height, pixels, hotspotX, hotspotY);
    }

    private static Cursor CreateFromBgra(int width, int height, byte[] bgra, int hotspotX, int hotspotY)
    {
        // カラー: 32bpp のトップダウン DIB
        var header = new BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = width,
            biHeight = -height,
            biPlanes = 1,
            biBitCount = 32,
        };
        IntPtr color = CreateDIBSection(IntPtr.Zero, ref header, 0, out IntPtr bits, IntPtr.Zero, 0);

        // マスク: 1bpp、各行は 2 バイト境界。1 = 透明
        int maskStride = (width + 15) / 16 * 2;
        var maskBits = new byte[maskStride * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (bgra[(y * width + x) * 4 + 3] == 0)
                {
                    maskBits[y * maskStride + (x >> 3)] |= (byte)(0x80 >> (x & 7));
                }
            }
        }

        IntPtr mask = CreateBitmap(width, height, 1, 1, maskBits);
        try
        {
            if (color == IntPtr.Zero || mask == IntPtr.Zero)
            {
                return Cursors.Arrow;
            }

            Marshal.Copy(bgra, 0, bits, bgra.Length);
            var info = new ICONINFO { fIcon = false, xHotspot = hotspotX, yHotspot = hotspotY, hbmMask = mask, hbmColor = color };
            IntPtr handle = CreateIconIndirect(ref info);
            return handle == IntPtr.Zero ? Cursors.Arrow : CursorInteropHelper.Create(new SafeIconHandle(handle));
        }
        finally
        {
            // CreateIconIndirect はビットマップを複製するので元は消してよい
            if (color != IntPtr.Zero)
            {
                DeleteObject(color);
            }

            if (mask != IntPtr.Zero)
            {
                DeleteObject(mask);
            }
        }
    }

    private sealed class SafeIconHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeIconHandle(IntPtr handle) : base(ownsHandle: true) => SetHandle(handle);

        protected override bool ReleaseHandle() => DestroyIcon(handle);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        [MarshalAs(UnmanagedType.Bool)] public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreateIconIndirect(ref ICONINFO info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER header, uint usage, out IntPtr bits, IntPtr section, uint offset);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateBitmap(int width, int height, uint planes, uint bitCount, byte[] bits);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr handle);
}
