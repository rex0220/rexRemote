using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RexRemote.App.Input;
using RexRemote.Protocol;
using RexRemote.Protocol.Input;

namespace RexRemote.App;

public partial class MainWindow : Window
{
    private const int WheelStep = 120;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);

    private readonly AppSettings _settings = AppSettings.Load();
    private RemoteSession? _session;
    private CancellationTokenSource? _connectCts;
    private bool _connectCancelledByUser; // タイムアウトと利用者の中止を区別する
    private string? _lastUserName = Environment.GetEnvironmentVariable("REXREMOTE_USER");

    /// <summary>接続中のセッションで使った資格情報（ロック画面へのパスワード入力用）。切断時に破棄する。</summary>
    private RfbCredentials? _sessionCredentials;

    private enum CredentialSource { Environment, Stored, Dialog }

    private sealed record CredentialRequest(RfbCredentials Credentials, CredentialSource Source, bool Save);

    // キーボード状態
    private readonly KeyboardTranslator _keyboard = new();
    private readonly HashSet<int> _reportedUnmappedKeys = [];
    private LowLevelKeyboardHook? _keyboardHook;

    // マウス状態
    private RfbButtons _buttons;
    private int _wheelAccumulator;
    private (int X, int Y, RfbButtons Buttons) _lastPointer = (-1, -1, RfbButtons.None);

    // 表示
    private DisplayScale _scale;
    private bool _initialized;

    public MainWindow()
    {
        InitializeComponent();

        // 前回選んだ表示倍率と設定
        _scale = _settings.Scale;
        ScaleModeBox.SelectedIndex = (int)_scale;
        InitializeSettingsPanel();
        _initialized = true;
        ApplyScaleMode();

        // 接続先の履歴。最後に接続した接続先を選んだ状態で始める（起動引数があればそちらで上書きされる）
        RefreshConnectionHistory();

        Loaded += async (_, _) => await AutoConnectFromCommandLineAsync();
    }

    /// <summary>履歴をホスト欄の一覧に反映し、先頭（最後に接続した接続先）を選ぶ。</summary>
    private void RefreshConnectionHistory()
    {
        HostBox.ItemsSource = _settings.RecentConnections.ToList();
        if (_settings.RecentConnections.Count > 0)
        {
            HostBox.SelectedIndex = 0; // OnHostSelectionChanged でポート欄も切り替わる
        }
    }

    private void OnHostSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (HostBox.SelectedItem is ConnectionEntry entry)
        {
            PortBox.Text = entry.Port.ToString();
        }
    }

    /// <summary>
    /// 履歴の「✕」。パスワードを保存している接続先なら、パスワードも消すか確認する
    /// （はい: 両方削除 / いいえ: 履歴だけ削除 / キャンセル: 何もしない）。
    /// </summary>
    private void OnRemoveHistoryClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.Tag is not ConnectionEntry entry)
        {
            return;
        }

        bool deletePassword = false;
        if (CredentialStore.Exists(entry.Host, entry.Port))
        {
            MessageBoxResult answer = MessageBox.Show(this,
                $"「{entry}」を履歴から削除します。\n保存しているパスワードも削除しますか？",
                "rexRemote - 履歴から削除", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            // キャンセルのほか、ダイアログが閉じられた・ウィンドウごと閉じられた（None）場合も何もしない
            if (answer is not (MessageBoxResult.Yes or MessageBoxResult.No))
            {
                return;
            }

            deletePassword = answer == MessageBoxResult.Yes;
        }

        // 入力中のホスト・ポートは残したまま一覧だけ更新する
        string host = HostBox.Text;
        string port = PortBox.Text;

        _settings.RemoveRecentConnection(entry.Host, entry.Port);
        _settings.Save();
        if (deletePassword)
        {
            CredentialStore.Delete(entry.Host, entry.Port);
        }

        HostBox.ItemsSource = _settings.RecentConnections.ToList();
        HostBox.Text = host;
        PortBox.Text = port;
        SetStatus(deletePassword ? $"「{entry}」を履歴と保存したパスワードから削除しました。" : $"「{entry}」を履歴から削除しました。");
    }

    /// <summary>起動引数 <c>host[:port]</c> があれば、その接続先にすぐ接続する。</summary>
    private async Task AutoConnectFromCommandLineAsync()
    {
        string[] args = Environment.GetCommandLineArgs();
        if (args.Length < 2)
        {
            return;
        }

        if (!TryParseTarget(args[1], out string host, out int port))
        {
            SetStatus($"起動引数を解釈できません: {args[1]}（host[:port] の形式で指定）");
            return;
        }

        HostBox.Text = host;
        PortBox.Text = port.ToString();
        await ConnectAsync();
    }

    internal static bool TryParseTarget(string target, out string host, out int port)
    {
        host = target.Trim();
        port = 5900;
        int colon = host.LastIndexOf(':');
        if (host.StartsWith('['))
        {
            // [IPv6]:port
            int close = host.IndexOf(']');
            if (close < 0)
            {
                return false;
            }

            string rest = host[(close + 1)..];
            host = host[1..close];
            return rest.Length == 0 || (rest[0] == ':' && int.TryParse(rest[1..], out port) && port is > 0 and <= 65535);
        }

        if (colon >= 0 && host.IndexOf(':') == colon)
        {
            if (!int.TryParse(host[(colon + 1)..], out port) || port is < 1 or > 65535)
            {
                return false;
            }

            host = host[..colon];
        }

        return host.Length > 0;
    }

    // ------------------------------------------------------------------
    // 接続・切断
    // ------------------------------------------------------------------

    private async void OnConnectClick(object sender, RoutedEventArgs e)
    {
        if (_reconnectCts is not null)
        {
            // 自動再接続の待機中・試行中なら中止
            _connectCancelledByUser = true;
            _connectCts?.Cancel();
            StopReconnect();
            ResetConnectionUi();
            SetStatus("再接続を中止しました。");
            return;
        }

        if (_connectCts is not null)
        {
            _connectCancelledByUser = true; // 接続処理中なら中止
            _connectCts.Cancel();
            return;
        }

        if (_session is not null)
        {
            await DisconnectAsync();
            return;
        }

        await ConnectAsync();
    }

    private async Task ConnectAsync()
    {
        string host = HostBox.Text.Trim();
        if (host.Length == 0 || !int.TryParse(PortBox.Text.Trim(), out int port) || port is < 1 or > 65535)
        {
            MessageBox.Show(this, "ホストとポート（1〜65535）を正しく入力してください。", "rexRemote", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        CredentialRequest? request = GetCredentials(host, port);
        if (request is null)
        {
            return;
        }

        await ConnectCoreAsync(host, port, request, isReconnect: false);
    }

    /// <summary>接続する（利用者の操作と自動再接続の共通処理）。成功したら true。</summary>
    /// <param name="isReconnect">自動再接続のとき true（失敗してもダイアログは出さず、呼び出し側が再試行する）。</param>
    private async Task<bool> ConnectCoreAsync(string host, int port, CredentialRequest request, bool isReconnect)
    {
        RfbCredentials credentials = request.Credentials;
        using var cts = new CancellationTokenSource(ConnectTimeout);
        _connectCts = cts;
        _connectCancelledByUser = false;
        SetConnectButtonState(ConnectButtonState.Connecting);
        HostBox.IsEnabled = PortBox.IsEnabled = false;

        try
        {
            RemoteSession session = await RemoteSession.ConnectAsync(
                host, port, credentials, ClipboardEncoding, Dispatcher, Log, cts.Token);
            _session = session;
            session.BitmapChanged += OnBitmapChanged;
            session.Disconnected += error => OnSessionDisconnected(session, error);
            session.CursorChanged += OnRemoteCursorChanged;
            session.ServerClipboardChanged += OnServerClipboardChanged;
            session.Scale = _scale; // Start 前に決めておく（最初のビットマップがこの倍率で作られる）
            session.Start();
            _lastConnection = (host, port, request with { Save = false });
            InstallKeyboardHook();
            _sessionCredentials = credentials;
            TypePasswordButton.IsEnabled = true;
            ScreenshotButton.IsEnabled = true;

            // 接続に成功した接続先を履歴の先頭に入れる（次回起動時の初期値になる）
            _settings.AddRecentConnection(host, port);
            _settings.Save();
            RefreshConnectionHistory();

            // 認証に成功したときだけ保存する
            if (request.Save)
            {
                try
                {
                    CredentialStore.Save(host, port, credentials);
                    AppLog.Write($"資格情報を保存しました（{CredentialStore.TargetName(host, port)}）");
                }
                catch (Exception ex)
                {
                    AppLog.Write($"資格情報を保存できませんでした: {ex.Message}");
                }
            }

            RfbClient client = session.Client;
            ServerInit init = client.ServerInit!;
            Title = $"rexRemote - {init.Name}";
            SetStatus($"接続中: {init.Name}  {init.Width}x{init.Height}  ({client.ServerVersion}, 認証タイプ {(byte)client.SecurityType})");
            SetConnectButtonState(ConnectButtonState.Connected);
            RemoteImage.Focus();
            return true;
        }
        catch (OperationCanceledException)
        {
            SetStatus(_connectCancelledByUser ? "接続を中止しました。" : "接続がタイムアウトしました。");
            ResetConnectionUi();
            return false;
        }
        catch (RfbAuthenticationException ex)
        {
            // パスワード違いは何度試しても同じなので、自動再接続もやめる
            StopReconnect();
            ResetConnectionUi();
            if (request.Source == CredentialSource.Stored)
            {
                // 保存したパスワードが古い（Mac 側で変更したなど）。削除して次回はダイアログで尋ねる
                CredentialStore.Delete(host, port);
                Log($"保存したパスワードで認証に失敗したため削除しました: {ex.Message}");
                MessageBox.Show(this, $"{ex.Message}\n\n保存していたパスワードを削除しました。もう一度接続すると入力を求めます。",
                    "rexRemote - 認証失敗", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else
            {
                Log($"接続失敗: {ex.Message}");
                MessageBox.Show(this, ex.Message, "rexRemote - 認証失敗", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            return false;
        }
        catch (Exception ex)
        {
            Log($"接続失敗: {ex.GetType().Name}: {ex.Message}");
            SetStatus($"接続失敗: {ex.Message}");
            ResetConnectionUi();
            if (!isReconnect)
            {
                MessageBox.Show(this, ex.Message, "rexRemote - 接続失敗", MessageBoxButton.OK, MessageBoxImage.Error);
            }

            return false;
        }
        finally
        {
            _connectCts = null;
        }
    }

    // ------------------------------------------------------------------
    // 自動再接続
    // ------------------------------------------------------------------

    /// <summary>再接続までの待ち時間（秒）。回数はこの配列の長さ。</summary>
    private static readonly int[] ReconnectDelaysSeconds = [2, 4, 8, 15, 30, 30, 30, 60, 60, 60];

    private CancellationTokenSource? _reconnectCts;
    private (string Host, int Port, CredentialRequest Request)? _lastConnection;

    /// <summary>回線の瞬断などで切れたとき、前回と同じ接続先・資格情報で間隔を空けながらつなぎ直す。</summary>
    private async void StartReconnect(Exception error)
    {
        if (_lastConnection is not { } target)
        {
            return;
        }

        StopReconnect();
        var cts = new CancellationTokenSource();
        _reconnectCts = cts;

        for (int attempt = 1; attempt <= ReconnectDelaysSeconds.Length; attempt++)
        {
            int delay = ReconnectDelaysSeconds[attempt - 1];
            SetConnectButtonState(ConnectButtonState.Reconnecting);
            HostBox.IsEnabled = PortBox.IsEnabled = false;
            SetStatus($"切断されました（{error.Message}）。{delay} 秒後に再接続します（{attempt}/{ReconnectDelaysSeconds.Length}）…");
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(delay), cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            Log($"再接続しています（{attempt}/{ReconnectDelaysSeconds.Length}）…");
            if (await ConnectCoreAsync(target.Host, target.Port, target.Request, isReconnect: true))
            {
                AppLog.Write($"再接続しました（{attempt} 回目）");
                if (ReferenceEquals(_reconnectCts, cts))
                {
                    _reconnectCts = null;
                }

                return;
            }

            if (cts.IsCancellationRequested)
            {
                return; // 中止された、または認証失敗で打ち切った
            }
        }

        _reconnectCts = null;
        ResetConnectionUi();
        SetStatus("再接続できませんでした。");
        MessageBox.Show(this, $"再接続できませんでした。\n\n最後のエラー: {error.Message}", "rexRemote - 切断",
            MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void StopReconnect()
    {
        _reconnectCts?.Cancel();
        _reconnectCts = null;
    }

    /// <summary>
    /// 資格情報を決める。優先順: 環境変数 REXREMOTE_USER / REXREMOTE_PASSWORD → 資格情報マネージャーに保存したもの → ダイアログ。
    /// </summary>
    private CredentialRequest? GetCredentials(string host, int port)
    {
        string? user = Environment.GetEnvironmentVariable("REXREMOTE_USER");
        string? password = Environment.GetEnvironmentVariable("REXREMOTE_PASSWORD");
        if (!string.IsNullOrEmpty(password))
        {
            Log("資格情報: 環境変数を使用");
            return new CredentialRequest(new RfbCredentials(string.IsNullOrEmpty(user) ? null : user, password), CredentialSource.Environment, Save: false);
        }

        if (CredentialStore.Load(host, port) is { } stored)
        {
            Log("資格情報: 保存したものを使用");
            return new CredentialRequest(stored, CredentialSource.Stored, Save: false);
        }

        var dialog = new CredentialsDialog($"{host}:{port}", _lastUserName) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Credentials is null)
        {
            return null;
        }

        _lastUserName = dialog.Credentials.UserName;
        return new CredentialRequest(dialog.Credentials, CredentialSource.Dialog, dialog.SavePassword);
    }

    /// <summary>
    /// 接続に使ったパスワードを Mac に入力して Enter を押す（ロック画面・ログイン画面用）。
    /// ロック中かどうかは RFB では分からないので、自動では行わず利用者がボタンで指示したときだけ送る。
    /// </summary>
    private void OnTypePasswordClick(object sender, RoutedEventArgs e)
    {
        if (_session is not { } session || _sessionCredentials is not { } credentials)
        {
            return;
        }

        ReleaseAllKeys();
        foreach (KeyAction action in TextTyping.ToKeyActions(credentials.Password, pressReturn: true))
        {
            session.Client.SendKeyEvent(action.Down, action.Keysym);
        }

        RemoteImage.Focus();
    }

    private async Task DisconnectAsync()
    {
        RemoteSession? session = _session;
        if (session is null)
        {
            return;
        }

        _session = null;
        ConnectButton.IsEnabled = false;
        await session.DisposeAsync();
        ConnectButton.IsEnabled = true;
        SetStatus("切断しました。");
        ResetConnectionUi();
    }

    private void OnSessionDisconnected(RemoteSession session, Exception? error)
    {
        if (!ReferenceEquals(session, _session))
        {
            return; // 利用者が切断済み
        }

        _session = null;
        _ = session.DisposeAsync().AsTask();
        ResetConnectionUi();

        if (error is null)
        {
            SetStatus("切断されました。");
            return;
        }

        Log($"切断: {error.GetType().Name}: {error.Message}");
        if (_settings.AutoReconnect && _lastConnection is not null && error is not RfbAuthenticationException)
        {
            StartReconnect(error);
            return;
        }

        SetStatus($"切断されました: {error.Message}");
        MessageBox.Show(this, error.Message, "rexRemote - 切断", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void ResetConnectionUi()
    {
        SetConnectButtonState(ConnectButtonState.Idle);
        HostBox.IsEnabled = PortBox.IsEnabled = true;
        Title = "rexRemote";
        _sessionCredentials = null;
        TypePasswordButton.IsEnabled = false;
        ScreenshotButton.IsEnabled = false;
        UninstallKeyboardHook();
        ResetRemoteCursor();
        _buttons = RfbButtons.None;
        _lastPointer = (-1, -1, RfbButtons.None);
        if (RemoteImage.IsMouseCaptured)
        {
            RemoteImage.ReleaseMouseCapture();
        }
    }

    private enum ConnectButtonState { Idle, Connecting, Connected, Reconnecting }

    // Segoe Fluent Icons / Segoe MDL2 Assets のコードポイント
    private static readonly string IconConnect = ((char)0xE703).ToString();
    private static readonly string IconCancel = ((char)0xE711).ToString();
    private static readonly string IconPower = ((char)0xE7E8).ToString();

    /// <summary>接続ボタンの文字・アイコン・色を状態に合わせる。主要操作の「接続」だけ強調色にする。</summary>
    private void SetConnectButtonState(ConnectButtonState state)
    {
        (string text, string icon, string style) = state switch
        {
            ConnectButtonState.Connecting or ConnectButtonState.Reconnecting => ("中止", IconCancel, "ModernButton"),
            ConnectButtonState.Connected => ("切断", IconPower, "ModernButton"),
            _ => ("接続", IconConnect, "AccentButton"),
        };
        ConnectText.Text = text;
        ConnectIcon.Text = icon;
        ConnectButton.Style = (Style)FindResource(style);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        StopReconnect();
        RemoveClipboardListener();
        _connectCancelledByUser = true;
        _connectCts?.Cancel();
        ReleaseAllKeys();
        UninstallKeyboardHook();
        _session?.Abort();
        base.OnClosing(e);
    }

    private void Log(string message)
    {
        AppLog.Write(message);
        if (Dispatcher.CheckAccess())
        {
            SetStatus(message);
        }
        else
        {
            Dispatcher.BeginInvoke(() =>
            {
                // 接続確立後は受信ループのログでステータスを上書きしない
                if (_session is null)
                {
                    SetStatus(message);
                }
            });
        }
    }

    private void SetStatus(string text) => StatusText.Text = text;

    // ------------------------------------------------------------------
    // スクリーンショット
    // ------------------------------------------------------------------

    /// <summary>
    /// 表示中の Mac の画面を元の解像度で PNG に保存し、クリップボードにもコピーする。
    /// 32bpp のままだとクリップボード経由で透明扱いになるアプリがあるので 24bpp にする。
    /// </summary>
    private async void OnScreenshotClick(object sender, RoutedEventArgs e)
    {
        if (_session is not { Bitmap: not null } session)
        {
            return;
        }

        // UI スレッドでその瞬間の画面（表示倍率に関係なく元の解像度）を固定し、PNG 化は別スレッドで行う（4K だと時間がかかる）
        var snapshot = new FormatConvertedBitmap(session.CreateFullResolutionSnapshot(), PixelFormats.Bgr24, null, 0);
        snapshot.Freeze();

        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "rexRemote");
        string file = Path.Combine(folder, $"rexRemote-{DateTime.Now:yyyyMMdd-HHmmss}.png");

        ScreenshotButton.IsEnabled = false;
        try
        {
            await Task.Run(() =>
            {
                Directory.CreateDirectory(folder);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(snapshot));
                using var stream = File.Create(file);
                encoder.Save(stream);
            });

            string clipboard = "クリップボードにもコピーしました";
            try
            {
                Clipboard.SetImage(snapshot);
            }
            catch (ExternalException) // COMException（CLIPBRD_E_CANT_OPEN など）
            {
                clipboard = "クリップボードへのコピーは失敗しました（他のアプリが使用中）";
            }

            SetStatus($"スクリーンショットを保存しました: {file}（{clipboard}）");
            AppLog.Write($"スクリーンショット: {file}");
        }
        catch (Exception ex)
        {
            SetStatus($"スクリーンショットを保存できませんでした: {ex.Message}");
        }
        finally
        {
            ScreenshotButton.IsEnabled = _session is not null;
        }
    }

    // ------------------------------------------------------------------
    // 表示
    // ------------------------------------------------------------------

    private void OnBitmapChanged(WriteableBitmap bitmap)
    {
        RemoteImage.Source = bitmap;
        ApplyScaleMode();
    }

    private void OnScaleModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized || ScaleModeBox.SelectedItem is not ComboBoxItem { Tag: string tag }
            || !Enum.TryParse(tag, out DisplayScale scale) || scale == _scale)
        {
            return;
        }

        _scale = scale;
        _settings.Scale = scale;
        _settings.Save();
        if (_session is not null)
        {
            _session.Scale = scale; // 50% との切り替えではビットマップが作り直され、OnBitmapChanged が呼ばれる
            RemoteImage.Focus();
        }

        ApplyScaleMode();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        ApplyScaleMode();
        UpdateRemoteCursor();
    }

    /// <summary>
    /// ウィンドウに合わせる: 縦横比を保って WPF が拡大縮小。
    /// 50% / 100%: ビットマップの 1 ピクセル = 1 デバイスピクセル（Windows の表示スケールを打ち消す）でスクロール表示。
    /// 50% のビットマップは RemoteSession が 1/2 に縮小済み。
    /// </summary>
    private void ApplyScaleMode()
    {
        if (RemoteImage is null || Scroller is null)
        {
            return; // InitializeComponent 中
        }

        if (_scale == DisplayScale.Fit)
        {
            Scroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            Scroller.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
            RemoteImage.Stretch = Stretch.Uniform;
            RemoteImage.Width = double.NaN;
            RemoteImage.Height = double.NaN;
            RenderOptions.SetBitmapScalingMode(RemoteImage, BitmapScalingMode.HighQuality);
        }
        else
        {
            Scroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
            Scroller.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            RemoteImage.Stretch = Stretch.Fill;
            if (RemoteImage.Source is BitmapSource bitmap)
            {
                DpiScale dpi = VisualTreeHelper.GetDpi(this);
                RemoteImage.Width = bitmap.PixelWidth / dpi.DpiScaleX;
                RemoteImage.Height = bitmap.PixelHeight / dpi.DpiScaleY;
            }

            RenderOptions.SetBitmapScalingMode(RemoteImage, BitmapScalingMode.NearestNeighbor);
        }
    }

    // ------------------------------------------------------------------
    // キーボード
    // ------------------------------------------------------------------

    // フックは専用スレッドで動くので、フックが読む状態は UI スレッドがここに書いておく
    private volatile RemoteSession? _hookSession;   // キーを送る先（接続中のみ）
    private volatile bool _captureKeyboard;          // ウィンドウがアクティブで、リモート画面にキーボードフォーカスがある
    private uint _uiThreadId;                        // キーボード配列を取るための UI スレッドの Win32 スレッド ID

    private void InstallKeyboardHook()
    {
        _hookSession = _session;
        UpdateKeyboardCapture();
        if (_keyboardHook is not null)
        {
            return;
        }

        _uiThreadId = GetCurrentThreadId();
        try
        {
            _keyboardHook = new LowLevelKeyboardHook(
                OnKeyboardHook,
                ex => AppLog.Write($"キーボードフックでエラー: {ex.GetType().Name}: {ex.Message}"));
        }
        catch (Exception ex)
        {
            Log($"キーボードを送れません: {ex.Message}");
        }
    }

    private void UninstallKeyboardHook()
    {
        _hookSession = null;
        _captureKeyboard = false;
        lock (_keyboard)
        {
            _keyboard.ReleaseAll(); // 切断後なので送らずに状態だけ消す
        }

        _keyboardHook?.Dispose();
        _keyboardHook = null;
    }

    /// <summary>キーを Mac に送るかどうかの判定を更新する（UI スレッドから呼ぶ）。</summary>
    private void UpdateKeyboardCapture() =>
        _captureKeyboard = _hookSession is not null && IsActive && RemoteImage.IsKeyboardFocused;

    /// <summary>
    /// 低レベルフックから呼ばれる（<b>フック専用スレッド</b>。WPF の要素には触れない）。
    /// リモート画面にフォーカスがある間は、対応するキーをすべて横取りして Mac に送る
    /// （Win キー・Alt+Tab・半角/全角も Windows には渡さない）。入力内容はログに出さない。
    /// </summary>
    private bool OnKeyboardHook(in LowLevelKeyboardHook.KeyInfo key)
    {
        RemoteSession? session = _hookSession;
        if (session is null || WindowsKeyMap.IsPassThrough(key.VirtualKey))
        {
            return false;
        }

        lock (_keyboard)
        {
            if (!key.IsDown)
            {
                // フォーカスが外れていても、押下を送ったキーの解放は必ず送る
                if (_keyboard.KeyUp(key.VirtualKey) is not uint released)
                {
                    return false;
                }

                session.Client.SendKeyEvent(false, released);
                return true;
            }

            if (!_captureKeyboard)
            {
                return false;
            }

            string? text = WindowsKeyMap.MapSpecialKey(key.VirtualKey, key.IsExtended) is null
                ? KeyboardLayout.GetText(key.VirtualKey, key.ScanCode, _keyboard.IsShiftDown, KeyboardLayout.GetLayoutOfThread(_uiThreadId))
                : null;

            if (_keyboard.KeyDown(key.VirtualKey, key.IsExtended, text) is not uint keysym)
            {
                // 未対応のキー（音量キーなど）は Windows に任せる。キーコードだけ記録する（文字は記録しない）
                if (_reportedUnmappedKeys.Add(key.VirtualKey))
                {
                    AppLog.Write($"未対応のキー: VK 0x{key.VirtualKey:X2}");
                }

                return false;
            }

            session.Client.SendKeyEvent(true, keysym);
            return true;
        }
    }

    /// <summary>押下中のキーをすべて Mac 側で離す（フォーカスを失ったとき用）。</summary>
    private void ReleaseAllKeys()
    {
        IReadOnlyList<uint> released;
        lock (_keyboard)
        {
            released = _keyboard.ReleaseAll();
        }

        if (_session is { } session)
        {
            foreach (uint keysym in released)
            {
                session.Client.SendKeyEvent(false, keysym);
            }
        }
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        UpdateKeyboardCapture();
    }

    protected override void OnDeactivated(EventArgs e)
    {
        _captureKeyboard = false;
        ReleaseAllKeys();
        base.OnDeactivated(e);
    }

    private void OnRemoteGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => UpdateKeyboardCapture();

    private void OnRemoteLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        _captureKeyboard = false;
        ReleaseAllKeys();
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    // ------------------------------------------------------------------
    // 設定パネル
    // ------------------------------------------------------------------

    private void InitializeSettingsPanel()
    {
        SwapCtrlCmdCheckBox.IsChecked = _settings.SwapControlAndCommand;
        ClipboardCheckBox.IsChecked = _settings.ClipboardSharing;
        AutoReconnectCheckBox.IsChecked = _settings.AutoReconnect;
        lock (_keyboard)
        {
            _keyboard.SwapControlAndCommand = _settings.SwapControlAndCommand;
        }

        // 開いている間にボタンを押すと「閉じる → すぐ開く」になるので、開いている間はボタンを押せなくする
        SettingsPopup.Opened += (_, _) => SettingsButton.IsHitTestVisible = false;
        SettingsPopup.Closed += (_, _) =>
        {
            SettingsButton.IsHitTestVisible = true;
            if (_session is not null)
            {
                RemoteImage.Focus();
            }
        };
    }

    private void OnHelpClick(object sender, RoutedEventArgs e)
    {
        new HelpWindow { Owner = this }.ShowDialog();
        if (_session is not null)
        {
            RemoteImage.Focus();
        }
    }

    private void OnSettingToggled(object sender, RoutedEventArgs e)
    {
        if (!_initialized)
        {
            return;
        }

        _settings.SwapControlAndCommand = SwapCtrlCmdCheckBox.IsChecked == true;
        _settings.ClipboardSharing = ClipboardCheckBox.IsChecked == true;
        _settings.AutoReconnect = AutoReconnectCheckBox.IsChecked == true;
        _settings.Save();
        lock (_keyboard)
        {
            _keyboard.SwapControlAndCommand = _settings.SwapControlAndCommand;
        }
    }

    // ------------------------------------------------------------------
    // クリップボード共有
    // ------------------------------------------------------------------

    private const int WM_CLIPBOARDUPDATE = 0x031D;
    private const int MaxClipboardLength = 1024 * 1024; // 文字数。大きすぎる文字列は送らない

    /// <summary>
    /// ClientCutText の文字コード。RFB 標準は Latin-1 だが、それでは日本語が送れないので UTF-8 にする
    /// （Mac 側の解釈は実機で確認する。CLAUDE.md の実機調査メモ）。
    /// </summary>
    private static readonly Encoding ClipboardEncoding = Encoding.UTF8;

    private HwndSource? _hwndSource;

    // 往復（Mac から受けた文字列を Windows に入れた → その変更通知でまた Mac に送る）を防ぐため、最後にやり取りした文字列（改行は LF）
    private string? _lastClipboardFromMac;
    private string? _lastClipboardToMac;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwndSource = (HwndSource)PresentationSource.FromVisual(this);
        _hwndSource.AddHook(WndProc);
        AddClipboardFormatListener(_hwndSource.Handle);
    }

    private void RemoveClipboardListener()
    {
        if (_hwndSource is not null)
        {
            RemoveClipboardFormatListener(_hwndSource.Handle);
            _hwndSource.RemoveHook(WndProc);
            _hwndSource = null;
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_CLIPBOARDUPDATE)
        {
            OnWindowsClipboardChanged();
        }

        return IntPtr.Zero;
    }

    /// <summary>Windows でコピーされた文字列を Mac に送る。内容はログに出さない（文字数だけ）。</summary>
    private void OnWindowsClipboardChanged()
    {
        if (!_settings.ClipboardSharing || _session is not { } session)
        {
            return;
        }

        string? text = TryGetClipboardText();
        if (text is null || text.Length > MaxClipboardLength)
        {
            return;
        }

        string normalized = NormalizeNewlines(text);
        if (normalized == _lastClipboardFromMac || normalized == _lastClipboardToMac)
        {
            return; // Mac から受け取ったものを入れただけ、または送信済み
        }

        _lastClipboardToMac = normalized;
        session.Client.SendClientCutText(normalized);
        AppLog.Write($"クリップボード → Mac（{normalized.Length} 文字）");
    }

    /// <summary>Mac でコピーされた文字列を Windows のクリップボードに入れる。</summary>
    private void OnServerClipboardChanged(string text)
    {
        if (!_settings.ClipboardSharing)
        {
            return;
        }

        string normalized = NormalizeNewlines(text);
        if (normalized == _lastClipboardToMac || normalized == _lastClipboardFromMac)
        {
            return; // こちらから送ったものが返ってきた
        }

        _lastClipboardFromMac = normalized;
        if (TrySetClipboardText(normalized.Replace("\n", "\r\n")))
        {
            AppLog.Write($"クリップボード ← Mac（{normalized.Length} 文字）");
        }
    }

    private static string NormalizeNewlines(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n');

    /// <summary>クリップボードは他のアプリが使用中だと開けないことがあるので、少し待って何度か試す。</summary>
    private static string? TryGetClipboardText()
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                return Clipboard.ContainsText() ? Clipboard.GetText() : null;
            }
            catch (ExternalException)
            {
                Thread.Sleep(30);
            }
        }

        return null;
    }

    private static bool TrySetClipboardText(string text)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                return true;
            }
            catch (ExternalException)
            {
                Thread.Sleep(30);
            }
        }

        AppLog.Write("Mac のクリップボードを Windows に入れられませんでした（他のアプリが使用中）");
        return false;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    // ------------------------------------------------------------------
    // カーソル（Mac から形を受け取り、手元で描く）
    // ------------------------------------------------------------------

    private CursorShape? _remoteCursorShape;
    private Cursor? _remoteCursor;
    private (CursorShape Shape, int Width, int Height)? _remoteCursorKey;

    private void OnRemoteCursorChanged(CursorShape shape)
    {
        if (_remoteCursorShape is null)
        {
            AppLog.Write($"Mac からカーソル形状を受信しました（{shape.Width}x{shape.Height}）。カーソルは手元で描きます");
        }

        _remoteCursorShape = shape;
        UpdateRemoteCursor();
    }

    private void OnRemoteImageSizeChanged(object sender, SizeChangedEventArgs e) => UpdateRemoteCursor();

    /// <summary>
    /// カーソルを Windows の表示スケールに合わせた大きさで作り直す（大きさが変わらなければ何もしない）。
    /// Mac はカーソルを Retina の 2 倍ではなく等倍（矢印で 23x22 程度）で送ってくる（実機で確認）ので、
    /// 画面の縮小率ではなく Windows の DPI 倍率で拡大し、Windows の普段のカーソルと同じくらいの大きさにする。
    /// </summary>
    private void UpdateRemoteCursor()
    {
        if (_remoteCursorShape is not { } shape || _session is null)
        {
            return;
        }

        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        if (shape.Width > 48 || shape.Height > 48)
        {
            scale /= 2; // 2 倍の大きさで届いたとみなす
        }

        int width = (int)Math.Round(shape.Width * scale);
        int height = (int)Math.Round(shape.Height * scale);
        if (_remoteCursorKey is { } key && ReferenceEquals(key.Shape, shape) && key.Width == width && key.Height == height)
        {
            return;
        }

        Cursor next = RemoteCursorFactory.Create(shape, scale);
        RemoteImage.Cursor = next;
        DisposeRemoteCursor();
        _remoteCursor = next;
        _remoteCursorKey = (shape, width, height);
    }

    private void ResetRemoteCursor()
    {
        RemoteImage.Cursor = null;
        DisposeRemoteCursor();
        _remoteCursorShape = null;
        _remoteCursorKey = null;
    }

    private void DisposeRemoteCursor()
    {
        // Cursors.None などの共有カーソルは Dispose しない
        if (_remoteCursor is not null && _remoteCursor != Cursors.None && _remoteCursor != Cursors.Arrow)
        {
            _remoteCursor.Dispose();
        }

        _remoteCursor = null;
    }

    // ------------------------------------------------------------------
    // マウス
    // ------------------------------------------------------------------

    private void OnRemoteMouseMove(object sender, MouseEventArgs e) => SendPointer(e, _buttons);

    private void OnRemoteMouseDown(object sender, MouseButtonEventArgs e)
    {
        RemoteImage.Focus();
        RfbButtons button = MapButton(e.ChangedButton);
        if (button == RfbButtons.None)
        {
            return;
        }

        _buttons |= button;
        RemoteImage.CaptureMouse(); // ドラッグがウィンドウ外に出ても追従する
        SendPointer(e, _buttons);
        e.Handled = true;
    }

    private void OnRemoteMouseUp(object sender, MouseButtonEventArgs e)
    {
        RfbButtons button = MapButton(e.ChangedButton);
        if (button == RfbButtons.None)
        {
            return;
        }

        _buttons &= ~button;
        SendPointer(e, _buttons);
        if (_buttons == RfbButtons.None)
        {
            RemoteImage.ReleaseMouseCapture();
        }

        e.Handled = true;
    }

    private void OnRemoteMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!TryMapPoint(e, out int x, out int y))
        {
            return;
        }

        // 高分解能ホイール（タッチパッド）は 120 単位に貯めてから送る。ホイールは押して離すで 1 段
        _wheelAccumulator += e.Delta;
        while (Math.Abs(_wheelAccumulator) >= WheelStep)
        {
            RfbButtons wheel = _wheelAccumulator > 0 ? RfbButtons.WheelUp : RfbButtons.WheelDown;
            _session!.Client.SendPointerEvent(_buttons | wheel, x, y);
            _session.Client.SendPointerEvent(_buttons, x, y);
            _wheelAccumulator -= Math.Sign(_wheelAccumulator) * WheelStep;
        }

        e.Handled = true;
    }

    private void OnRemoteLostMouseCapture(object sender, MouseEventArgs e)
    {
        // Alt+Tab などでキャプチャを失ったらボタンを離したことにする
        if (_buttons != RfbButtons.None)
        {
            _buttons = RfbButtons.None;
            SendPointer(e, _buttons, force: true);
        }
    }

    private void SendPointer(MouseEventArgs e, RfbButtons buttons, bool force = false)
    {
        if (!TryMapPoint(e, out int x, out int y))
        {
            return;
        }

        if (!force && _lastPointer == (x, y, buttons))
        {
            return;
        }

        _lastPointer = (x, y, buttons);
        _session!.Client.SendPointerEvent(buttons, x, y);
    }

    /// <summary>Image 上の位置をリモートのピクセル座標に変換する（表示倍率に依存しない）。</summary>
    private bool TryMapPoint(MouseEventArgs e, out int x, out int y)
    {
        x = y = 0;
        // 50% 表示ではビットマップが半分の大きさなので、Mac の画面の大きさ（RemoteWidth/Height）で換算する
        if (_session is not { Bitmap: not null, RemoteWidth: > 0, RemoteHeight: > 0 } session
            || RemoteImage.ActualWidth <= 0 || RemoteImage.ActualHeight <= 0)
        {
            return false;
        }

        Point p = e.GetPosition(RemoteImage);
        x = Math.Clamp((int)(p.X * session.RemoteWidth / RemoteImage.ActualWidth), 0, session.RemoteWidth - 1);
        y = Math.Clamp((int)(p.Y * session.RemoteHeight / RemoteImage.ActualHeight), 0, session.RemoteHeight - 1);
        return true;
    }

    private static RfbButtons MapButton(MouseButton button) => button switch
    {
        MouseButton.Left => RfbButtons.Left,
        MouseButton.Middle => RfbButtons.Middle,
        MouseButton.Right => RfbButtons.Right,
        _ => RfbButtons.None,
    };
}
