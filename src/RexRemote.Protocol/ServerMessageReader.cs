using System.Text;
using RexRemote.Protocol.Decoding;
using RexRemote.Protocol.IO;

namespace RexRemote.Protocol;

internal abstract record ServerMessage;

/// <param name="Cursor">この更新に含まれていたカーソル形状（最後のもの）。無ければ null。</param>
internal sealed record FramebufferUpdateMessage(IReadOnlyList<RfbRectangle> Rectangles, bool Resized, CursorShape? Cursor = null) : ServerMessage;

internal sealed record SetColourMapEntriesMessage(int FirstColour, int Count) : ServerMessage;

internal sealed record BellMessage : ServerMessage;

internal sealed record ServerCutTextMessage(string Text) : ServerMessage;

/// <summary>サーバー → クライアントのメッセージを読み、FramebufferUpdate はフレームバッファへ描く。</summary>
internal sealed class ServerMessageReader : IDisposable
{
    public const byte FramebufferUpdateType = 0;
    public const byte SetColourMapEntriesType = 1;
    public const byte BellType = 2;
    public const byte ServerCutTextType = 3;

    private const int MaxCutTextLength = 64 * 1024 * 1024;
    private const int MaxZrleLength = 256 * 1024 * 1024;
    private const int MaxCursorSize = 1024; // 1 辺。Retina の拡大カーソルでも十分

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly RfbReader _reader;
    private readonly ZrleDecoder _zrle = new();
    private byte[] _scratch = [];

    public ServerMessageReader(RfbReader reader, Framebuffer framebuffer)
    {
        _reader = reader;
        Framebuffer = framebuffer;
    }

    public Framebuffer Framebuffer { get; }

    public async ValueTask<ServerMessage> ReadAsync(CancellationToken ct)
    {
        byte type = await _reader.ReadU8Async(ct).ConfigureAwait(false);
        switch (type)
        {
            case FramebufferUpdateType:
                return await ReadFramebufferUpdateAsync(ct).ConfigureAwait(false);

            case SetColourMapEntriesType:
            {
                // true color を要求しているので本来は来ない。中身は読み飛ばす
                await _reader.SkipAsync(1, ct).ConfigureAwait(false);
                int first = await _reader.ReadU16Async(ct).ConfigureAwait(false);
                int count = await _reader.ReadU16Async(ct).ConfigureAwait(false);
                await _reader.SkipAsync(6L * count, ct).ConfigureAwait(false);
                return new SetColourMapEntriesMessage(first, count);
            }

            case BellType:
                return new BellMessage();

            case ServerCutTextType:
            {
                await _reader.SkipAsync(3, ct).ConfigureAwait(false);
                uint length = await _reader.ReadU32Async(ct).ConfigureAwait(false);
                if (length > MaxCutTextLength)
                {
                    // 拡張クリップボード（長さが負）は要求していないので来ないはず
                    throw new RfbProtocolException($"ServerCutText が大きすぎます: {length} バイト");
                }

                byte[] text = await _reader.ReadBytesAsync((int)length, ct).ConfigureAwait(false);
                return new ServerCutTextMessage(DecodeCutText(text));
            }

            default:
                throw new RfbProtocolException($"未知のサーバーメッセージ種別です: {type}");
        }
    }

