using System.Buffers;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading.Channels;
using RexRemote.Protocol.Auth;
using RexRemote.Protocol.IO;

namespace RexRemote.Protocol;

/// <summary>受信ループからの通知を受け取る。</summary>
public interface IRfbEventHandler
{
    /// <summary>
    /// FramebufferUpdate 1 回分をフレームバッファに描き終えたときに呼ばれる。
    /// 受信ループはこの ValueTask の完了を待ってから次のメッセージを読むので、
    /// この間はフレームバッファを安全に読める（UI へのコピーはここで行う）。
    /// </summary>
    /// <param name="resized">DesktopSize によりサイズが変わった（<see cref="Framebuffer.Pixels"/> も差し替わっている）。</param>
    ValueTask OnFramebufferUpdatedAsync(Framebuffer framebuffer, IReadOnlyList<RfbRectangle> rectangles, bool resized, CancellationToken ct);

    void OnBell() { }

    /// <summary>Mac のクリップボードが変わった（改行は LF）。</summary>
    void OnServerCutText(string text) { }

    /// <summary>カーソルの形が変わった（Cursor 擬似エンコーディングを要求している場合）。受信ループのスレッドで呼ばれる。</summary>
    void OnCursorChanged(CursorShape cursor) { }
}

public sealed class RfbClientOptions
{
    /// <summary>SetEncodings で要求するエンコーディング（優先順）。</summary>
    public IReadOnlyList<RfbEncoding> Encodings { get; init; } = RfbClient.DefaultEncodings;

    /// <summary>ClientInit の shared フラグ。true なら他のクライアントの接続を切らない。</summary>
    public bool Shared { get; init; } = true;

    /// <summary>診断ログ。資格情報は決して渡さない。</summary>
    public Action<string>? Log { get; init; }

    /// <summary>ClientCutText の文字コード。RFB 標準は Latin-1。日本語を送るには UTF-8（サーバーが対応している場合）。</summary>
    public Encoding ClipboardEncoding { get; init; } = Encoding.Latin1;
}

/// <summary>
/// RFB 3.8 クライアント。<see cref="Stream"/> を受け取るだけで、ソケットや UI には依存しない。
/// 使い方: <see cref="HandshakeAsync"/> → <see cref="RunAsync"/>（受信・送信ループ）。
/// ストリームの破棄は呼び出し側の責任。
/// </summary>
public sealed class RfbClient : IAsyncDisposable
{
    public static IReadOnlyList<RfbEncoding> DefaultEncodings { get; } =
        [RfbEncoding.Zrle, RfbEncoding.CopyRect, RfbEncoding.Raw, RfbEncoding.Cursor, RfbEncoding.DesktopSize];

    private const int MaxReasonLength = 64 * 1024;
    private const int MaxNameLength = 64 * 1024;
    private const int MaxArdKeyLength = 1024; // 8192bit

    private enum State { New, Handshaking, Ready, Running, Closed }

