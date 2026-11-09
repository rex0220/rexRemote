using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace RexRemote.Protocol.Decoding;

/// <summary>
/// ZRLE（エンコーディング 16）のデコーダー。接続ごとに 1 つ作り、全 ZRLE 矩形で使い回す
/// （zlib ストリームが接続全体で連続しているため）。
/// ピクセル形式は <see cref="PixelFormat.Bgr32"/> 固定を前提とし、CPIXEL は 3 バイト（B, G, R）。
/// </summary>
internal sealed class ZrleDecoder : IDisposable
{
    private const int TileSize = 64;
    private const int CPixelSize = 3;

    private readonly InputQueueStream _input = new();
    private readonly ZLibStream _zlib;

    // zlib の展開結果のバッファ
    private readonly byte[] _out = new byte[64 * 1024];
    private int _outPos;
    private int _outLen;

    private readonly byte[] _tile = new byte[TileSize * TileSize * Framebuffer.BytesPerPixel];
    private readonly uint[] _palette = new uint[128];

    public ZrleDecoder()
    {
        Debug.Assert(BitConverter.IsLittleEndian, "Bgr32 のピクセルを uint で書き込むため little-endian を前提とする");
        _zlib = new ZLibStream(_input, CompressionMode.Decompress, leaveOpen: true);
    }

    /// <summary>1 矩形分の圧縮データを展開し、フレームバッファに描く。</summary>
    public void Decode(ReadOnlySpan<byte> compressed, int x, int y, int width, int height, Framebuffer framebuffer)
    {
        _input.Append(compressed);

        for (int ty = 0; ty < height; ty += TileSize)
        {
            int th = Math.Min(TileSize, height - ty);
            for (int tx = 0; tx < width; tx += TileSize)
            {
                int tw = Math.Min(TileSize, width - tx);
                DecodeTile(tw, th);
                framebuffer.WriteRect(x + tx, y + ty, tw, th, _tile, tw * Framebuffer.BytesPerPixel);
            }
        }
    }

    private void DecodeTile(int tw, int th)
    {
        int count = tw * th;
        Span<uint> pixels = MemoryMarshal.Cast<byte, uint>(_tile.AsSpan(0, count * Framebuffer.BytesPerPixel));
        int sub = ReadByte();

        switch (sub)
        {
            case 0: // Raw: CPIXEL × count
                Ensure(count * CPixelSize);
                for (int i = 0; i < count; i++)
                {
                    pixels[i] = TakeCPixel();
                }

                break;

            case 1: // 単色
                pixels.Fill(ReadCPixel());
                break;

            case >= 2 and <= 16: // パック済みパレット
                DecodePackedPalette(pixels, tw, th, paletteSize: sub);
                break;

            case 128: // プレーン RLE
                DecodePlainRle(pixels);
                break;

            case >= 130: // パレット RLE
                DecodePaletteRle(pixels, paletteSize: sub - 128);
                break;

            default:
                throw new RfbProtocolException($"不正な ZRLE サブエンコーディングです: {sub}");
        }
    }

    private void DecodePackedPalette(Span<uint> pixels, int tw, int th, int paletteSize)
    {
        ReadPalette(paletteSize);
        int bits = paletteSize == 2 ? 1 : paletteSize <= 4 ? 2 : 4;
        int mask = (1 << bits) - 1;
        int rowBytes = (tw * bits + 7) / 8; // 各行はバイト境界に揃える

        Ensure(rowBytes * th);
        for (int row = 0; row < th; row++)
        {
            int rowStart = _outPos + row * rowBytes;
            for (int col = 0; col < tw; col++)
            {
                int bitOffset = col * bits;
                int shift = 8 - bits - (bitOffset & 7); // 上位ビットが先
                int index = (_out[rowStart + (bitOffset >> 3)] >> shift) & mask;
                if (index >= paletteSize)
                {
                    throw new RfbProtocolException($"ZRLE パレットの範囲外のインデックスです: {index}（パレット {paletteSize} 色）");
                }

                pixels[row * tw + col] = _palette[index];
            }
        }

        _outPos += rowBytes * th;
    }

    private void DecodePlainRle(Span<uint> pixels)
    {
        int i = 0;
        while (i < pixels.Length)
        {
            uint color = ReadCPixel();
            int length = ReadRunLength();
            if (length > pixels.Length - i)
            {
                throw new RfbProtocolException("ZRLE のランがタイルをはみ出しています。");
            }

            pixels.Slice(i, length).Fill(color);
            i += length;
        }
    }

    private void DecodePaletteRle(Span<uint> pixels, int paletteSize)
    {
        ReadPalette(paletteSize);
        int i = 0;
        while (i < pixels.Length)
        {
            int index = ReadByte();
            int length = 1;
            if ((index & 0x80) != 0)
            {
                index &= 0x7F;
                length = ReadRunLength();
            }

            if (index >= paletteSize)
            {
                throw new RfbProtocolException($"ZRLE パレットの範囲外のインデックスです: {index}（パレット {paletteSize} 色）");
            }

            if (length > pixels.Length - i)
            {
                throw new RfbProtocolException("ZRLE のランがタイルをはみ出しています。");
            }

            pixels.Slice(i, length).Fill(_palette[index]);
            i += length;
        }
    }

    private void ReadPalette(int paletteSize)
    {
        Ensure(paletteSize * CPixelSize);
        for (int i = 0; i < paletteSize; i++)
        {
            _palette[i] = TakeCPixel();
        }
    }

    /// <summary>ラン長：各バイトの合計 + 1。最後以外のバイトは 255。</summary>
    private int ReadRunLength()
    {
        int length = 1;
        int b;
        do
        {
            b = ReadByte();
            length += b;
            if (length > TileSize * TileSize)
            {
                throw new RfbProtocolException("ZRLE のラン長が大きすぎます。");
            }
        }
        while (b == 255);

        return length;
    }

    private int ReadByte()
    {
        Ensure(1);
        return _out[_outPos++];
    }

    private uint ReadCPixel()
    {
        Ensure(CPixelSize);
        return TakeCPixel();
    }

    /// <summary>CPIXEL（B, G, R の 3 バイト）を Bgr32 の uint（little-endian で B G R 0）にする。</summary>
    private uint TakeCPixel()
    {
        uint v = (uint)(_out[_outPos] | _out[_outPos + 1] << 8 | _out[_outPos + 2] << 16);
        _outPos += CPixelSize;
        return v;
    }

    /// <summary>展開済みバッファに少なくとも <paramref name="count"/> バイトある状態にする。</summary>
    private void Ensure(int count)
    {
        int buffered = _outLen - _outPos;
        if (buffered >= count)
        {
            return;
        }

        if (_outPos > 0)
        {
            Buffer.BlockCopy(_out, _outPos, _out, 0, buffered);
            _outPos = 0;
            _outLen = buffered;
        }

        while (_outLen < count)
        {
            int n = _zlib.Read(_out, _outLen, _out.Length - _outLen);
            if (n <= 0)
            {
                throw new RfbProtocolException("ZRLE のデータが途中で終わっています。");
            }

            _outLen += n;
        }
    }

    public void Dispose()
    {
        _zlib.Dispose();
        _input.Dispose();
    }
}
