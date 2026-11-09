using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using RexRemote.Protocol.Auth;

namespace RexRemote.Protocol.Tests.Support;

/// <summary>
/// macOS の画面共有を模した偽サーバー。ハンドシェイクを行い、ARD / VNC 認証の応答を実際に検証する。
/// </summary>
internal sealed class FakeMacServer
{
    private readonly Stream _stream;

    public FakeMacServer(Stream stream) => _stream = stream;

    public string Version { get; init; } = "RFB 003.889\n";
    public byte[] SecurityTypes { get; init; } = [30, 33, 36, 35];
    public string ExpectedUserName { get; init; } = "alice";
    public string ExpectedPassword { get; init; } = "s3cret";
    public string FailureReason { get; init; } = "Authentication failed";
    public ushort Generator { get; init; } = 5;
    public byte[] Prime { get; init; } = TestPrimes.Synthetic4096;
    public int Width { get; init; } = 320;
    public int Height { get; init; } = 200;
    public string Name { get; init; } = "Test Mac";

    // 記録
    public string? ReceivedVersion { get; private set; }
    public byte ChosenSecurityType { get; private set; }
    public string? ReceivedUserName { get; private set; }
    public string? ReceivedPassword { get; private set; }
    public byte? ClientInitShared { get; private set; }
    public PixelFormat? ReceivedPixelFormat { get; private set; }
    public List<int> ReceivedEncodings { get; } = [];

    /// <summary>ハンドシェイクを最後まで行う。認証失敗時は理由を送って false を返す。</summary>
    public async Task<bool> RunHandshakeAsync(CancellationToken ct = default)
    {
        await WriteAsync(Encoding.ASCII.GetBytes(Version), ct);
        ReceivedVersion = Encoding.ASCII.GetString(await ReadAsync(12, ct));

        await WriteAsync([(byte)SecurityTypes.Length, .. SecurityTypes], ct);
        ChosenSecurityType = (await ReadAsync(1, ct))[0];

        bool ok = ChosenSecurityType switch
        {
            30 => await HandleArdAsync(ct),
            2 => await HandleVncAsync(ct),
            _ => throw new InvalidOperationException($"unexpected security type {ChosenSecurityType}"),
        };

        if (!ok)
        {
            byte[] reason = Encoding.UTF8.GetBytes(FailureReason);
            await WriteAsync([.. U32(1), .. U32((uint)reason.Length), .. reason], ct);
            return false;
        }

        await WriteAsync(U32(0), ct);

        ClientInitShared = (await ReadAsync(1, ct))[0];

        byte[] name = Encoding.UTF8.GetBytes(Name);
        var serverFormat = new byte[PixelFormat.Size];
        new PixelFormat(32, 24, false, true, 255, 255, 255, 16, 8, 0).Write(serverFormat);
        await WriteAsync([.. U16(Width), .. U16(Height), .. serverFormat, .. U32((uint)name.Length), .. name], ct);

        // SetPixelFormat
        byte[] spf = await ReadAsync(20, ct);
        Assert.Equal(0, spf[0]);
        ReceivedPixelFormat = PixelFormat.Parse(spf.AsSpan(4));

        // SetEncodings
        byte[] seHeader = await ReadAsync(4, ct);
        Assert.Equal(2, seHeader[0]);
        int count = BinaryPrimitives.ReadUInt16BigEndian(seHeader.AsSpan(2));
        byte[] encodings = await ReadAsync(4 * count, ct);
        for (int i = 0; i < count; i++)
        {
            ReceivedEncodings.Add(BinaryPrimitives.ReadInt32BigEndian(encodings.AsSpan(4 * i)));
        }

        return true;
    }

