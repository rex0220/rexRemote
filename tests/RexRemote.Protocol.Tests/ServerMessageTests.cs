using System.Text;
using RexRemote.Protocol.IO;
using RexRemote.Protocol.Tests.Support;
using static RexRemote.Protocol.Tests.Support.FakeMacServer;

namespace RexRemote.Protocol.Tests;

public class ServerMessageTests
{
    private static ServerMessageReader CreateReader(byte[] data, Framebuffer framebuffer) =>
        new(new RfbReader(new MemoryStream(data), bufferSize: 64), framebuffer);

    private static byte[] RectHeader(int x, int y, int w, int h, RfbEncoding encoding) =>
        [.. U16(x), .. U16(y), .. U16(w), .. U16(h), .. S32((int)encoding)];

    private static byte[] UpdateHeader(int rectCount) => [0, 0, .. U16(rectCount)];

    private static byte[] Pixel(uint rgb) => [(byte)rgb, (byte)(rgb >> 8), (byte)(rgb >> 16), 0];

    [Fact]
    public async Task Raw_WritesPixels()
    {
        var fb = new Framebuffer(10, 10);
        byte[] data =
        [
            .. UpdateHeader(1),
            .. RectHeader(2, 3, 2, 2, RfbEncoding.Raw),
            .. Pixel(0x112233), .. Pixel(0x445566),
            .. Pixel(0x778899), .. Pixel(0xAABBCC),
        ];
        using var reader = CreateReader(data, fb);

        var update = Assert.IsType<FramebufferUpdateMessage>(await reader.ReadAsync(default));

        Assert.Equal([new RfbRectangle(2, 3, 2, 2)], update.Rectangles);
        Assert.False(update.Resized);
        Assert.Equal(0x112233u, fb.GetPixel(2, 3));
        Assert.Equal(0x445566u, fb.GetPixel(3, 3));
        Assert.Equal(0x778899u, fb.GetPixel(2, 4));
        Assert.Equal(0xAABBCCu, fb.GetPixel(3, 4));
        Assert.Equal(0u, fb.GetPixel(1, 3));
    }

    [Fact]
    public async Task Raw_OutOfBoundsRectIsClippedAndStreamStaysAligned()
    {
        var fb = new Framebuffer(4, 4);
        byte[] data =
        [
            .. UpdateHeader(1),
            .. RectHeader(3, 3, 2, 2, RfbEncoding.Raw), // 右下にはみ出す
            .. Pixel(0x010101), .. Pixel(0x020202),
            .. Pixel(0x030303), .. Pixel(0x040404),
            ServerMessageReader.BellType,
        ];
        using var reader = CreateReader(data, fb);

        await reader.ReadAsync(default);
        Assert.Equal(0x010101u, fb.GetPixel(3, 3));
        Assert.IsType<BellMessage>(await reader.ReadAsync(default));
    }

    [Fact]
    public async Task CopyRect_HandlesOverlap()
    {
        var fb = new Framebuffer(5, 5);
        for (int y = 0; y < 5; y++)
        {
            for (int x = 0; x < 5; x++)
            {
                Pixel((uint)(y * 16 + x)).CopyTo(fb.Pixels, (y * 5 + x) * 4);
            }
        }

        uint[,] before = Snapshot(fb);
        byte[] data = [.. UpdateHeader(1), .. RectHeader(1, 1, 3, 3, RfbEncoding.CopyRect), .. U16(0), .. U16(0)];
        using var reader = CreateReader(data, fb);

        await reader.ReadAsync(default);

        for (int y = 0; y < 3; y++)
        {
            for (int x = 0; x < 3; x++)
            {
                Assert.Equal(before[x, y], fb.GetPixel(x + 1, y + 1));
            }
        }

        Assert.Equal(before[0, 0], fb.GetPixel(0, 0));
        Assert.Equal(before[4, 4], fb.GetPixel(4, 4));
    }

