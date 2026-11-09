namespace RexRemote.Protocol;

/// <summary>セキュリティタイプ。</summary>
public enum RfbSecurityType : byte
{
    Invalid = 0,
    None = 1,
    VncAuthentication = 2,
    AppleRemoteDesktop = 30,
}

/// <summary>SetEncodings で使うエンコーディング番号（擬似エンコーディングを含む）。</summary>
public enum RfbEncoding : int
{
    Raw = 0,
    CopyRect = 1,
    Zrle = 16,
    DesktopSize = -223,

    /// <summary>カーソル形状（RichCursor）。サーバーはカーソルを画面に描かず、形状だけ送ってくる。</summary>
    Cursor = -239,
}

/// <summary>
/// カーソル形状。<see cref="Bgra"/> は 1 ピクセル 4 バイト（B, G, R, A）、A は 0（透明）か 255。
/// 幅または高さが 0 なら「カーソルを隠す」。座標はリモート画面のピクセル単位（Retina なら 2 倍の大きさ）。
/// </summary>
public sealed record CursorShape(int HotspotX, int HotspotY, int Width, int Height, byte[] Bgra)
{
    public bool IsHidden => Width == 0 || Height == 0;
}

/// <summary>PointerEvent のボタンマスク。</summary>
[Flags]
public enum RfbButtons : byte
{
    None = 0,
    Left = 1 << 0,
    Middle = 1 << 1,
    Right = 1 << 2,
    WheelUp = 1 << 3,
    WheelDown = 1 << 4,
    WheelLeft = 1 << 5,
    WheelRight = 1 << 6,
}

/// <summary>矩形（フレームバッファ座標・ピクセル単位）。</summary>
public readonly record struct RfbRectangle(int X, int Y, int Width, int Height);

/// <summary>ServerInit メッセージの内容。</summary>
public sealed record ServerInit(int Width, int Height, PixelFormat PixelFormat, string Name);