    private readonly Stream _stream;
    private readonly RfbReader _reader;
    private readonly RfbClientOptions _options;
    private readonly Channel<byte[]> _outgoing =
        Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });

    private ServerMessageReader? _messages;
    private State _state = State.New;

    public RfbClient(Stream stream, RfbClientOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _stream = stream;
        _reader = new RfbReader(stream);
        _options = options ?? new RfbClientOptions();
    }

    /// <summary>サーバーが送ってきたバージョン文字列（例: "RFB 003.889"）。</summary>
    public string? ServerVersion { get; private set; }

    /// <summary>サーバーが提示したセキュリティタイプ。</summary>
    public IReadOnlyList<byte> OfferedSecurityTypes { get; private set; } = [];

    public RfbSecurityType SecurityType { get; private set; }

    public ServerInit? ServerInit { get; private set; }

    public Framebuffer Framebuffer =>
        _messages?.Framebuffer ?? throw new InvalidOperationException("ハンドシェイクが完了していません。");

    // ------------------------------------------------------------------
    // ハンドシェイク
    // ------------------------------------------------------------------

    public async Task HandshakeAsync(RfbCredentials credentials, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        if (_state != State.New)
        {
            throw new InvalidOperationException("ハンドシェイクは 1 回だけ実行できます。");
        }

        _state = State.Handshaking;
        try
        {
            await NegotiateVersionAsync(ct).ConfigureAwait(false);
            await NegotiateSecurityAsync(credentials, ct).ConfigureAwait(false);
            await InitializeAsync(ct).ConfigureAwait(false);
            _state = State.Ready;
        }
        catch
        {
            _state = State.Closed;
            throw;
        }
    }

    private async Task NegotiateVersionAsync(CancellationToken ct)
    {
        byte[] version = await _reader.ReadBytesAsync(12, ct).ConfigureAwait(false);
        var (major, minor) = ParseVersion(version);
        ServerVersion = Encoding.ASCII.GetString(version, 0, 11);
        Log($"サーバーのバージョン: {ServerVersion}");

        // Mac は 003.889 を送ってくる。3.8 以上なら 3.8 として扱う
        if (major < 3 || (major == 3 && minor < 8))
        {
            throw new RfbProtocolException($"未対応の RFB バージョンです: {ServerVersion}（3.8 以上が必要）");
        }

        await WriteDirectAsync(ClientMessages.ProtocolVersion38(), ct).ConfigureAwait(false);
    }

    private async Task NegotiateSecurityAsync(RfbCredentials credentials, CancellationToken ct)
    {
        int count = await _reader.ReadU8Async(ct).ConfigureAwait(false);
        if (count == 0)
        {
            string reason = await ReadReasonAsync(ct).ConfigureAwait(false);
            throw new RfbException($"サーバーが接続を拒否しました: {reason}");
        }

        byte[] offered = await _reader.ReadBytesAsync(count, ct).ConfigureAwait(false);
        OfferedSecurityTypes = offered;
        SecurityType = SelectSecurityType(offered, credentials.HasUserName);
        Log($"セキュリティタイプ: 提示 [{string.Join(", ", offered)}] → {(byte)SecurityType} を選択");

        await WriteDirectAsync([(byte)SecurityType], ct).ConfigureAwait(false);

        switch (SecurityType)
        {
            case RfbSecurityType.AppleRemoteDesktop:
                await AuthenticateArdAsync(credentials, ct).ConfigureAwait(false);
                break;
            case RfbSecurityType.VncAuthentication:
                await AuthenticateVncAsync(credentials, ct).ConfigureAwait(false);
                break;
        }

        uint result = await _reader.ReadU32Async(ct).ConfigureAwait(false);
        if (result != 0)
        {
            string reason = await TryReadReasonAsync(ct).ConfigureAwait(false);
            throw new RfbAuthenticationException(
                string.IsNullOrEmpty(reason) ? "認証に失敗しました。" : $"認証に失敗しました: {reason}");
        }

        Log("認証に成功しました。");
    }

    private async Task AuthenticateArdAsync(RfbCredentials credentials, CancellationToken ct)
    {
        ushort generator = await _reader.ReadU16Async(ct).ConfigureAwait(false);
        ushort keyLength = await _reader.ReadU16Async(ct).ConfigureAwait(false);
        if (keyLength == 0 || keyLength > MaxArdKeyLength)
        {
            throw new RfbProtocolException($"ARD の鍵長が不正です: {keyLength} バイト");
        }

        byte[] prime = await _reader.ReadBytesAsync(keyLength, ct).ConfigureAwait(false);
        byte[] serverPublicKey = await _reader.ReadBytesAsync(keyLength, ct).ConfigureAwait(false);
        Log($"ARD: generator={generator}, keyLength={keyLength} バイト（{keyLength * 8}bit）");

        // 4096bit の ModPow は数十 ms かかるので UI スレッドから呼ばれても固まらないようにする
        var response = await Task.Run(
            () => ArdAuthenticator.ComputeResponse(generator, prime, serverPublicKey, credentials.UserName!, credentials.Password),
            ct).ConfigureAwait(false);

        await WriteDirectAsync(response.ToWireFormat(), ct).ConfigureAwait(false);
    }

    private async Task AuthenticateVncAsync(RfbCredentials credentials, CancellationToken ct)
    {
        byte[] challenge = await _reader.ReadBytesAsync(VncAuthenticator.ChallengeSize, ct).ConfigureAwait(false);
        byte[] response = VncAuthenticator.ComputeResponse(challenge, credentials.Password);
        await WriteDirectAsync(response, ct).ConfigureAwait(false);
    }

    private async Task InitializeAsync(CancellationToken ct)
    {
        await WriteDirectAsync([_options.Shared ? (byte)1 : (byte)0], ct).ConfigureAwait(false);

        int width = await _reader.ReadU16Async(ct).ConfigureAwait(false);
        int height = await _reader.ReadU16Async(ct).ConfigureAwait(false);
        byte[] format = await _reader.ReadBytesAsync(PixelFormat.Size, ct).ConfigureAwait(false);
        uint nameLength = await _reader.ReadU32Async(ct).ConfigureAwait(false);
        if (nameLength > MaxNameLength)
        {
            throw new RfbProtocolException($"ServerInit の名前が長すぎます: {nameLength} バイト");
        }

        byte[] name = await _reader.ReadBytesAsync((int)nameLength, ct).ConfigureAwait(false);
        ServerInit = new ServerInit(width, height, PixelFormat.Parse(format), Encoding.UTF8.GetString(name));
        Log($"ServerInit: {width}x{height} \"{ServerInit.Name}\" サーバー形式 [{ServerInit.PixelFormat}]");

        _messages = new ServerMessageReader(_reader, new Framebuffer(width, height));

        // 扱いやすい形式に固定し、エンコーディングを要求する（1 回の書き込みで送る）
        byte[] setPixelFormat = ClientMessages.SetPixelFormat(PixelFormat.Bgr32);
        byte[] setEncodings = ClientMessages.SetEncodings(_options.Encodings);
        await WriteDirectAsync([.. setPixelFormat, .. setEncodings], ct).ConfigureAwait(false);
        Log($"SetEncodings: [{string.Join(", ", _options.Encodings)}]");
    }

    // ------------------------------------------------------------------
    // 受信・送信ループ
    // ------------------------------------------------------------------

    /// <summary>
    /// 受信ループと送信ループを実行する。キャンセルされるか、接続が切れるかエラーになるまで戻らない。
    /// 最初にフルスクリーンの非インクリメンタル更新を要求し、以降は更新を受け取るたびにインクリメンタル更新を要求する。
    /// </summary>
    public async Task RunAsync(IRfbEventHandler handler, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (_state != State.Ready)
        {
            throw new InvalidOperationException("ハンドシェイク完了後に 1 回だけ実行できます。");
        }

        _state = State.Running;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        RequestFramebufferUpdate(incremental: false);

        Task send = SendLoopAsync(linked.Token);
        Task receive = ReceiveLoopAsync(handler, linked.Token);
        Task first = await Task.WhenAny(send, receive).ConfigureAwait(false);
        linked.Cancel();
        try
        {
            await Task.WhenAll(send, receive).ConfigureAwait(false);
        }
        catch
        {
            // 下で原因を選んで投げ直す
        }
        finally
        {
            _state = State.Closed;
            _outgoing.Writer.TryComplete();
        }

        // 先に終わった側の例外（＝根本原因）を優先する。後から終わった側はキャンセルによる二次的なもの
        Task second = first == send ? receive : send;
        foreach (Task task in new[] { first, second })
        {
            if (task.IsFaulted && task.Exception!.InnerException is { } inner and not OperationCanceledException)
            {
                ExceptionDispatchInfo.Capture(inner).Throw();
            }
        }

        ct.ThrowIfCancellationRequested();
        throw new RfbException("接続が終了しました。");
    }

    private async Task ReceiveLoopAsync(IRfbEventHandler handler, CancellationToken ct)
    {
        await Task.Yield(); // 呼び出し元をブロックしない
        ServerMessageReader messages = _messages!;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            ServerMessage message = await messages.ReadAsync(ct).ConfigureAwait(false);
            switch (message)
            {
                case FramebufferUpdateMessage update:
                    if (update.Cursor is { } cursor)
                    {
                        handler.OnCursorChanged(cursor);
                    }

                    await handler.OnFramebufferUpdatedAsync(messages.Framebuffer, update.Rectangles, update.Resized, ct)
                        .ConfigureAwait(false);
                    // サイズが変わったらフルスクリーンを取り直す
                    RequestFramebufferUpdate(incremental: !update.Resized);
                    break;

                case BellMessage:
                    handler.OnBell();
                    break;

                case ServerCutTextMessage cut:
                    handler.OnServerCutText(cut.Text);
                    break;

                case SetColourMapEntriesMessage:
                    break;
            }
        }
    }

    private async Task SendLoopAsync(CancellationToken ct)
    {
        var buffer = new ArrayBufferWriter<byte>(4096);
        ChannelReader<byte[]> reader = _outgoing.Reader;
        while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
        {
            // 溜まっているメッセージをまとめて 1 回で書き込む
            buffer.Clear();
            while (reader.TryRead(out byte[]? message))
            {
                buffer.Write(message);
            }

            await _stream.WriteAsync(buffer.WrittenMemory, ct).ConfigureAwait(false);
            await _stream.FlushAsync(ct).ConfigureAwait(false);
        }
    }

    // ------------------------------------------------------------------
    // 送信（どのスレッドから呼んでもよい。順序は保たれる）
    // ------------------------------------------------------------------

    public void RequestFramebufferUpdate(bool incremental)
    {
        Framebuffer fb = Framebuffer;
        Enqueue(ClientMessages.FramebufferUpdateRequest(incremental, 0, 0, fb.Width, fb.Height));
    }

    public void SendPointerEvent(RfbButtons buttons, int x, int y) =>
        Enqueue(ClientMessages.PointerEvent(buttons, x, y));

    public void SendKeyEvent(bool down, uint keysym) =>
        Enqueue(ClientMessages.KeyEvent(down, keysym));

    public void SendClientCutText(string text) =>
        Enqueue(ClientMessages.ClientCutText(text, _options.ClipboardEncoding));

    private void Enqueue(byte[] message)
    {
        if (_state is State.New or State.Handshaking)
        {
            throw new InvalidOperationException("ハンドシェイクが完了していません。");
        }

        // 切断後は黙って捨てる
        _outgoing.Writer.TryWrite(message);
    }

    // ------------------------------------------------------------------
    // ヘルパー
    // ------------------------------------------------------------------

    internal static (int Major, int Minor) ParseVersion(ReadOnlySpan<byte> version)
    {
        if (version.Length == 12 && version.StartsWith("RFB "u8) && version[7] == (byte)'.' && version[11] == (byte)'\n'
            && TryParseDigits(version[4..7], out int major) && TryParseDigits(version[8..11], out int minor))
        {
            return (major, minor);
        }

        throw new RfbProtocolException("RFB サーバーではないか、バージョン文字列が不正です。");
    }

    private static bool TryParseDigits(ReadOnlySpan<byte> digits, out int value)
    {
        value = 0;
        foreach (byte d in digits)
        {
            if (d is < (byte)'0' or > (byte)'9')
            {
                return false;
            }

            value = value * 10 + (d - '0');
        }

        return true;
    }

    /// <summary>
    /// ユーザー名があり 30 が提示されていれば 30。そうでなく 2 があれば 2。どちらも無ければエラー。
    /// </summary>
    internal static RfbSecurityType SelectSecurityType(ReadOnlySpan<byte> offered, bool hasUserName)
    {
        bool ard = offered.Contains((byte)RfbSecurityType.AppleRemoteDesktop);
        bool vnc = offered.Contains((byte)RfbSecurityType.VncAuthentication);

        if (hasUserName && ard)
        {
            return RfbSecurityType.AppleRemoteDesktop;
        }

        if (vnc)
        {
            return RfbSecurityType.VncAuthentication;
        }

        string list = string.Join(", ", offered.ToArray());
        if (ard)
        {
            throw new RfbAuthenticationException(
                $"サーバーは ARD 認証（タイプ 30）を要求しています。macOS のユーザー名を指定してください。（提示: {list}）");
        }

        throw new RfbAuthenticationException($"対応しているセキュリティタイプ（30, 2）がサーバーから提示されませんでした。（提示: {list}）");
    }

    private async Task<string> ReadReasonAsync(CancellationToken ct)
    {
        uint length = await _reader.ReadU32Async(ct).ConfigureAwait(false);
        if (length > MaxReasonLength)
        {
            throw new RfbProtocolException($"理由文字列が長すぎます: {length} バイト");
        }

        byte[] reason = await _reader.ReadBytesAsync((int)length, ct).ConfigureAwait(false);
        return Encoding.UTF8.GetString(reason);
    }

    /// <summary>認証失敗時の理由。サーバーによっては送らずに切断するので、読めなければ空文字。</summary>
    private async Task<string> TryReadReasonAsync(CancellationToken ct)
    {
        try
        {
            return await ReadReasonAsync(ct).ConfigureAwait(false);
        }
        catch (EndOfStreamException)
        {
            return string.Empty;
        }
        catch (IOException)
        {
            return string.Empty;
        }
    }

    private async Task WriteDirectAsync(byte[] data, CancellationToken ct)
    {
        await _stream.WriteAsync(data, ct).ConfigureAwait(false);
        await _stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private void Log(string message) => _options.Log?.Invoke(message);

    public ValueTask DisposeAsync()
    {
        _state = State.Closed;
        _outgoing.Writer.TryComplete();
        _messages?.Dispose();
        return ValueTask.CompletedTask;
    }
}
