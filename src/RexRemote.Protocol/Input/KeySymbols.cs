namespace RexRemote.Protocol.Input;

/// <summary>KeyEvent で送る X11 keysym。</summary>
public static class KeySymbols
{
    public const uint Space = 0x0020;

    public const uint BackSpace = 0xFF08;
    public const uint Tab = 0xFF09;
    public const uint Return = 0xFF0D;
    public const uint Pause = 0xFF13;
    public const uint ScrollLock = 0xFF14;
    public const uint Escape = 0xFF1B;
    public const uint Delete = 0xFFFF;

    // 日本語キーボード
    public const uint Kanji = 0xFF21;
    public const uint Muhenkan = 0xFF22;
    public const uint Henkan = 0xFF23;
    public const uint Hiragana = 0xFF25;
    public const uint Katakana = 0xFF26;
    public const uint HiraganaKatakana = 0xFF27;
    public const uint ZenkakuHankaku = 0xFF2A;
    public const uint EisuToggle = 0xFF30;

    public const uint Home = 0xFF50;
    public const uint Left = 0xFF51;
    public const uint Up = 0xFF52;
    public const uint Right = 0xFF53;
    public const uint Down = 0xFF54;
    public const uint PageUp = 0xFF55;
    public const uint PageDown = 0xFF56;
    public const uint End = 0xFF57;

    public const uint Print = 0xFF61;
    public const uint Insert = 0xFF63;
    public const uint Menu = 0xFF67;
    public const uint NumLock = 0xFF7F;

    public const uint KpEnter = 0xFF8D;
    public const uint KpMultiply = 0xFFAA;
    public const uint KpAdd = 0xFFAB;
    public const uint KpSeparator = 0xFFAC;
    public const uint KpSubtract = 0xFFAD;
    public const uint KpDecimal = 0xFFAE;
    public const uint KpDivide = 0xFFAF;
    public const uint Kp0 = 0xFFB0; // Kp0..Kp9 は連番

    public const uint F1 = 0xFFBE;  // F1..F35 は連番

    public const uint ShiftL = 0xFFE1;
    public const uint ShiftR = 0xFFE2;
    public const uint ControlL = 0xFFE3;
    public const uint ControlR = 0xFFE4;
    public const uint CapsLock = 0xFFE5;
    public const uint MetaL = 0xFFE7;
    public const uint MetaR = 0xFFE8;
    public const uint AltL = 0xFFE9;   // Mac の Option
    public const uint AltR = 0xFFEA;
    public const uint SuperL = 0xFFEB; // Mac の Command
    public const uint SuperR = 0xFFEC;

    /// <summary>
    /// 文字を keysym にする。Latin-1 の表示文字はその値そのまま、それ以外の Unicode は 0x01000000 + コードポイント。
    /// 制御文字やサロゲートは null。
    /// </summary>
    public static uint? FromCodePoint(int codePoint)
    {
        if (codePoint is (>= 0x20 and <= 0x7E) or (>= 0xA0 and <= 0xFF))
        {
            return (uint)codePoint;
        }

        if (codePoint < 0x100 || codePoint > 0x10FFFF || codePoint is >= 0xD800 and <= 0xDFFF)
        {
            return null;
        }

        return 0x0100_0000u | (uint)codePoint;
    }

    /// <summary>1 文字（1 コードポイント）の文字列なら keysym にする。</summary>
    public static uint? FromText(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        int codePoint = char.ConvertToUtf32(text, 0);
        int length = char.IsSurrogatePair(text, 0) ? 2 : 1;
        return text.Length == length ? FromCodePoint(codePoint) : null;
    }
}
