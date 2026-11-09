using System.Buffers.Binary;
using System.Text;
using RexRemote.Protocol.Tests.Support;
using static RexRemote.Protocol.Tests.Support.FakeMacServer;

namespace RexRemote.Protocol.Tests;

/// <summary>ハンドシェイク後の受信・送信ループ（RunAsync）の結合テスト。</summary>
public class SessionTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private sealed class RecordingHandler : IRfbEventHandler
    {
        public List<(IReadOnlyList<RfbRectangle> Rects, bool Resized, uint FirstPixel)> Updates { get; } = [];
        public TaskCompletionSource<string> CutText { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Bells;

        public ValueTask OnFramebufferUpdatedAsync(Framebuffer framebuffer, IReadOnlyList<RfbRectangle> rectangles, bool resized, CancellationToken ct)
        {
            Updates.Add((rectangles, resized, framebuffer.GetPixel(1, 1)));
            return ValueTask.CompletedTask;
        }

        public void OnBell() => Interlocked.Increment(ref Bells);

        public void OnServerCutText(string text) => CutText.TrySetResult(text);
    }

    private static async Task<(RfbClient Client, FakeMacServer Server)> ConnectAsync(PipeStream clientStream, PipeStream serverStream)
    {
        var server = new FakeMacServer(serverStream) { Prime = TestPrimes.Oakley1024, Generator = 2, Width = 64, Height = 48 };
        Task<bool> serverTask = server.RunHandshakeAsync();
        var client = new RfbClient(clientStream);
        await client.HandshakeAsync(new RfbCredentials("alice", "s3cret")).WaitAsync(Timeout);
        Assert.True(await serverTask.WaitAsync(Timeout));
        return (client, server);
    }

    [Fact]
    public async Task RunAsync_RequestsUpdates_DeliversThem_AndSendsInput()
    {
        var (clientStream, serverStream) = PipeStream.CreatePair();
        var (client, server) = await ConnectAsync(clientStream, serverStream);
        await using var _ = client;
        var handler = new RecordingHandler();
        using var cts = new CancellationTokenSource();

        Task run = client.RunAsync(handler, cts.Token);

        // 最初は非インクリメンタルのフルスクリーン要求
        byte[] request = await server.ReadAsync(10).WaitAsync(Timeout);
        Assert.Equal(ClientMessages.FramebufferUpdateRequest(false, 0, 0, 64, 48), request);

        // Raw 1x1 at (1,1)、Bell、ServerCutText を送る
        byte[] text = Encoding.Latin1.GetBytes("clip");
        await server.WriteAsync(
        [
            0, 0, .. U16(1), .. U16(1), .. U16(1), .. U16(1), .. U16(1), .. S32(0), 0x33, 0x22, 0x11, 0,
            2,
            3, 0, 0, 0, .. U32((uint)text.Length), .. text,
        ]);

        // 更新を処理したら次はインクリメンタル要求
        request = await server.ReadAsync(10).WaitAsync(Timeout);
        Assert.Equal(ClientMessages.FramebufferUpdateRequest(true, 0, 0, 64, 48), request);
        Assert.Equal("clip", await handler.CutText.Task.WaitAsync(Timeout));
        Assert.Equal(1, handler.Bells);
        var update = Assert.Single(handler.Updates);
        Assert.Equal([new RfbRectangle(1, 1, 1, 1)], update.Rects);
        Assert.Equal(0x112233u, update.FirstPixel);

        // 入力イベントは送った順に届く
        client.SendPointerEvent(RfbButtons.Left, 10, 20);
        client.SendPointerEvent(RfbButtons.None, 11, 21);
        client.SendKeyEvent(true, 0xFFEB);
        Assert.Equal(ClientMessages.PointerEvent(RfbButtons.Left, 10, 20), await server.ReadAsync(6).WaitAsync(Timeout));
        Assert.Equal(ClientMessages.PointerEvent(RfbButtons.None, 11, 21), await server.ReadAsync(6).WaitAsync(Timeout));
        Assert.Equal(ClientMessages.KeyEvent(true, 0xFFEB), await server.ReadAsync(8).WaitAsync(Timeout));

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(Timeout));
    }

    [Fact]
    public async Task RunAsync_DesktopSize_RequestsFullUpdateWithNewSize()
    {
        var (clientStream, serverStream) = PipeStream.CreatePair();
        var (client, server) = await ConnectAsync(clientStream, serverStream);
        await using var _ = client;
        var handler = new RecordingHandler();
        using var cts = new CancellationTokenSource();
        Task run = client.RunAsync(handler, cts.Token);
        await server.ReadAsync(10).WaitAsync(Timeout);

        await server.WriteAsync([0, 0, .. U16(1), .. U16(0), .. U16(0), .. U16(100), .. U16(80), .. S32(-223)]);

        byte[] request = await server.ReadAsync(10).WaitAsync(Timeout);
        Assert.Equal(ClientMessages.FramebufferUpdateRequest(false, 0, 0, 100, 80), request);
        Assert.True(Assert.Single(handler.Updates).Resized);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(Timeout));
    }

    [Fact]
    public async Task RunAsync_ServerDisconnects_ThrowsEndOfStream()
    {
        var (clientStream, serverStream) = PipeStream.CreatePair();
        var (client, server) = await ConnectAsync(clientStream, serverStream);
        await using var _ = client;
        Task run = client.RunAsync(new RecordingHandler());
        await server.ReadAsync(10).WaitAsync(Timeout);

        serverStream.Dispose();

        await Assert.ThrowsAsync<EndOfStreamException>(() => run.WaitAsync(Timeout));
    }

    [Fact]
    public async Task RunAsync_ProtocolError_IsSurfaced()
    {
        var (clientStream, serverStream) = PipeStream.CreatePair();
        var (client, server) = await ConnectAsync(clientStream, serverStream);
        await using var _ = client;
        Task run = client.RunAsync(new RecordingHandler());
        await server.ReadAsync(10).WaitAsync(Timeout);

        await server.WriteAsync([200]);

        await Assert.ThrowsAsync<RfbProtocolException>(() => run.WaitAsync(Timeout));
    }

    [Fact]
    public void SendBeforeHandshake_Throws()
    {
        var (clientStream, _) = PipeStream.CreatePair();
        var client = new RfbClient(clientStream);
        Assert.Throws<InvalidOperationException>(() => client.SendPointerEvent(RfbButtons.None, 0, 0));
    }

    [Fact]
    public void FramebufferUpdateRequest_EncodesSize()
    {
        byte[] msg = ClientMessages.FramebufferUpdateRequest(true, 0, 0, 5120, 2880);
        Assert.Equal(5120, BinaryPrimitives.ReadUInt16BigEndian(msg.AsSpan(6)));
        Assert.Equal(2880, BinaryPrimitives.ReadUInt16BigEndian(msg.AsSpan(8)));
    }
}
