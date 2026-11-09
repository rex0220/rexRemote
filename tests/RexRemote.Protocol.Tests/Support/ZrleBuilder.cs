using System.IO.Compression;

namespace RexRemote.Protocol.Tests.Support;

/// <summary>ZRLE のタイルデータ（展開後）を組み立てる。色は 0x00RRGGBB。</summary>
internal sealed class ZrleTileBuilder
{
    private readonly List<byte> _bytes = [];

    public byte[] ToArray() => [.. _bytes];

    public void Raw(IReadOnlyList<uint> pixels)
    {
        _bytes.Add(0);
        foreach (uint p in pixels)
        {
            CPixel(p);
        }
    }

    public void Solid(uint color)
    {
        _bytes.Add(1);
        CPixel(color);
    }

    public void PackedPalette(IReadOnlyList<uint> palette, IReadOnlyList<int> indices, int tw, int th)
    {
        int n = palette.Count;
        if (n is < 2 or > 16)
        {
            throw new ArgumentOutOfRangeException(nameof(palette));
        }

        _bytes.Add((byte)n);
        foreach (uint c in palette)
        {
            CPixel(c);
        }

        int bits = n == 2 ? 1 : n <= 4 ? 2 : 4;
        for (int row = 0; row < th; row++)
        {
            int acc = 0;
            int used = 0;
            for (int col = 0; col < tw; col++)
            {
                acc = (acc << bits) | indices[row * tw + col];
                used += bits;
                if (used == 8)
                {
                    _bytes.Add((byte)acc);
                    acc = 0;
                    used = 0;
                }
            }

            if (used > 0)
            {
                _bytes.Add((byte)(acc << (8 - used))); // 行末はバイト境界まで詰める
            }
        }
    }

    public void PlainRle(IEnumerable<(uint Color, int Length)> runs)
    {
        _bytes.Add(128);
        foreach (var (color, length) in runs)
        {
            CPixel(color);
            RunLength(length);
        }
    }

    public void PaletteRle(IReadOnlyList<uint> palette, IEnumerable<(int Index, int Length)> runs)
    {
        _bytes.Add((byte)(128 + palette.Count));
        foreach (uint c in palette)
        {
            CPixel(c);
        }

        foreach (var (index, length) in runs)
        {
            if (length == 1)
            {
                _bytes.Add((byte)index);
            }
            else
            {
                _bytes.Add((byte)(index | 0x80));
                RunLength(length);
            }
        }
    }

    public void RawByte(byte b) => _bytes.Add(b);

    private void CPixel(uint rgb)
    {
        _bytes.Add((byte)rgb);          // B
        _bytes.Add((byte)(rgb >> 8));   // G
        _bytes.Add((byte)(rgb >> 16));  // R
    }

    private void RunLength(int length)
    {
        int rest = length - 1;
        while (rest >= 255)
        {
            _bytes.Add(255);
            rest -= 255;
        }

        _bytes.Add((byte)rest);
    }
}

/// <summary>
/// 接続全体で 1 本の zlib ストリームを模す。矩形ごとに sync flush し、その矩形分の圧縮データを返す。
/// </summary>
internal sealed class ZrleCompressor : IDisposable
{
    private readonly MemoryStream _compressed = new();
    private readonly ZLibStream _zlib;
    private int _taken;

    public ZrleCompressor() => _zlib = new ZLibStream(_compressed, CompressionLevel.Optimal, leaveOpen: true);

    public byte[] CompressRect(byte[] tileData)
    {
        _zlib.Write(tileData);
        _zlib.Flush(); // Z_SYNC_FLUSH
        byte[] all = _compressed.ToArray();
        byte[] chunk = all[_taken..];
        _taken = all.Length;
        return chunk;
    }

    public void Dispose() => _zlib.Dispose();
}
