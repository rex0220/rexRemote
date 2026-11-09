using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RexRemote.Protocol;

namespace RexRemote.App;

/// <summary>
/// 1 回分の接続。TCP 接続・<see cref="RfbClient"/>・表示用の <see cref="WriteableBitmap"/> を持つ。
/// 受信ループはバックグラウンドで回し、描画は Dispatcher 経由で UI スレッドに渡す。
/// </summary>
internal sealed class RemoteSession : IRfbEventHandler, IAsyncDisposable
{
    private const int UnionThreshold = 32;

    private readonly TcpClient _tcp;
    private readonly Dispatcher _dispatcher;
    private readonly CancellationTokenSource _cts = new();
    private Task? _runTask;

    private RemoteSession(TcpClient tcp, RfbClient client, Dispatcher dispatcher)
    {
        _tcp = tcp;
        Client = client;
        _dispatcher = dispatcher;
    }

    public RfbClient Client { get; }

    /// <summary>表示中のビットマップ（UI スレッド専用）。</summary>
    public WriteableBitmap? Bitmap { get; private set; }

    /// <summary>ビットマップを作り直した（UI スレッドで発生）。</summary>
    public event Action<WriteableBitmap>? BitmapChanged;

    /// <summary>切断された（UI スレッドで発生）。引数はエラー。利用者による切断なら null。</summary>
    public event Action<Exception?>? Disconnected;

    /// <summary>Mac のカーソルの形が変わった（UI スレッドで発生）。</summary>
    public event Action<CursorShape>? CursorChanged;

    /// <summary>Mac のクリップボードが変わった（UI スレッドで発生。改行は LF）。</summary>
    public event Action<string>? ServerClipboardChanged;

