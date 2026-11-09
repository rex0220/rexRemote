using RexRemote.Protocol.Input;
using static RexRemote.Protocol.Input.WindowsKeyMap;

namespace RexRemote.Protocol.Tests;

public class KeySymbolsTests
{
    [Theory]
    [InlineData('a', 0x61u)]
    [InlineData('A', 0x41u)]
    [InlineData('@', 0x40u)]
    [InlineData('\\', 0x5Cu)]
    [InlineData('¥', 0xA5u)]
    [InlineData('é', 0xE9u)]
    [InlineData('あ', 0x01003042u)]
    [InlineData('€', 0x010020ACu)]
    public void FromCodePoint_LatinAsIs_OtherUnicodeOffset(char c, uint expected) =>
        Assert.Equal(expected, KeySymbols.FromCodePoint(c));

    [Theory]
    [InlineData(0x03)] // Ctrl+C の制御文字
    [InlineData(0x7F)]
    [InlineData(0x9F)]
    [InlineData(0xD800)]
    public void FromCodePoint_ControlAndSurrogates_AreNull(int codePoint) =>
        Assert.Null(KeySymbols.FromCodePoint(codePoint));

    [Fact]
    public void FromText_SingleCodePointOnly()
    {
        Assert.Equal(0x61u, KeySymbols.FromText("a"));
        Assert.Equal(0x0101F600u, KeySymbols.FromText("😀")); // サロゲートペア
        Assert.Null(KeySymbols.FromText("ab"));
        Assert.Null(KeySymbols.FromText(""));
        Assert.Null(KeySymbols.FromText(null));
    }
}

public class WindowsKeyMapTests
{
    [Theory]
    [InlineData(VK_LWIN, KeySymbols.SuperL)]     // Win → Command
    [InlineData(VK_RWIN, KeySymbols.SuperR)]
    [InlineData(VK_LMENU, KeySymbols.AltL)]      // Alt → Option
    [InlineData(VK_LCONTROL, KeySymbols.ControlL)]
    [InlineData(VK_LSHIFT, KeySymbols.ShiftL)]
    [InlineData(VK_RSHIFT, KeySymbols.ShiftR)]
    [InlineData(VK_RETURN, KeySymbols.Return)]
    [InlineData(VK_BACK, KeySymbols.BackSpace)]
    [InlineData(VK_DELETE, KeySymbols.Delete)]
    [InlineData(VK_LEFT, KeySymbols.Left)]
    [InlineData(VK_SPACE, KeySymbols.Space)]
    [InlineData(VK_F1, KeySymbols.F1)]
    [InlineData(0x7B, KeySymbols.F1 + 11)]       // F12
    [InlineData(0x65, KeySymbols.Kp0 + 5)]       // テンキー 5
    [InlineData(VK_NONCONVERT, KeySymbols.EisuToggle)]
    [InlineData(VK_CONVERT, KeySymbols.Hiragana)]
    public void MapSpecialKey_Defaults(int vk, uint expected) =>
        Assert.Equal(expected, MapSpecialKey(vk, extended: false));

    [Fact]
    public void MapSpecialKey_NumpadEnterIsExtended() =>
        Assert.Equal(KeySymbols.KpEnter, MapSpecialKey(VK_RETURN, extended: true));

    [Fact]
    public void MapSpecialKey_SwapControlAndCommand()
    {
        Assert.Equal(KeySymbols.SuperL, MapSpecialKey(VK_LCONTROL, false, swapControlAndCommand: true));
        Assert.Equal(KeySymbols.SuperR, MapSpecialKey(VK_RCONTROL, false, swapControlAndCommand: true));
        Assert.Equal(KeySymbols.ControlL, MapSpecialKey(VK_LWIN, false, swapControlAndCommand: true));
        Assert.Equal(KeySymbols.AltL, MapSpecialKey(VK_LMENU, false, swapControlAndCommand: true));
    }

    [Theory]
    [InlineData(0x41)] // A
    [InlineData(0x32)] // 2
    [InlineData(0xBA)] // OEM_1
    [InlineData(0xE2)] // OEM_102（JIS の ろ）
    public void MapSpecialKey_CharacterKeysAreNull(int vk) =>
        Assert.Null(MapSpecialKey(vk, extended: false));
}

public class TextTypingTests
{
    [Fact]
    public void LowercaseAndDigits_ArePlainPressRelease()
    {
        Assert.Equal(
            new KeyAction[] { new(true, 'a'), new(false, 'a'), new(true, '1'), new(false, '1') },
            TextTyping.ToKeyActions("a1"));
    }

    [Fact]
    public void UppercaseAndShiftedSymbols_AreWrappedWithShift()
    {
        Assert.Equal(
            new KeyAction[]
            {
                new(true, KeySymbols.ShiftL), new(true, 'P'), new(false, 'P'), new(false, KeySymbols.ShiftL),
                new(true, KeySymbols.ShiftL), new(true, '!'), new(false, '!'), new(false, KeySymbols.ShiftL),
                new(true, '-'), new(false, '-'),
            },
            TextTyping.ToKeyActions("P!-"));
    }