    [Fact]
    public async Task CopyRect_UpwardOverlap()
    {
        var fb = new Framebuffer(3, 4);
        for (int i = 0; i < 12; i++)
        {
            Pixel((uint)i + 1).CopyTo(fb.Pixels, i * 4);
        }

        uint[,] before = Snapshot(fb);
        byte[] data = [.. UpdateHeader(1), .. RectHeader(0, 0, 3, 3, RfbEncoding.CopyRect), .. U16(0), .. U16(1)];
        using var reader = CreateReader(data, fb);

        await reader.ReadAsync(default);

        for (int y = 0; y < 3; y++)
        {
            for (int x = 0; x < 3; x++)
            {
                Assert.Equal(before[x, y + 1], fb.GetPixel(x, y));
            }
        }
    }

    [Fact]
    public async Task DesktopSize_ResizesFramebuffer()
    {
        var fb = new Framebuffer(4, 4);
        Pixel(0xABCDEF).CopyTo(fb.Pixels, 0);
        byte[] data = [.. UpdateHeader(1), .. RectHeader(0, 0, 8, 6, RfbEncoding.DesktopSize)];
        using var reader = CreateReader(data, fb);

        var update = Assert.IsType<FramebufferUpdateMessage>(await reader.ReadAsync(default));

        Assert.True(update.Resized);
        Assert.Empty(update.Rectangles);
        Assert.Equal(8, fb.Width);
        Assert.Equal(6, fb.Height);
        Assert.Equal(8 * 6 * 4, fb.Pixels.Length);
        Assert.Equal(0xABCDEFu, fb.GetPixel(0, 0)); // 重なる部分は残る
    }

    [Fact]
    public async Task OtherMessages_AreParsed()
    {
        byte[] text = Encoding.Latin1.GetBytes("héllo");
        byte[] data =
        [
            ServerMessageReader.SetColourMapEntriesType, 0, .. U16(0), .. U16(2), .. new byte[12],
            ServerMessageReader.BellType,
            ServerMessageReader.ServerCutTextType, 0, 0, 0, .. U32((uint)text.Length), .. text,
        ];
        using var reader = CreateReader(data, new Framebuffer(1, 1));

        Assert.Equal(new SetColourMapEntriesMessage(0, 2), await reader.ReadAsync(default));
        Assert.IsType<BellMessage>(await reader.ReadAsync(default));
        Assert.Equal(new ServerCutTextMessage("héllo"), await reader.ReadAsync(default));
    }

    [Theory]
    [InlineData(new byte[] { 0x61, 0x62 }, "ab")]                               // ASCII
    [InlineData(new byte[] { 0xE3, 0x81, 0x82, 0x0A, 0x41 }, "あ\nA")]          // 正しい UTF-8 → UTF-8
    [InlineData(new byte[] { 0x68, 0xE9, 0x6C }, "hél")]                        // UTF-8 として不正 → Latin-1
    public void DecodeCutText_PrefersValidUtf8(byte[] bytes, string expected) =>
        Assert.Equal(expected, ServerMessageReader.DecodeCutText(bytes));

