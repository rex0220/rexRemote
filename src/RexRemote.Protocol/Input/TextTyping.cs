namespace RexRemote.Protocol.Input;

/// <summary>
/// 文字列を「キーを押して離す」の KeyEvent 列にする（Mac のロック画面へのパスワード入力などに使う）。
/// Mac 側の配列は US（ABC）を想定し、US 配列で Shift が要る文字は Shift_L で挟む。
/// サーバーが自分で Shift を補う場合でも、二重の Shift は無害。
/// </summary>
public static class TextTyping
{
    private const string UsShiftedSymbols = "~!@#$%^&*()_+{}|:\"<>?";

    public static IReadOnlyList<KeyAction> ToKeyActions(string text, bool pressReturn = false)
    {
        ArgumentNullException.ThrowIfNull(text);
        var actions = new List<KeyAction>(text.Length * 4 + 2);

        for (int i = 0; i < text.Length; i += char.IsSurrogatePair(text, i) ? 2 : 1)
        {
            int codePoint = char.ConvertToUtf32(text, i);
            if (KeySymbols.FromCodePoint(codePoint) is not uint keysym)
            {
                continue; // 制御文字などは送らない
            }

            bool shift = codePoint is >= 'A' and <= 'Z' || (codePoint < 0x80 && UsShiftedSymbols.Contains((char)codePoint));
            if (shift)
            {
                actions.Add(new KeyAction(true, KeySymbols.ShiftL));
            }

            actions.Add(new KeyAction(true, keysym));
            actions.Add(new KeyAction(false, keysym));
            if (shift)
            {
                actions.Add(new KeyAction(false, KeySymbols.ShiftL));
            }
        }

        if (pressReturn)
        {
            actions.Add(new KeyAction(true, KeySymbols.Return));
            actions.Add(new KeyAction(false, KeySymbols.Return));
        }

        return actions;
    }
}

/// <summary>Mac に送る KeyEvent 1 つ分。</summary>
public readonly record struct KeyAction(bool Down, uint Keysym);