    [Fact]
    public void PressReturn_AppendsEnter()
    {
        IReadOnlyList<KeyAction> actions = TextTyping.ToKeyActions("x", pressReturn: true);
        Assert.Equal(new KeyAction(true, KeySymbols.Return), actions[^2]);
        Assert.Equal(new KeyAction(false, KeySymbols.Return), actions[^1]);
    }

    [Fact]
    public void ControlCharactersAreSkipped_NonAsciiUsesUnicodeKeysym()
    {
        Assert.Empty(TextTyping.ToKeyActions("\t\n"));
        Assert.Equal(
            new KeyAction[] { new(true, 0x010065E5u), new(false, 0x010065E5u) },
            TextTyping.ToKeyActions("日"));
    }
}

public class KeyboardTranslatorTests
{
    private const int VK_A = 0x41;
    private const int VK_C = 0x43;

    [Fact]
    public void CharacterKey_SendsCharacterKeysym()
    {
        var t = new KeyboardTranslator();
        Assert.Equal(0x61u, t.KeyDown(VK_A, false, "a"));
        Assert.Equal(0x61u, t.KeyUp(VK_A));
    }

    [Fact]
    public void KeyUp_SendsSameKeysymAsKeyDown_EvenIfShiftChanged()
    {
        var t = new KeyboardTranslator();
        Assert.Equal(KeySymbols.ShiftL, t.KeyDown(VK_LSHIFT, false, null));
        Assert.True(t.IsShiftDown);
        Assert.Equal(0x41u, t.KeyDown(VK_A, false, "A"));
        Assert.Equal(KeySymbols.ShiftL, t.KeyUp(VK_LSHIFT));
        Assert.False(t.IsShiftDown);
        Assert.Equal(0x41u, t.KeyUp(VK_A)); // 'a' ではなく 'A' を離す
    }

    [Fact]
    public void Repeat_ResendsSameKeysym()
    {
        var t = new KeyboardTranslator();
        Assert.Equal(0x61u, t.KeyDown(VK_A, false, "a"));
        Assert.Equal(0x61u, t.KeyDown(VK_A, false, "a"));
        Assert.Equal(0x61u, t.KeyUp(VK_A));
        Assert.Null(t.KeyUp(VK_A));
    }

    [Fact]
    public void SpecialKeyTableWinsOverText()
    {
        var t = new KeyboardTranslator();
        Assert.Equal(KeySymbols.Return, t.KeyDown(VK_RETURN, false, "\r"));
        Assert.Equal(KeySymbols.Space, t.KeyDown(VK_SPACE, false, " "));
    }

    [Fact]
    public void UnmappedKey_SendsNothing()
    {
        var t = new KeyboardTranslator();
        Assert.Null(t.KeyDown(0xFF, false, null));
        Assert.False(t.IsDown(0xFF));
        Assert.Null(t.KeyUp(0xFF));
    }

    [Fact]
    public void ControlCombination_UsesPlainLetter_AndSwapMakesCommand()
    {
        var t = new KeyboardTranslator { SwapControlAndCommand = true };
        Assert.Equal(KeySymbols.SuperL, t.KeyDown(VK_LCONTROL, false, null)); // Ctrl → Command
        Assert.Equal(0x63u, t.KeyDown(VK_C, false, "c"));                     // Command+C
        t.SwapControlAndCommand = false;                                      // 押下中に切り替えても
        Assert.Equal(0x63u, t.KeyUp(VK_C));
        Assert.Equal(KeySymbols.SuperL, t.KeyUp(VK_LCONTROL));                // 押下時の keysym で離す
    }

    [Fact]
    public void ReleaseAll_ReleasesEverything_ModifiersLast()
    {
        var t = new KeyboardTranslator();
        t.KeyDown(VK_LWIN, false, null);
        t.KeyDown(VK_A, false, "a");
        t.KeyDown(VK_LSHIFT, false, null);

        IReadOnlyList<uint> released = t.ReleaseAll();

        Assert.Equal(3, released.Count);
        Assert.Equal(0x61u, released[0]);
        Assert.Contains(KeySymbols.SuperL, released);
        Assert.Contains(KeySymbols.ShiftL, released);
        Assert.False(t.IsDown(VK_A));
        Assert.Empty(t.ReleaseAll());
    }

    [Fact]
    public void PassThroughKeys() =>
        Assert.True(IsPassThrough(VK_CAPITAL) && IsPassThrough(VK_NUMLOCK) && !IsPassThrough(VK_A));

    [Fact]
    public void PrintScreen_IsLeftToWindows()
    {
        // 接続中でも Windows のスクリーンショット（Snipping Tool）を使えるようにする
        Assert.True(IsPassThrough(VK_SNAPSHOT));
        Assert.Null(MapSpecialKey(VK_SNAPSHOT, extended: false));
    }

    [Fact]
    public void CapsLock_IsNotSentToMac()
    {
        // Mac の画面共有サーバーは CapsLock の keysym を物理キーのように扱わない（実機で確認）ので Windows 側で効かせる
        Assert.Null(MapSpecialKey(VK_CAPITAL, extended: false));
    }
}