    [Fact]
    public async Task Cursor_PseudoRect_ParsesShapeAndMask()
    {
        // 3x2 のカーソル、ホットスポット (1,0)。マスク: 1 行目 101、2 行目 010
        byte[] pixels =
        [
            .. Pixel(0xFF0000), .. Pixel(0x00FF00), .. Pixel(0x0000FF),
            .. Pixel(0x111111), .. Pixel(0x222222), .. Pixel(0x333333),
        ];
        byte[] mask = [0b1010_0000, 0b0100_0000];
        byte[] data =
        [
            .. UpdateHeader(2),
            .. RectHeader(1, 0, 3, 2, RfbEncoding.Cursor), .. pixels, .. mask,
            .. RectHeader(0, 0, 1, 1, RfbEncoding.Raw), .. Pixel(0xABCDEF),
        ];
        var fb = new Framebuffer(4, 4);
        using var reader = CreateReader(data, fb);

        var update = Assert.IsType<FramebufferUpdateMessage>(await reader.ReadAsync(default));

        // カーソルは描画矩形には含めず、フレームバッファにも描かない
        Assert.Equal([new RfbRectangle(0, 0, 1, 1)], update.Rectangles);
        Assert.Equal(0xABCDEFu, fb.GetPixel(0, 0));
        CursorShape cursor = Assert.IsType<CursorShape>(update.Cursor);
        Assert.Equal((1, 0, 3, 2), (cursor.HotspotX, cursor.HotspotY, cursor.Width, cursor.Height));
        Assert.False(cursor.IsHidden);
        byte[] alpha = Enumerable.Range(0, 6).Select(i => cursor.Bgra[i * 4 + 3]).ToArray();
        Assert.Equal(new byte[] { 255, 0, 255, 0, 255, 0 }, alpha);
        Assert.Equal(new byte[] { 0x00, 0x00, 0xFF }, cursor.Bgra[0..3]); // 赤（B, G, R）
    }

    [Fact]
    public async Task Cursor_EmptyMeansHidden()
    {
        byte[] data = [.. UpdateHeader(1), .. RectHeader(0, 0, 0, 0, RfbEncoding.Cursor)];
        using var reader = CreateReader(data, new Framebuffer(4, 4));

        var update = Assert.IsType<FramebufferUpdateMessage>(await reader.ReadAsync(default));

        Assert.True(update.Cursor!.IsHidden);
        Assert.Empty(update.Rectangles);
    }

    [Fact]
    public async Task UnknownMessageType_Throws()
    {
        using var reader = CreateReader([99], new Framebuffer(1, 1));
        await Assert.ThrowsAsync<RfbProtocolException>(() => reader.ReadAsync(default).AsTask());
    }

    [Fact]
    public async Task UnrequestedEncoding_Throws()
    {
        byte[] data = [.. UpdateHeader(1), .. RectHeader(0, 0, 1, 1, (RfbEncoding)7)];
        using var reader = CreateReader(data, new Framebuffer(1, 1));
        await Assert.ThrowsAsync<RfbProtocolException>(() => reader.ReadAsync(default).AsTask());
    }

    // ------------------------------------------------------------------
    // ZRLE
    // ------------------------------------------------------------------

    [Fact]
    public async Task Zrle_EachSubencoding_SingleTile()
    {
        const int w = 7, h = 5;
        using var compressor = new ZrleCompressor();
        var random = new Random(1);
        var fb = new Framebuffer(w, h * 7);
        var expected = new uint[w, h * 7];
        var stream = new List<byte>(UpdateHeader(7));

        // 7 種類のサブエンコーディングを縦に並べた 7 矩形（各 1 タイル）
        for (int kind = 0; kind < 7; kind++)
        {
            var tile = new ZrleTileBuilder();
            uint[] pixels = TileGenerator.Generate(tile, kind, w, h, random);
            for (int i = 0; i < pixels.Length; i++)
            {
                expected[i % w, kind * h + i / w] = pixels[i];
            }

            byte[] chunk = compressor.CompressRect(tile.ToArray());
            stream.AddRange(RectHeader(0, kind * h, w, h, RfbEncoding.Zrle));
            stream.AddRange(U32((uint)chunk.Length));
            stream.AddRange(chunk);
        }

        using var reader = CreateReader([.. stream], fb);
        var update = Assert.IsType<FramebufferUpdateMessage>(await reader.ReadAsync(default));

        Assert.Equal(7, update.Rectangles.Count);
        AssertFramebuffer(expected, fb);
    }