    private async ValueTask<FramebufferUpdateMessage> ReadFramebufferUpdateAsync(CancellationToken ct)
    {
        await _reader.SkipAsync(1, ct).ConfigureAwait(false); // padding
        int count = await _reader.ReadU16Async(ct).ConfigureAwait(false);
        var rectangles = new List<RfbRectangle>(count);
        bool resized = false;
        CursorShape? cursor = null;

        for (int i = 0; i < count; i++)
        {
            int x = await _reader.ReadU16Async(ct).ConfigureAwait(false);
            int y = await _reader.ReadU16Async(ct).ConfigureAwait(false);
            int w = await _reader.ReadU16Async(ct).ConfigureAwait(false);
            int h = await _reader.ReadU16Async(ct).ConfigureAwait(false);
            var encoding = (RfbEncoding)await _reader.ReadS32Async(ct).ConfigureAwait(false);

            switch (encoding)
            {
                case RfbEncoding.Raw:
                    await ReadRawAsync(x, y, w, h, ct).ConfigureAwait(false);
                    rectangles.Add(new RfbRectangle(x, y, w, h));
                    break;

                case RfbEncoding.CopyRect:
                {
                    int sx = await _reader.ReadU16Async(ct).ConfigureAwait(false);
                    int sy = await _reader.ReadU16Async(ct).ConfigureAwait(false);
                    Framebuffer.CopyRect(sx, sy, x, y, w, h);
                    rectangles.Add(new RfbRectangle(x, y, w, h));
                    break;
                }

                case RfbEncoding.Zrle:
                {
                    uint length = await _reader.ReadU32Async(ct).ConfigureAwait(false);
                    if (length > MaxZrleLength)
                    {
                        throw new RfbProtocolException($"ZRLE データが大きすぎます: {length} バイト");
                    }

                    Memory<byte> data = RentScratch((int)length);
                    await _reader.ReadExactlyAsync(data, ct).ConfigureAwait(false);
                    _zrle.Decode(data.Span, x, y, w, h, Framebuffer);
                    rectangles.Add(new RfbRectangle(x, y, w, h));
                    break;
                }

                case RfbEncoding.DesktopSize:
                    Framebuffer.Resize(w, h);
                    resized = true;
                    break;

                case RfbEncoding.Cursor:
                    cursor = await ReadCursorAsync(x, y, w, h, ct).ConfigureAwait(false);
                    break;

                default:
                    throw new RfbProtocolException($"要求していないエンコーディングを受信しました: {(int)encoding}");
            }
        }

        return new FramebufferUpdateMessage(rectangles, resized, cursor);
    }

    /// <summary>
    /// Cursor 擬似エンコーディング。x, y がホットスポット、続いて w*h ピクセル（Bgr32）と
    /// 1 行 (w+7)/8 バイトのマスク（上位ビットが左、1 = 不透明）。
    /// </summary>
    private async ValueTask<CursorShape> ReadCursorAsync(int hotspotX, int hotspotY, int w, int h, CancellationToken ct)
    {
        if (w > MaxCursorSize || h > MaxCursorSize)
        {
            throw new RfbProtocolException($"カーソルが大きすぎます: {w}x{h}");
        }

        byte[] pixels = await _reader.ReadBytesAsync(w * h * Framebuffer.BytesPerPixel, ct).ConfigureAwait(false);
        int maskStride = (w + 7) / 8;
        byte[] mask = await _reader.ReadBytesAsync(maskStride * h, ct).ConfigureAwait(false);
        return CreateCursorShape(hotspotX, hotspotY, w, h, pixels, mask);
    }

    internal static CursorShape CreateCursorShape(int hotspotX, int hotspotY, int w, int h, byte[] pixels, byte[] mask)
    {
        int maskStride = (w + 7) / 8;
        var bgra = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                bool opaque = (mask[y * maskStride + (x >> 3)] & (0x80 >> (x & 7))) != 0;
                bgra[i] = pixels[i];
                bgra[i + 1] = pixels[i + 1];
                bgra[i + 2] = pixels[i + 2];
                bgra[i + 3] = opaque ? (byte)255 : (byte)0;
            }
        }

        return new CursorShape(hotspotX, hotspotY, w, h, bgra);
    }

    /// <summary>
    /// ServerCutText の文字列。RFB 標準は Latin-1 だが、正しい UTF-8 として読めるならそちらを優先する
    /// （日本語を UTF-8 で送ってくるサーバーに対応するため。ASCII はどちらでも同じ）。改行は LF のまま返す。
    /// </summary>
    internal static string DecodeCutText(byte[] bytes)
    {
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }

    /// <summary>Raw は行ごとにフレームバッファへ直接読み込む。はみ出した部分は読み捨てる。</summary>
    private async ValueTask ReadRawAsync(int x, int y, int w, int h, CancellationToken ct)
    {
        int rowBytes = w * Framebuffer.BytesPerPixel;
        for (int row = 0; row < h; row++)
        {
            Memory<byte> destination = Framebuffer.GetRowMemory(x, y + row, w);
            await _reader.ReadExactlyAsync(destination, ct).ConfigureAwait(false);
            if (destination.Length < rowBytes)
            {
                await _reader.SkipAsync(rowBytes - destination.Length, ct).ConfigureAwait(false);
            }
        }
    }

    private Memory<byte> RentScratch(int length)
    {
        if (_scratch.Length < length)
        {
            _scratch = new byte[Math.Max(length, Math.Min(_scratch.Length * 2, MaxZrleLength))];
        }

        return _scratch.AsMemory(0, length);
    }

    public void Dispose() => _zrle.Dispose();
}
