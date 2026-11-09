using System.Buffers.Binary;

namespace RexRemote.Protocol.IO;

/// <summary>
/// 受信側のバッファ付きリーダー。RFB の整数はすべてビッグエンディアン。
/// NetworkStream は読み書きを別スレッドから同時に行えるが BufferedStream はできないため、
/// 読み込み側だけ独自にバッファリングする。
/// </summary>
internal sealed class RfbReader
{
    private readonly Stream _stream;
    private readonly byte[] _buffer;
    private int _pos;
    private int _len;

    public RfbReader(Stream stream, int bufferSize = 64 * 1024)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfLessThan(bufferSize, 16);
        _stream = stream;
        _buffer = new byte[bufferSize];
    }

    private int Buffered => _len - _pos;

    public ValueTask<byte> ReadU8Async(CancellationToken ct)
    {
        if (Buffered >= 1)
        {
            return new ValueTask<byte>(_buffer[_pos++]);
        }

        return Slow(ct);

        async ValueTask<byte> Slow(CancellationToken ct)
        {
            await FillAtLeastAsync(1, ct).ConfigureAwait(false);
            return _buffer[_pos++];
        }
    }

    public ValueTask<ushort> ReadU16Async(CancellationToken ct)
    {
        if (Buffered >= 2)
        {
            return new ValueTask<ushort>(TakeU16());
        }

        return Slow(ct);

        async ValueTask<ushort> Slow(CancellationToken ct)
        {
            await FillAtLeastAsync(2, ct).ConfigureAwait(false);
            return TakeU16();
        }
    }

    public ValueTask<uint> ReadU32Async(CancellationToken ct)
    {
        if (Buffered >= 4)
        {
            return new ValueTask<uint>(TakeU32());
        }

        return Slow(ct);

        async ValueTask<uint> Slow(CancellationToken ct)
        {
            await FillAtLeastAsync(4, ct).ConfigureAwait(false);
            return TakeU32();
        }
    }

    public async ValueTask<int> ReadS32Async(CancellationToken ct) =>
        unchecked((int)await ReadU32Async(ct).ConfigureAwait(false));

    public async ValueTask<byte[]> ReadBytesAsync(int count, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        var result = new byte[count];
        await ReadExactlyAsync(result, ct).ConfigureAwait(false);
        return result;
    }

    public async ValueTask ReadExactlyAsync(Memory<byte> destination, CancellationToken ct)
    {
        int copied = TakeBuffered(destination.Span);

        while (copied < destination.Length)
        {
            int remaining = destination.Length - copied;
            if (remaining >= _buffer.Length)
            {
                // 大きな読み込みはバッファを経由せず直接読む
                int n = await _stream.ReadAsync(destination[copied..], ct).ConfigureAwait(false);
                if (n == 0)
                {
                    throw EndOfStream();
                }

                copied += n;
            }
            else
            {
                await FillAtLeastAsync(1, ct).ConfigureAwait(false);
                copied += TakeBuffered(destination.Span[copied..]);
            }
        }
    }

    public async ValueTask SkipAsync(long count, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        while (count > 0)
        {
            if (Buffered == 0)
            {
                await FillAtLeastAsync(1, ct).ConfigureAwait(false);
            }

            int n = (int)Math.Min(count, Buffered);
            _pos += n;
            count -= n;
        }
    }

    private int TakeBuffered(Span<byte> destination)
    {
        int n = Math.Min(Buffered, destination.Length);
        if (n > 0)
        {
            _buffer.AsSpan(_pos, n).CopyTo(destination);
            _pos += n;
        }

        return n;
    }

    private ushort TakeU16()
    {
        ushort v = BinaryPrimitives.ReadUInt16BigEndian(_buffer.AsSpan(_pos, 2));
        _pos += 2;
        return v;
    }

    private uint TakeU32()
    {
        uint v = BinaryPrimitives.ReadUInt32BigEndian(_buffer.AsSpan(_pos, 4));
        _pos += 4;
        return v;
    }

    /// <summary>バッファに少なくとも <paramref name="count"/> バイトある状態にする。</summary>
    private async ValueTask FillAtLeastAsync(int count, CancellationToken ct)
    {
        if (Buffered >= count)
        {
            return;
        }

        // 未読分を先頭に詰める
        if (_pos > 0)
        {
            Buffer.BlockCopy(_buffer, _pos, _buffer, 0, Buffered);
            _len -= _pos;
            _pos = 0;
        }

        while (_len < count)
        {
            int n = await _stream.ReadAsync(_buffer.AsMemory(_len), ct).ConfigureAwait(false);
            if (n == 0)
            {
                throw EndOfStream();
            }

            _len += n;
        }
    }

    private static EndOfStreamException EndOfStream() => new("サーバーが接続を閉じました。");
}