    [Fact]
    public async Task Zrle_MultiTileRects_AcrossMessages_SharedZlibStream()
    {
        // 64x64 タイル境界をまたぐ矩形を、複数の FramebufferUpdate に分けて送る（zlib ストリームは連続）
        var fb = new Framebuffer(300, 200);
        var expected = new uint[300, 200];
        var random = new Random(42);
        using var compressor = new ZrleCompressor();
        var stream = new List<byte>();

        RfbRectangle[] rects = [new(5, 7, 150, 100), new(130, 60, 170, 140), new(0, 0, 64, 64), new(299, 199, 1, 1)];
        int kind = 0;
        foreach (var rect in rects)
        {
            var tile = new ZrleTileBuilder();
            for (int ty = 0; ty < rect.Height; ty += 64)
            {
                int th = Math.Min(64, rect.Height - ty);
                for (int tx = 0; tx < rect.Width; tx += 64)
                {
                    int tw = Math.Min(64, rect.Width - tx);
                    uint[] pixels = TileGenerator.Generate(tile, kind++ % 7, tw, th, random);
                    for (int i = 0; i < pixels.Length; i++)
                    {
                        expected[rect.X + tx + i % tw, rect.Y + ty + i / tw] = pixels[i];
                    }
                }
            }

            byte[] chunk = compressor.CompressRect(tile.ToArray());
            stream.AddRange(UpdateHeader(1));
            stream.AddRange(RectHeader(rect.X, rect.Y, rect.Width, rect.Height, RfbEncoding.Zrle));
            stream.AddRange(U32((uint)chunk.Length));
            stream.AddRange(chunk);
        }

        using var reader = CreateReader([.. stream], fb);
        foreach (var rect in rects)
        {
            var update = Assert.IsType<FramebufferUpdateMessage>(await reader.ReadAsync(default));
            Assert.Equal([rect], update.Rectangles);
        }

        AssertFramebuffer(expected, fb);
    }

    [Fact]
    public async Task Zrle_LongRunLengths()
    {
        // 1 タイル 64x64 = 4096 ピクセルを長いラン（複数バイトのラン長）で埋める
        var tile = new ZrleTileBuilder();
        tile.PlainRle([(0x102030, 255), (0x405060, 256), (0x708090, 510), (0xA0B0C0, 511), (0x0F0F0F, 4096 - 255 - 256 - 510 - 511)]);
        using var compressor = new ZrleCompressor();
        byte[] chunk = compressor.CompressRect(tile.ToArray());
        var fb = new Framebuffer(64, 64);
        byte[] data = [.. UpdateHeader(1), .. RectHeader(0, 0, 64, 64, RfbEncoding.Zrle), .. U32((uint)chunk.Length), .. chunk];
        using var reader = CreateReader(data, fb);

        await reader.ReadAsync(default);

        Assert.Equal(0x102030u, fb.GetPixel(0, 0));
        Assert.Equal(0x102030u, fb.GetPixel(254 % 64, 254 / 64));
        Assert.Equal(0x405060u, fb.GetPixel(255 % 64, 255 / 64));
        Assert.Equal(0x405060u, fb.GetPixel(510 % 64, 510 / 64));
        Assert.Equal(0x708090u, fb.GetPixel(511 % 64, 511 / 64));
        Assert.Equal(0xA0B0C0u, fb.GetPixel(1021 % 64, 1021 / 64));
        Assert.Equal(0x0F0F0Fu, fb.GetPixel(63, 63));
    }

    [Fact]
    public async Task Zrle_InvalidSubencoding_Throws()
    {
        var tile = new ZrleTileBuilder();
        tile.RawByte(17);
        using var compressor = new ZrleCompressor();
        byte[] chunk = compressor.CompressRect(tile.ToArray());
        byte[] data = [.. UpdateHeader(1), .. RectHeader(0, 0, 4, 4, RfbEncoding.Zrle), .. U32((uint)chunk.Length), .. chunk];
        using var reader = CreateReader(data, new Framebuffer(4, 4));

        await Assert.ThrowsAsync<RfbProtocolException>(() => reader.ReadAsync(default).AsTask());
    }

