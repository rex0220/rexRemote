using System.Windows.Threading;

namespace RexRemote.App;

/// <summary>
/// 診断用：スクロールの入力がどの経路で届いているか（ウィンドウのメッセージ／WPF のイベント）を数え、
/// 操作が途切れたら 1 行だけログに出す。数値だけを記録する。
/// タッチパッドでスクロールできないといった問題の切り分けに使う。
/// ログが増え続けないよう既定ではオフ。環境変数 REXREMOTE_DIAG に "scroll" を含めると有効になる。
/// </summary>
internal sealed class ScrollInputStats
{
    private readonly DispatcherTimer? _timer;

    public int WindowWheel;
    public int WindowHorizontalWheel;
    public int Gesture;
    public int Pointer;
    public int WpfWheel;
    public int Unmapped;
    public int ClicksSent;

    private int _deltaSum;
    private int _minAbsDelta = int.MaxValue;

    public ScrollInputStats(Dispatcher dispatcher)
    {
        Enabled = (Environment.GetEnvironmentVariable("REXREMOTE_DIAG") ?? string.Empty)
            .Contains("scroll", StringComparison.OrdinalIgnoreCase);
        if (Enabled)
        {
            _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(800), DispatcherPriority.Background, (_, _) => Flush(), dispatcher);
            _timer.Stop();
        }
    }

    public bool Enabled { get; }

    /// <summary>入力があった（ログを出すまでの待ち時間を延ばす）。</summary>
    public void Touch()
    {
        if (_timer is null)
        {
            return;
        }

        _timer.Stop();
        _timer.Start();
    }

    public void RecordWheel(int delta)
    {
        WpfWheel++;
        _deltaSum += delta;
        _minAbsDelta = Math.Min(_minAbsDelta, Math.Abs(delta));
        Touch();
    }

    private void Flush()
    {
        _timer?.Stop();
        if (WindowWheel + WindowHorizontalWheel + Gesture + Pointer + WpfWheel == 0)
        {
            return;
        }

        string minDelta = _minAbsDelta == int.MaxValue ? "-" : _minAbsDelta.ToString();
        AppLog.Write(
            $"スクロール入力: WM_MOUSEWHEEL {WindowWheel} / WPF MouseWheel {WpfWheel}（Δ合計 {_deltaSum}、最小|Δ| {minDelta}）" +
            $" / WM_MOUSEHWHEEL {WindowHorizontalWheel} / WM_POINTER {Pointer} / WM_GESTURE {Gesture}" +
            $" / 位置の変換失敗 {Unmapped} / Mac に送った段数 {ClicksSent}");

        WindowWheel = WindowHorizontalWheel = Gesture = Pointer = WpfWheel = Unmapped = ClicksSent = 0;
        _deltaSum = 0;
        _minAbsDelta = int.MaxValue;
    }
}
