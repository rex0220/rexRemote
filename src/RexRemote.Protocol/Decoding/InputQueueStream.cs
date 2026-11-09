namespace RexRemote.Protocol.Decoding;

/// <summary>
/// 後から追記できる読み込み専用ストリーム。ZRLE の zlib ストリームは接続全体で 1 本なので、
/// 矩形ごとに届く圧縮データをここへ追記し、1 つの ZLibStream で読み続ける。
/// データが無いときは 0 を返す（ブロックしない）。
/// </summary>
internal sealed class InputQueueStream : Stream
{
    private byte[] _data = new byte[64 * 1024];
    private int _start;
    private int _end;

    public int Available => _end - _start;

    public void Append(ReadOnlySpan<byte> bytes)
    {
        if (_data.Length - _end < bytes.Length)
        {
            int available = Available;
            if (_data.Length - available >= bytes.Length)
            {
                // 詰めれば入る
                Buffer.BlockCopy(_data, _start, _data, 0, available);
            }
            else
            {
                var grown = new byte[Math.Max(_data.Length * 2, available + bytes.Length)];
                Buffer.BlockCopy(_data, _start, grown, 0, available);
                _data = grown;
            }

            _start = 0;
            _end = available;
        }

        bytes.CopyTo(_data.AsSpan(_end));
        _end += bytes.Length;
    }

    public override int Read(Span<byte> buffer)
    {
        int n = Math.Min(buffer.Length, Available);
        _data.AsSpan(_start, n).CopyTo(buffer);
        _start += n;
        if (_start == _end)
        {
            _start = 0;
            _end = 0;
        }

        return n;
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int ReadByte()
    {
        Span<byte> one = stackalloc byte[1];
        return Read(one) == 1 ? one[0] : -1;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
