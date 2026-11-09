using System.ComponentModel;
using System.Runtime.InteropServices;

namespace RexRemote.App.Input;

/// <summary>
/// 低レベルキーボードフック（WH_KEYBOARD_LL）。Win キー・Alt+Tab・IME の切り替えキーなども
/// Windows が処理する前に受け取れる。
/// <para>
/// 低レベルフックは Windows 全体のキー入力がコールバックの完了を待つ。UI スレッドで動かすと、
/// 画面の更新などで UI スレッドが忙しいときに（rexRemote を使っていないときでも）Windows 全体の
/// キー入力が遅れる（Alt+Tab が重くなる）。そのため専用スレッドの自前のメッセージループで動かす。
/// ハンドラーはこのスレッドで呼ばれるので、WPF の要素に触れてはいけない。
/// </para>
/// </summary>
internal sealed class LowLevelKeyboardHook : IDisposable
{
    /// <returns>true ならキーを横取りし、Windows や他のアプリには渡さない。</returns>
    public delegate bool KeyHandler(in KeyInfo key);

    public readonly record struct KeyInfo(int VirtualKey, int ScanCode, bool IsDown, bool IsExtended, bool IsInjected);

    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;
    private const uint WM_QUIT = 0x0012;
    private const uint LLKHF_EXTENDED = 0x01;
    private const uint LLKHF_INJECTED = 0x10;

    private readonly KeyHandler _handler;
    private readonly Action<Exception> _onError;
    private readonly HookProc _proc; // GC に回収されないよう保持する
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _started = new();
    private Exception? _startError;
    private uint _threadId;
    private IntPtr _hook;

    public LowLevelKeyboardHook(KeyHandler handler, Action<Exception> onError)
    {
        _handler = handler;
        _onError = onError;
        _proc = Callback;
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "rexRemote keyboard hook",
            Priority = ThreadPriority.Highest, // キー入力を待たせない
        };
        _thread.Start();
        _started.Wait();
        if (_startError is not null)
        {
            throw _startError;
        }
    }

    private void Run()
    {
        _threadId = GetCurrentThreadId();
        _hook = SetWindowsHookExW(WH_KEYBOARD_LL, _proc, GetModuleHandleW(null), 0);
        if (_hook == IntPtr.Zero)
        {
            _startError = new Win32Exception(Marshal.GetLastWin32Error(), "キーボードフックを設定できませんでした。");
            _started.Set();
            return;
        }

        _started.Set();

        // フックのコールバックはこのスレッドのメッセージ処理の中で呼ばれる
        while (GetMessageW(out MSG msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }

        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int message = (int)wParam;
            bool down = message is WM_KEYDOWN or WM_SYSKEYDOWN;
            bool up = message is WM_KEYUP or WM_SYSKEYUP;
            if (down || up)
            {
                var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                var key = new KeyInfo(
                    (int)data.vkCode,
                    (int)data.scanCode,
                    down,
                    (data.flags & LLKHF_EXTENDED) != 0,
                    (data.flags & LLKHF_INJECTED) != 0);
                try
                {
                    if (_handler(key))
                    {
                        return 1; // 横取り
                    }
                }
                catch (Exception ex)
                {
                    // フック内の例外でキーボード全体が効かなくならないよう、キーは素通しする
                    _onError(ex);
                }
            }
        }

        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_threadId != 0 && _thread.IsAlive)
        {
            PostThreadMessageW(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            _thread.Join(TimeSpan.FromSeconds(1));
        }

        _started.Dispose();
    }

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookExW(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessageW(out MSG msg, IntPtr hwnd, uint filterMin, uint filterMax);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref MSG msg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessageW(ref MSG msg);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessageW(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);
}
