using System.Runtime.InteropServices;
using RexRemote.Protocol.Input;

namespace RexRemote.App.Input;

/// <summary>Windows のキーボード配列（JIS など）で、あるキーが出す文字を調べる。</summary>
internal static class KeyboardLayout
{
    private const uint ToUnicodeDoNotChangeState = 0x4; // Windows 10 1607 以降: カーネルのデッドキー状態を変えない

    /// <summary>
    /// <paramref name="vk"/> が出す文字。Shift と CapsLock だけを反映し、Ctrl・Alt・Win は無視する
    /// （Ctrl+C でも 'c' を返す。修飾キーは別に KeyEvent で送るため）。文字にならなければ null。
    /// </summary>
    /// <param name="keyboardLayout">使うキーボード配列（HKL）。フックは別スレッドで動くので、UI スレッドの配列を渡す。</param>
    public static string? GetText(int vk, int scanCode, bool shift, IntPtr keyboardLayout)
    {
        var state = new byte[256];
        if (shift)
        {
            state[WindowsKeyMap.VK_SHIFT] = 0x80;
            state[WindowsKeyMap.VK_LSHIFT] = 0x80;
        }

        // CapsLock は Windows に任せているので、Windows 側のトグル状態を使う
        if ((GetKeyState(WindowsKeyMap.VK_CAPITAL) & 1) != 0)
        {
            state[WindowsKeyMap.VK_CAPITAL] = 0x01;
        }

        var buffer = new char[8];
        int n = ToUnicodeEx((uint)vk, (uint)scanCode, state, buffer, buffer.Length, ToUnicodeDoNotChangeState, keyboardLayout);
        // n < 0 はデッドキー。JIS 配列には無いので扱わない
        return n > 0 ? new string(buffer, 0, n) : null;
    }

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int nVirtKey);

    /// <summary>指定したスレッド（Win32 のスレッド ID）のキーボード配列。</summary>
    public static IntPtr GetLayoutOfThread(uint threadId) => GetKeyboardLayout(threadId);

    [DllImport("user32.dll")]
    private static extern IntPtr GetKeyboardLayout(uint idThread);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ToUnicodeEx(
        uint wVirtKey, uint wScanCode, byte[] lpKeyState,
        [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 4)] char[] pwszBuff,
        int cchBuff, uint wFlags, IntPtr dwhkl);
}
