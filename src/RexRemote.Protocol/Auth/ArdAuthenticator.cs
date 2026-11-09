using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace RexRemote.Protocol.Auth;

/// <summary>
/// Apple Remote Desktop 認証（セキュリティタイプ 30）。
/// Diffie-Hellman で共有鍵を作り、MD5(共有鍵) を鍵として資格情報ブロックを AES-128-ECB で暗号化する。
/// </summary>
public static class ArdAuthenticator
{
    public const int FieldSize = 64;
    public const int CredentialBlockSize = FieldSize * 2;

    /// <summary>乱数源（<see cref="RandomNumberGenerator.Fill(Span{byte})"/> と同じ形）。テストで差し替える。</summary>
    public delegate void RandomSource(Span<byte> destination);

    /// <summary>
    /// DH の秘密指数のバイト数。512bit あれば 4096bit 群（実機の macOS 27 はこれ）でも
    /// 安全性の要件（群の強度の 2 倍以上）を満たし、ModPow もフル長より大幅に速い。
    /// </summary>
    internal const int PrivateExponentBytes = 64;

    /// <summary>サーバーに送る応答（暗号文 128 バイト + クライアント公開鍵 keyLength バイト）。</summary>
    public sealed class Response
    {
        internal Response(byte[] ciphertext, byte[] clientPublicKey)
        {
            Ciphertext = ciphertext;
            ClientPublicKey = clientPublicKey;
        }

        public byte[] Ciphertext { get; }

        public byte[] ClientPublicKey { get; }

        public byte[] ToWireFormat()
        {
            var wire = new byte[Ciphertext.Length + ClientPublicKey.Length];
            Ciphertext.CopyTo(wire, 0);
            ClientPublicKey.CopyTo(wire, Ciphertext.Length);
            return wire;
        }
    }

    /// <param name="generator">サーバーから受け取った生成元。</param>
    /// <param name="prime">素数 p（keyLength バイト・ビッグエンディアン）。</param>
    /// <param name="serverPublicKey">サーバー公開鍵（keyLength バイト・ビッグエンディアン）。</param>
    /// <param name="fillRandom">乱数源。テストで差し替える。既定は暗号論的乱数。</param>
    public static Response ComputeResponse(
        ushort generator,
        ReadOnlySpan<byte> prime,
        ReadOnlySpan<byte> serverPublicKey,
        string userName,
        string password,
        RandomSource? fillRandom = null)
    {
        ArgumentNullException.ThrowIfNull(userName);
        ArgumentNullException.ThrowIfNull(password);
        fillRandom ??= RandomNumberGenerator.Fill;

        int keyLength = prime.Length;
        if (keyLength == 0 || serverPublicKey.Length != keyLength)
        {
            throw new RfbProtocolException(
                $"ARD の鍵長が不正です（prime={prime.Length}, serverPublicKey={serverPublicKey.Length}）。");
        }

        var p = new BigInteger(prime, isUnsigned: true, isBigEndian: true);
        var g = new BigInteger(generator);
        var serverPub = new BigInteger(serverPublicKey, isUnsigned: true, isBigEndian: true);

        if (p <= 3 || g <= 1 || g >= p)
        {
            throw new RfbProtocolException("ARD の DH パラメータが不正です。");
        }

        if (serverPub <= 1 || serverPub >= p - 1)
        {
            throw new RfbProtocolException("ARD のサーバー公開鍵が不正です。");
        }

        // 資格情報は先に検証しておく（DH 計算の前に失敗させる）
        byte[] block = BuildCredentialBlock(userName, password, fillRandom);
        byte[]? sharedBytes = null;
        byte[]? aesKey = null;
        try
        {
            BigInteger privateKey = GeneratePrivateKey(p, fillRandom);
            BigInteger clientPub = BigInteger.ModPow(g, privateKey, p);
            BigInteger shared = BigInteger.ModPow(serverPub, privateKey, p);

            sharedBytes = ToFixedLength(shared, keyLength);
            aesKey = MD5.HashData(sharedBytes);

            byte[] ciphertext;
            using (var aes = Aes.Create())
            {
                aes.Key = aesKey;
                ciphertext = aes.EncryptEcb(block, PaddingMode.None);
            }

            return new Response(ciphertext, ToFixedLength(clientPub, keyLength));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(block);
            if (sharedBytes is not null)
            {
                CryptographicOperations.ZeroMemory(sharedBytes);
            }

            if (aesKey is not null)
            {
                CryptographicOperations.ZeroMemory(aesKey);
            }
        }
    }

    /// <summary>
    /// 128 バイトの資格情報ブロック。[0..63] ユーザー名 + NUL、[64..127] パスワード + NUL、残りは乱数。
    /// </summary>
    internal static byte[] BuildCredentialBlock(string userName, string password, RandomSource fillRandom)
    {
        var block = new byte[CredentialBlockSize];
        try
        {
            fillRandom(block);
            WriteField(block.AsSpan(0, FieldSize), userName, "ユーザー名");
            WriteField(block.AsSpan(FieldSize, FieldSize), password, "パスワード");
            return block;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(block);
            throw;
        }
    }

    private static void WriteField(Span<byte> field, string value, string label)
    {
        int byteCount = Encoding.UTF8.GetByteCount(value);
        if (byteCount > field.Length - 1)
        {
            // 値そのものはメッセージに含めない
            throw new ArgumentException($"{label}は UTF-8 で {field.Length - 1} バイト以内にしてください。");
        }

        Encoding.UTF8.GetBytes(value, field);
        field[byteCount] = 0;
    }

    /// <summary>ビッグエンディアン・符号なしで <paramref name="length"/> バイトに左ゼロ詰めする。</summary>
    internal static byte[] ToFixedLength(BigInteger value, int length)
    {
        byte[] raw = value.ToByteArray(isUnsigned: true, isBigEndian: true);
        if (raw.Length == length)
        {
            return raw;
        }

        if (raw.Length > length)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "値が鍵長を超えています。");
        }

        var result = new byte[length];
        raw.CopyTo(result, length - raw.Length);
        CryptographicOperations.ZeroMemory(raw);
        return result;
    }

    /// <summary>[2, p-2] の範囲の秘密指数を作る。</summary>
    private static BigInteger GeneratePrivateKey(BigInteger p, RandomSource fillRandom)
    {
        Span<byte> bytes = stackalloc byte[PrivateExponentBytes];
        try
        {
            fillRandom(bytes);
            var x = new BigInteger(bytes, isUnsigned: true, isBigEndian: true);
            return (x % (p - 3)) + 2;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }
}
