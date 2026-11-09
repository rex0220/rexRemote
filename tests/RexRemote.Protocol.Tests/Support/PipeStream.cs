using System.Threading.Channels;

namespace RexRemote.Protocol.Tests.Support;

/// <summary>
/// メモリ上の双方向ストリームの片側。<see cref="CreatePair"/> で作った 2 つは互いに書いた内容を読める。
/// クライアントと偽サーバーを同時に動かすテストで使う。
/// </summary>
internal sealed class PipeStream : Stream
{
    private readonly Channel<byte[]> _incoming;
    private readonly Channel<byte[]> _outgoing;
    private ReadOnlyMemory<byte> _current;

    private PipeStream(Channel<byte[]> incoming, Channel<byte[]> outgoing)
    {
        _incoming = incoming;
        _outgoing = outgoing;
    }

    public static (PipeStream Client, PipeStream Server) CreatePair()
    {
        var toServer = Channel.CreateUnbounded<byte[]>();
        var toClient = Channel.CreateUnbounded<byte[]>();
        return (new PipeStream(toClient, toServer), new PipeStream(toServer, toClient));
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (_current.IsEmpty)
        {
            if (!await _incoming.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return 0; // 相手が閉じた
            }

            if (_incoming.Reader.TryRead(out byte[]? chunk))
            {
                _current = chunk;
            }
        }

        int n = Math.Min(buffer.Length, _current.Length);
        _current[..n].CopyTo(buffer);
        _current = _current[n..];
        return n;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        _outgoing.Writer.TryWrite(buffer.ToArray());
        return ValueTask.CompletedTask;
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        _outgoing.Writer.TryWrite(buffer.AsSpan(offset, count).ToArray());
        return Task.CompletedTask;
    }

    public override void Write(byte[] buffer, int offset, int count) =>
        _outgoing.Writer.TryWrite(buffer.AsSpan(offset, count).ToArray());

    protected override void Dispose(bool disposing)
    {
        _outgoing.Writer.TryComplete();
        base.Dispose(disposing);
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() { }
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
