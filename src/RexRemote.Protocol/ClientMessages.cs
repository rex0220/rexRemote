using System.Buffers.Binary;
using System.Text;

namespace RexRemote.Protocol;

/// <summary>クライアント → サーバーのメッセージを組み立てる。</summary>
internal static class ClientMessages
{
    public const byte SetPixelFormatType = 0;
    public const byte SetEncodingsType = 2;
    public const byte FramebufferUpdateRequestType = 3;
    public const byte KeyEventType = 4;
    public const byte PointerEventType = 5;
    public const byte ClientCutTextType = 6;

    public static byte[] ProtocolVersion38() => "RFB 003.008\n"u8.ToArray();

    public static byte[] SetPixelFormat(PixelFormat format)
    {
        var msg = new byte[4 + PixelFormat.Size];
        msg[0] = SetPixelFormatType;
        // [1..3] padding
        format.Write(msg.AsSpan(4));
        return msg;
    }

    public static byte[] SetEncodings(IReadOnlyList<RfbEncoding> encodings)
    {
        ArgumentNullException.ThrowIfNull(encodings);
        if (encodings.Count > ushort.MaxValue)
        {
            throw new ArgumentException("エンコーディングが多すぎます。", nameof(encodings));
        }

        var msg = new byte[4 + 4 * encodings.Count];
        msg[0] = SetEncodingsType;
        // [1] padding
        BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(2), (ushort)encodings.Count);
        for (int i = 0; i < encodings.Count; i++)
        {
            BinaryPrimitives.WriteInt32BigEndian(msg.AsSpan(4 + 4 * i), (int)encodings[i]);
        }

        return msg;
    }

    public static byte[] FramebufferUpdateRequest(bool incremental, int x, int y, int width, int height)
    {
        var msg = new byte[10];
        msg[0] = FramebufferUpdateRequestType;
        msg[1] = incremental ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(2), ToU16(x));
        BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(4), ToU16(y));
        BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(6), ToU16(width));
        BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(8), ToU16(height));
        return msg;
    }

    public static byte[] KeyEvent(bool down, uint keysym)
    {
        var msg = new byte[8];
        msg[0] = KeyEventType;
        msg[1] = down ? (byte)1 : (byte)0;
        // [2..3] padding
        BinaryPrimitives.WriteUInt32BigEndian(msg.AsSpan(4), keysym);
        return msg;
    }

    public static byte[] PointerEvent(RfbButtons buttons, int x, int y)
    {
        var msg = new byte[6];
        msg[0] = PointerEventType;
        msg[1] = (byte)buttons;
        BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(2), ToU16(x));
        BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(4), ToU16(y));
        return msg;
    }

    /// <summary>
    /// ClientCutText。RFB 標準では ISO-8859-1（Latin-1）で、範囲外の文字は '?' になる。
    /// <paramref name="encoding"/> に UTF-8 を渡すと日本語も送れる（サーバーが UTF-8 として解釈する場合）。
    /// 改行は RFB の決まりどおり LF だけにする。
    /// </summary>
    public static byte[] ClientCutText(string text, Encoding? encoding = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        string normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        byte[] body = (encoding ?? Encoding.Latin1).GetBytes(normalized);
        var msg = new byte[8 + body.Length];
        msg[0] = ClientCutTextType;
        // [1..3] padding
        BinaryPrimitives.WriteUInt32BigEndian(msg.AsSpan(4), (uint)body.Length);
        body.CopyTo(msg, 8);
        return msg;
    }

    private static ushort ToU16(int value) => (ushort)Math.Clamp(value, 0, ushort.MaxValue);
}
