namespace RexRemote.Protocol.Input;

/// <summary>
/// Windows のキー押下・解放を RFB の KeyEvent（keysym）に変換し、押下中のキーを管理する。
/// <list type="bullet">
/// <item>解放時は押下時に送った keysym をそのまま送る（途中で Shift を離しても 'A' の down に 'a' の up を送らない）</item>
/// <item>キーリピートは down を繰り返し送る（Mac は注入されたキーを自動リピートしないため）</item>
/// <item>フォーカスを失ったら <see cref="ReleaseAll"/> で押下中のキーをすべて離し、Mac 側でキーが押しっぱなしになるのを防ぐ</item>
/// </list>
/// CapsLock は Mac に送らず Windows 側で効かせる（<see cref="WindowsKeyMap.IsPassThrough"/>）。
/// Mac の画面共有サーバーは CapsLock の keysym を物理キーのようには扱わず、入力ソース切り替えなどが不安定なため（実機で確認）。
/// Mac の入力ソース切り替えは Ctrl+Space（Mac 標準ショートカット）を使う。
/// 入力内容はログに出さないこと（パスワード入力を含むため）。
/// </summary>
public sealed class KeyboardTranslator
{
    private readonly Dictionary<int, uint> _pressed = [];

    /// <summary>Ctrl ⇔ Command を入れ替える。押下中のキーには影響しない。</summary>
    public bool SwapControlAndCommand { get; set; }

    public bool IsDown(int vk) => _pressed.ContainsKey(vk);

    public bool IsShiftDown => IsDown(WindowsKeyMap.VK_LSHIFT) || IsDown(WindowsKeyMap.VK_RSHIFT) || IsDown(WindowsKeyMap.VK_SHIFT);

    /// <summary>キーが押された（リピートを含む）。</summary>
    /// <param name="text">
    /// Windows のキーボード配列でこのキーが出す文字（Shift と CapsLock だけを反映し、Ctrl/Alt/Win は除いたもの）。文字にならなければ null。
    /// </param>
    /// <returns>送る keysym。送るものが無ければ null。</returns>
    public uint? KeyDown(int vk, bool extended, string? text)
    {
        if (_pressed.TryGetValue(vk, out uint repeated))
        {
            return repeated; // リピート
        }

        uint? keysym = WindowsKeyMap.MapSpecialKey(vk, extended, SwapControlAndCommand) ?? KeySymbols.FromText(text);
        if (keysym is uint value)
        {
            _pressed[vk] = value;
        }

        return keysym;
    }

    /// <summary>キーが離された。押下時に送った keysym を返す（押下を送っていなければ null）。</summary>
    public uint? KeyUp(int vk) => _pressed.Remove(vk, out uint keysym) ? keysym : null;

    /// <summary>押下中のキーをすべて離す。修飾キーは最後に離す。</summary>
    public IReadOnlyList<uint> ReleaseAll()
    {
        var released = _pressed
            .OrderBy(pair => WindowsKeyMap.IsModifier(pair.Key) ? 1 : 0)
            .Select(pair => pair.Value)
            .ToList();
        _pressed.Clear();
        return released;
    }
}
