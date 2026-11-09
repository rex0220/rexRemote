namespace RexRemote.Protocol.Input;

/// <summary>
/// Windows の仮想キーコード → keysym のうち、文字にならないキー（修飾キー・矢印・ファンクション・テンキーなど）の対応表。
/// 文字キーは Windows のキーボード配列で実際に出る文字から keysym を作る（<see cref="KeyboardTranslator"/>）。
/// UI に依存しない純粋な表なのでテストできるよう Protocol 側に置く。
/// </summary>
public static class WindowsKeyMap
{
    // 仮想キーコード（WinUser.h）
    public const int VK_BACK = 0x08;
    public const int VK_TAB = 0x09;
    public const int VK_RETURN = 0x0D;
    public const int VK_SHIFT = 0x10;
    public const int VK_CONTROL = 0x11;
    public const int VK_MENU = 0x12;
    public const int VK_PAUSE = 0x13;
    public const int VK_CAPITAL = 0x14;
    public const int VK_KANA = 0x15;
    public const int VK_KANJI = 0x19;
    public const int VK_ESCAPE = 0x1B;
    public const int VK_CONVERT = 0x1C;     // 変換
    public const int VK_NONCONVERT = 0x1D;  // 無変換
    public const int VK_SPACE = 0x20;
    public const int VK_PRIOR = 0x21;
    public const int VK_NEXT = 0x22;
    public const int VK_END = 0x23;
    public const int VK_HOME = 0x24;
    public const int VK_LEFT = 0x25;
    public const int VK_UP = 0x26;
    public const int VK_RIGHT = 0x27;
    public const int VK_DOWN = 0x28;
    public const int VK_SNAPSHOT = 0x2C;
    public const int VK_INSERT = 0x2D;
    public const int VK_DELETE = 0x2E;
    public const int VK_LWIN = 0x5B;
    public const int VK_RWIN = 0x5C;
    public const int VK_APPS = 0x5D;
    public const int VK_NUMPAD0 = 0x60;
    public const int VK_NUMPAD9 = 0x69;
    public const int VK_MULTIPLY = 0x6A;
    public const int VK_ADD = 0x6B;
    public const int VK_SEPARATOR = 0x6C;
    public const int VK_SUBTRACT = 0x6D;
    public const int VK_DECIMAL = 0x6E;
    public const int VK_DIVIDE = 0x6F;
    public const int VK_F1 = 0x70;
    public const int VK_F24 = 0x87;
    public const int VK_NUMLOCK = 0x90;
    public const int VK_SCROLL = 0x91;
    public const int VK_LSHIFT = 0xA0;
    public const int VK_RSHIFT = 0xA1;
    public const int VK_LCONTROL = 0xA2;
    public const int VK_RCONTROL = 0xA3;
    public const int VK_LMENU = 0xA4;
    public const int VK_RMENU = 0xA5;
    public const int VK_OEM_ATTN = 0xF0;    // JIS: 英数（CapsLock）
    public const int VK_OEM_COPY = 0xF2;    // JIS: カタカナ/ひらがな
    public const int VK_OEM_AUTO = 0xF3;    // JIS: 半角/全角
    public const int VK_OEM_ENLW = 0xF4;    // JIS: 半角/全角

    /// <summary>
    /// Mac に送らず Windows に任せるキー。
    /// <list type="bullet">
    /// <item>CapsLock / NumLock：トグル状態を Windows 側で持つ。大文字・小文字は Windows の CapsLock 状態で決めた文字を送る
    ///   （Mac の画面共有サーバーは CapsLock の keysym を物理キーのようには扱わないため。実機で確認）</item>
    /// <item>PrintScreen：Mac に相当するキーが無いので、接続中でも Windows のスクリーンショット（Snipping Tool）に使えるようにする</item>
    /// </list>
    /// </summary>
    public static bool IsPassThrough(int vk) => vk is VK_CAPITAL or VK_NUMLOCK or VK_OEM_ATTN or VK_SNAPSHOT;