    [Fact]
    public async Task Zrle_TruncatedData_Throws()
    {
        var tile = new ZrleTileBuilder();
        tile.Raw([0x111111, 0x222222]); // 4x4 = 16 ピクセル必要なのに 2 ピクセルしかない
        using var compressor = new ZrleCompressor();
        byte[] chunk = compressor.CompressRect(tile.ToArray());
        byte[] data = [.. UpdateHeader(1), .. RectHeader(0, 0, 4, 4, RfbEncoding.Zrle), .. U32((uint)chunk.Length), .. chunk];
        using var reader = CreateReader(data, new Framebuffer(4, 4));

        await Assert.ThrowsAnyAsync<Exception>(() => reader.ReadAsync(default).AsTask());
    }

    private static uint[,] Snapshot(Framebuffer fb)
    {
        var result = new uint[fb.Width, fb.Height];
        for (int y = 0; y < fb.Height; y++)
        {
            for (int x = 0; x < fb.Width; x++)
            {
                result[x, y] = fb.GetPixel(x, y);
            }
        }

        return result;
    }

    private static void AssertFramebuffer(uint[,] expected, Framebuffer fb)
    {
        for (int y = 0; y < fb.Height; y++)
        {
            for (int x = 0; x < fb.Width; x++)
            {
                if (expected[x, y] != fb.GetPixel(x, y))
                {
                    Assert.Fail($"pixel ({x},{y}): expected {expected[x, y]:X6}, actual {fb.GetPixel(x, y):X6}");
                }
            }
        }
    }
}

/// <summary>サブエンコーディングごとにランダムなタイルを作り、期待するピクセル列を返す。</summary>
internal static class TileGenerator
{
    public static uint[] Generate(ZrleTileBuilder tile, int kind, int tw, int th, Random random)
    {
        int count = tw * th;
        var pixels = new uint[count];
        switch (kind)
        {
            case 0: // raw
                for (int i = 0; i < count; i++)
                {
                    pixels[i] = RandomColor(random);
                }

                tile.Raw(pixels);
                break;

            case 1: // solid
                Array.Fill(pixels, RandomColor(random));
                tile.Solid(pixels[0]);
                break;

            case 2 or 3 or 4: // packed palette 2 色（1bit）/ 4 色（2bit）/ 16 色（4bit）
            {
                int size = kind == 2 ? 2 : kind == 3 ? 3 : 16;
                uint[] palette = Enumerable.Range(0, size).Select(_ => RandomColor(random)).ToArray();
                int[] indices = Enumerable.Range(0, count).Select(_ => random.Next(size)).ToArray();
                for (int i = 0; i < count; i++)
                {
                    pixels[i] = palette[indices[i]];
                }

                tile.PackedPalette(palette, indices, tw, th);
                break;
            }

            case 5: // plain RLE
            {
                var runs = new List<(uint, int)>();
                for (int i = 0; i < count;)
                {
                    int length = Math.Min(count - i, random.Next(1, 600));
                    uint color = RandomColor(random);
                    runs.Add((color, length));
                    pixels.AsSpan(i, length).Fill(color);
                    i += length;
                }

                tile.PlainRle(runs);
                break;
            }

            case 6: // palette RLE（127 色まで）
            {
                int size = random.Next(2, 128);
                uint[] palette = Enumerable.Range(0, size).Select(_ => RandomColor(random)).ToArray();
                var runs = new List<(int, int)>();
                for (int i = 0; i < count;)
                {
                    int length = Math.Min(count - i, random.Next(3) == 0 ? 1 : random.Next(1, 400));
                    int index = random.Next(size);
                    runs.Add((index, length));
                    pixels.AsSpan(i, length).Fill(palette[index]);
                    i += length;
                }

                tile.PaletteRle(palette, runs);
                break;
            }
        }

        return pixels;
    }

    private static uint RandomColor(Random random) => (uint)random.Next(0, 1 << 24);
}
