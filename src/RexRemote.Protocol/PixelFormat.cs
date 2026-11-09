using System.Buffers.Binary;

namespace RexRemote.Protocol;

/// <summary>RFB の PIXEL_FORMAT（16 バイト）。</summary>
public readonly record struct PixelFormat(
    byte BitsPerPixel,
    byte Depth,
    bool BigEndian,
    bool TrueColor,
    ushort RedMax,
    ushort GreenMax,
    ushort BlueMax,
    byte RedShift,
    byte GreenShift,
    byte BlueShift)
{
    public const int Size = 16;

    /// <summary>
    /// クライアントが要求する固定形式。32bpp / depth 24 / little-endian / true color / R=16 G=8 B=0。
    /// メモリ上のバイト順は B, G, R, (未使用) となり、WPF の Bgr32 にそのままコピーできる。
    /// </summary>
    public static PixelFormat Bgr32 { get; } = new(32, 24, false, true, 255, 255, 255, 16, 8, 0);

    public int BytesPerPixel => BitsPerPixel / 8;

    public static PixelFormat Parse(ReadOnlySpan<byte> source)
    {
        if (source.Length < Size)
        {
            throw new ArgumentException($"PIXEL_FORMAT は {Size} バイト必要です。", nameof(source));
        }

        return new PixelFormat(
            BitsPerPixel: source[0],
            Depth: source[1],
            BigEndian: source[2] != 0,
            TrueColor: source[3] != 0,
            RedMax: BinaryPrimitives.ReadUInt16BigEndian(source[4..]),
            GreenMax: BinaryPrimitives.ReadUInt16BigEndian(source[6..]),
            BlueMax: BinaryPrimitives.ReadUInt16BigEndian(source[8..]),
            RedShift: source[10],
            GreenShift: source[11],
            BlueShift: source[12]);
        // [13..15] は padding
    }

    public void Write(Span<byte> destination)
    {
        if (destination.Length < Size)
        {
            throw new ArgumentException($"PIXEL_FORMAT は {Size} バイト必要です。", nameof(destination));
        }

        destination[0] = BitsPerPixel;
        destination[1] = Depth;
        destination[2] = BigEndian ? (byte)1 : (byte)0;
        destination[3] = TrueColor ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt16BigEndian(destination[4..], RedMax);
        BinaryPrimitives.WriteUInt16BigEndian(destination[6..], GreenMax);
        BinaryPrimitives.WriteUInt16BigEndian(destination[8..], BlueMax);
        destination[10] = RedShift;
        destination[11] = GreenShift;
        destination[12] = BlueShift;
        destination[13] = 0;
        destination[14] = 0;
        destination[15] = 0;
    }

    public override string ToString() =>
        $"{BitsPerPixel}bpp depth={Depth} {(BigEndian ? "BE" : "LE")} {(TrueColor ? "true-color" : "colour-map")} " +
        $"max=({RedMax},{GreenMax},{BlueMax}) shift=({RedShift},{GreenShift},{BlueShift})";
}