    /// <summary>修飾キー（押しっぱなしで意味を持つキー）。</summary>
    public static bool IsModifier(int vk) => vk is VK_SHIFT or VK_LSHIFT or VK_RSHIFT
        or VK_CONTROL or VK_LCONTROL or VK_RCONTROL
        or VK_MENU or VK_LMENU or VK_RMENU
        or VK_LWIN or VK_RWIN;

    /// <param name="extended">拡張キーフラグ（テンキーの Enter の区別に使う）。</param>
    /// <param name="swapControlAndCommand">Ctrl → Command、Win → Control にする。</param>
    /// <returns>文字キーなど対応表に無いキーは null。</returns>
    public static uint? MapSpecialKey(int vk, bool extended, bool swapControlAndCommand = false)
    {
        switch (vk)
        {
            // 修飾キー。Win → Command（Super）、Alt → Option（Alt）
            case VK_SHIFT or VK_LSHIFT: return KeySymbols.ShiftL;
            case VK_RSHIFT: return KeySymbols.ShiftR;
            case VK_CONTROL or VK_LCONTROL: return swapControlAndCommand ? KeySymbols.SuperL : KeySymbols.ControlL;
            case VK_RCONTROL: return swapControlAndCommand ? KeySymbols.SuperR : KeySymbols.ControlR;
            case VK_LWIN: return swapControlAndCommand ? KeySymbols.ControlL : KeySymbols.SuperL;
            case VK_RWIN: return swapControlAndCommand ? KeySymbols.ControlR : KeySymbols.SuperR;
            case VK_MENU or VK_LMENU: return KeySymbols.AltL;
            case VK_RMENU: return KeySymbols.AltR;

            case VK_BACK: return KeySymbols.BackSpace;
            case VK_TAB: return KeySymbols.Tab;
            case VK_RETURN: return extended ? KeySymbols.KpEnter : KeySymbols.Return;
            case VK_PAUSE: return KeySymbols.Pause;
            case VK_ESCAPE: return KeySymbols.Escape;
            case VK_SPACE: return KeySymbols.Space;
            case VK_PRIOR: return KeySymbols.PageUp;
            case VK_NEXT: return KeySymbols.PageDown;
            case VK_END: return KeySymbols.End;
            case VK_HOME: return KeySymbols.Home;
            case VK_LEFT: return KeySymbols.Left;
            case VK_UP: return KeySymbols.Up;
            case VK_RIGHT: return KeySymbols.Right;
            case VK_DOWN: return KeySymbols.Down;
            case VK_INSERT: return KeySymbols.Insert;
            case VK_DELETE: return KeySymbols.Delete;
            case VK_APPS: return KeySymbols.Menu;
            case VK_SCROLL: return KeySymbols.ScrollLock;

            case >= VK_NUMPAD0 and <= VK_NUMPAD9: return KeySymbols.Kp0 + (uint)(vk - VK_NUMPAD0);
            case VK_MULTIPLY: return KeySymbols.KpMultiply;
            case VK_ADD: return KeySymbols.KpAdd;
            case VK_SEPARATOR: return KeySymbols.KpSeparator;
            case VK_SUBTRACT: return KeySymbols.KpSubtract;
            case VK_DECIMAL: return KeySymbols.KpDecimal;
            case VK_DIVIDE: return KeySymbols.KpDivide;

            case >= VK_F1 and <= VK_F24: return KeySymbols.F1 + (uint)(vk - VK_F1);

            // 日本語キーボード。Mac 側での効き方は実機で確認しながら調整する（CLAUDE.md「キーボード」）
            case VK_NONCONVERT: return KeySymbols.EisuToggle;      // 無変換 → 英数
            case VK_CONVERT: return KeySymbols.Hiragana;           // 変換 → かな
            case VK_OEM_COPY or VK_KANA: return KeySymbols.HiraganaKatakana;
            case VK_OEM_AUTO or VK_OEM_ENLW or VK_KANJI: return KeySymbols.ZenkakuHankaku;

            default: return null;
        }
    }
}