    public static async Task<RemoteSession> ConnectAsync(
        string host, int port, RfbCredentials credentials, Encoding clipboardEncoding,
        Dispatcher dispatcher, Action<string> log, CancellationToken ct)
    {
        var tcp = new TcpClient { NoDelay = true };
        try
        {
            log($"{host}:{port} に接続しています…");
            await tcp.ConnectAsync(host, port, ct);
            EnableKeepAlive(tcp.Client);
            var options = new RfbClientOptions { Log = log, ClipboardEncoding = clipboardEncoding };
            var client = new RfbClient(tcp.GetStream(), options);
            await client.HandshakeAsync(credentials, ct);
            return new RemoteSession(tcp, client, dispatcher);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    /// <summary>
    /// TCP キープアライブ。回線が黙って切れた（Wi-Fi の切り替えや Mac のスリープなど）ときに、
    /// 受信待ちのまま止まらず 30 秒ほどで切断を検知して自動再接続に移れるようにする。
    /// </summary>
    private static void EnableKeepAlive(Socket socket)
    {
        try
        {
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 15);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 5);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 3);
        }
        catch (SocketException)
        {
            // 古い Windows で一部の設定が使えなくても接続は続ける
        }
    }

    void IRfbEventHandler.OnCursorChanged(CursorShape cursor) =>
        _dispatcher.BeginInvoke(() => CursorChanged?.Invoke(cursor));

    void IRfbEventHandler.OnServerCutText(string text) =>
        _dispatcher.BeginInvoke(() => ServerClipboardChanged?.Invoke(text));

    /// <summary>受信ループを開始する。UI スレッドから呼ぶ。</summary>
    public void Start()
    {
        // 受信ループ開始前なのでフレームバッファを読んでも競合しない
        RecreateBitmap(Client.Framebuffer);
        _runTask = Task.Run(RunAsync);
    }

    private async Task RunAsync()
    {
        Exception? error = null;
        try
        {
            await Client.RunAsync(this, _cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (_cts.IsCancellationRequested && ex is IOException or ObjectDisposedException or SocketException)
        {
            // 利用者が切断したときにソケットを閉じたことによるもの
        }
        catch (Exception ex)
        {
            error = ex;
        }
        finally
        {
            _tcp.Dispose();
        }

        _ = _dispatcher.BeginInvoke(() => Disconnected?.Invoke(error));
    }

    async ValueTask IRfbEventHandler.OnFramebufferUpdatedAsync(
        Framebuffer framebuffer, IReadOnlyList<RfbRectangle> rectangles, bool resized, CancellationToken ct)
    {
        // 受信ループはこの完了を待つので、コピー中にフレームバッファが書き換わることはない
        await _dispatcher.InvokeAsync(() => Present(framebuffer, rectangles, resized), DispatcherPriority.Render, ct);
    }

    /// <summary>
    /// 表示倍率（UI スレッド専用）。50% のときは App 側で 1/2 に縮小したビットマップを持ち、
    /// それ以外は元の解像度のビットマップを持つ（「ウィンドウに合わせる」の拡大縮小は WPF が行う）。
    /// </summary>
    public DisplayScale Scale
    {
        get => _scale;
        set
        {
            bool halfChanged = (value == DisplayScale.Half) != (_scale == DisplayScale.Half);
            _scale = value;
            if (halfChanged && Bitmap is not null)
            {
                // 受信ループと並行してフレームバッファを読むので、念のためフルスクリーンを取り直して整合させる
                RecreateBitmap(Client.Framebuffer);
                Client.RequestFramebufferUpdate(incremental: false);
            }
        }
    }

    private DisplayScale _scale = DisplayScale.Fit;
    private byte[] _halfPixels = [];

    /// <summary>Mac の画面の大きさ（ピクセル）。表示倍率に関係なくマウス座標の変換に使う（UI スレッド専用）。</summary>
    public int RemoteWidth { get; private set; }

    public int RemoteHeight { get; private set; }

    private bool IsHalf => _scale == DisplayScale.Half;

    private void Present(Framebuffer fb, IReadOnlyList<RfbRectangle> rectangles, bool resized)
    {
        if (Bitmap is null || resized || RemoteWidth != fb.Width || RemoteHeight != fb.Height)
        {
            RecreateBitmap(fb);
            return;
        }

        if (rectangles.Count > UnionThreshold)
        {
            // 細かい矩形が多いときは外接矩形 1 回で書く方が速い
            int left = int.MaxValue, top = int.MaxValue, right = 0, bottom = 0;
            foreach (var r in rectangles)
            {
                left = Math.Min(left, r.X);
                top = Math.Min(top, r.Y);
                right = Math.Max(right, r.X + r.Width);
                bottom = Math.Max(bottom, r.Y + r.Height);
            }

            Write(fb, new RfbRectangle(left, top, right - left, bottom - top));
            return;
        }

        foreach (var r in rectangles)
        {
            Write(fb, r);
        }
    }

    private void Write(Framebuffer fb, RfbRectangle r)
    {
        if (IsHalf)
        {
            // 更新された矩形を含む 2x2 ブロックだけ縮小して書く
            int halfStride = FramebufferScaler.HalfSize(fb.Width) * Framebuffer.BytesPerPixel;
            RfbRectangle d = FramebufferScaler.DownscaleHalf(fb, r, _halfPixels, halfStride);
            if (d.Width > 0 && d.Height > 0)
            {
                Bitmap!.WritePixels(new Int32Rect(d.X, d.Y, d.Width, d.Height), _halfPixels, halfStride, d.X, d.Y);
            }

            return;
        }

        int width = Math.Min(r.Width, fb.Width - r.X);
        int height = Math.Min(r.Height, fb.Height - r.Y);
        if (width <= 0 || height <= 0)
        {
            return;
        }

        Bitmap!.WritePixels(new Int32Rect(r.X, r.Y, width, height), fb.Pixels, fb.Stride, r.X, r.Y);
    }

    private void RecreateBitmap(Framebuffer fb)
    {
        RemoteWidth = fb.Width;
        RemoteHeight = fb.Height;
        WriteableBitmap bitmap;
        if (IsHalf)
        {
            int halfWidth = FramebufferScaler.HalfSize(fb.Width);
            int halfHeight = FramebufferScaler.HalfSize(fb.Height);
            _halfPixels = new byte[halfWidth * halfHeight * Framebuffer.BytesPerPixel];
            bitmap = new WriteableBitmap(Math.Max(halfWidth, 1), Math.Max(halfHeight, 1), 96, 96, PixelFormats.Bgr32, null);
            Bitmap = bitmap;
            Write(fb, new RfbRectangle(0, 0, fb.Width, fb.Height));
        }
        else
        {
            _halfPixels = [];
            bitmap = new WriteableBitmap(Math.Max(fb.Width, 1), Math.Max(fb.Height, 1), 96, 96, PixelFormats.Bgr32, null);
            Bitmap = bitmap;
            Write(fb, new RfbRectangle(0, 0, fb.Width, fb.Height));
        }

        BitmapChanged?.Invoke(bitmap);
    }

    /// <summary>
    /// 元の解像度の画面のコピー（スクリーンショット用、UI スレッドで呼ぶ）。
    /// 50% 表示中はフレームバッファから作る。受信中の更新と重なると一部の矩形だけ 1 つ前の内容になることがある。
    /// </summary>
    public BitmapSource CreateFullResolutionSnapshot()
    {
        if (!IsHalf && Bitmap is not null)
        {
            WriteableBitmap clone = Bitmap.Clone(); // 表示中のものは Freeze しない（以降の WritePixels が失敗する）
            clone.Freeze();
            return clone;
        }

        Framebuffer fb = Client.Framebuffer;
        BitmapSource snapshot = BitmapSource.Create(
            fb.Width, fb.Height, 96, 96, PixelFormats.Bgr32, null, (byte[])fb.Pixels.Clone(), fb.Stride);
        snapshot.Freeze();
        return snapshot;
    }

    /// <summary>待たずに切断する（ウィンドウを閉じるとき用）。</summary>
    public void Abort()
    {
        _cts.Cancel();
        _tcp.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        Abort();
        if (_runTask is not null)
        {
            try
            {
                await _runTask;
            }
            catch
            {
                // RunAsync 内で処理済み
            }
        }

        await Client.DisposeAsync();
        _cts.Dispose();
    }
}