    private async Task<bool> HandleArdAsync(CancellationToken ct)
    {
        int keyLength = Prime.Length;
        var p = new BigInteger(Prime, isUnsigned: true, isBigEndian: true);
        var serverPrivate = new BigInteger(RandomNumberGenerator.GetBytes(32), isUnsigned: true, isBigEndian: true) + 2;
        BigInteger serverPublic = BigInteger.ModPow(Generator, serverPrivate, p);

        await WriteAsync([.. U16(Generator), .. U16(keyLength), .. Prime, .. ArdAuthenticator.ToFixedLength(serverPublic, keyLength)], ct);

        byte[] ciphertext = await ReadAsync(ArdAuthenticator.CredentialBlockSize, ct);
        byte[] clientPublicBytes = await ReadAsync(keyLength, ct);
        var clientPublic = new BigInteger(clientPublicBytes, isUnsigned: true, isBigEndian: true);

        BigInteger shared = BigInteger.ModPow(clientPublic, serverPrivate, p);
        byte[] aesKey = MD5.HashData(ArdAuthenticator.ToFixedLength(shared, keyLength));
        using var aes = Aes.Create();
        aes.Key = aesKey;
        byte[] block = aes.DecryptEcb(ciphertext, PaddingMode.None);

        ReceivedUserName = ReadNulTerminated(block.AsSpan(0, 64));
        ReceivedPassword = ReadNulTerminated(block.AsSpan(64, 64));
        return ReceivedUserName == ExpectedUserName && ReceivedPassword == ExpectedPassword;
    }

    private async Task<bool> HandleVncAsync(CancellationToken ct)
    {
        byte[] challenge = RandomNumberGenerator.GetBytes(16);
        await WriteAsync(challenge, ct);
        byte[] response = await ReadAsync(16, ct);
        return response.AsSpan().SequenceEqual(VncAuthenticator.ComputeResponse(challenge, ExpectedPassword));
    }

    private static string ReadNulTerminated(ReadOnlySpan<byte> field)
    {
        int nul = field.IndexOf((byte)0);
        Assert.True(nul >= 0, "credential field must be NUL-terminated");
        return Encoding.UTF8.GetString(field[..nul]);
    }

    public async Task<byte[]> ReadAsync(int count, CancellationToken ct = default)
    {
        var buffer = new byte[count];
        await _stream.ReadExactlyAsync(buffer, ct);
        return buffer;
    }

    public Task WriteAsync(byte[] data, CancellationToken ct = default) => _stream.WriteAsync(data, 0, data.Length, ct);

    public static byte[] U16(int v)
    {
        var b = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)v);
        return b;
    }

    public static byte[] U32(uint v)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, v);
        return b;
    }

    public static byte[] S32(int v)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(b, v);
        return b;
    }
}

internal static class TestPrimes
{
    /// <summary>RFC 2409 Second Oakley Group（1024bit）。</summary>
    public static readonly byte[] Oakley1024 = Convert.FromHexString(
        "FFFFFFFFFFFFFFFFC90FDAA22168C234C4C6628B80DC1CD1" +
        "29024E088A67CC74020BBEA63B139B22514A08798E3404DD" +
        "EF9519B3CD3A431B302B0A6DF25F14374FE1356D6D51C245" +
        "E485B576625E7EC6F44C42E9A637ED6B0BFF5CB6F406B7ED" +
        "EE386BFB5A899FA5AE9F24117C4B1FE649286651ECE65381" +
        "FFFFFFFFFFFFFFFF");

    /// <summary>
    /// 4096bit（512 バイト）の奇数。実機（macOS 27）の keyLength=512 と同じ長さで計算量を再現する。
    /// DH の往復計算 (g^a)^b = (g^b)^a は法が素数でなくても成り立つので、テストにはこれで足りる。
    /// </summary>
    public static readonly byte[] Synthetic4096 = CreateSynthetic(512);

    private static byte[] CreateSynthetic(int length)
    {
        var bytes = new byte[length];
        bytes.AsSpan().Fill(0xFF);
        bytes[length / 2] = 0xC9;
        bytes[^1] = 0xFD; // 奇数
        return bytes;
    }
}
