using System.Security.Cryptography;
using System.Text;

namespace RexRemote.Protocol.Auth;

/// <summary>
/// VNC パスワード認証（セキュリティタイプ 2）。
/// パスワード先頭 8 バイトの各バイトのビット順を反転したものを DES 鍵にし、16 バイトのチャレンジを DES-ECB で暗号化する。
/// </summary>
public static class VncAuthenticator
{
    public const int ChallengeSize = 16;

    public static byte[] ComputeResponse(ReadOnlySpan<byte> challenge, string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        if (challenge.Length != ChallengeSize)
        {
            throw new RfbProtocolException($"VNC 認証のチャレンジは {ChallengeSize} バイトのはずですが {challenge.Length} バイトでした。");
        }

        byte[] key = DeriveKey(password);
        try
        {
            // .NET の DES は弱鍵・準弱鍵（例: 空パスワード → 全 0）で例外を投げるため、その場合だけ自前実装を使う
            if (DES.IsWeakKey(key) || DES.IsSemiWeakKey(key))
            {
                return DesCipher.EncryptEcb(key, challenge);
            }

            using var des = DES.Create();
            des.Key = key;
            return des.EncryptEcb(challenge, PaddingMode.None);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>パスワード（UTF-8）先頭 8 バイトを取り、不足分は 0 埋めし、各バイトのビット順を反転する。</summary>
    internal static byte[] DeriveKey(string password)
    {
        var key = new byte[8];
        byte[] bytes = Encoding.UTF8.GetBytes(password);
        try
        {
            for (int i = 0; i < key.Length && i < bytes.Length; i++)
            {
                key[i] = ReverseBits(bytes[i]);
            }

            return key;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    internal static byte ReverseBits(byte b)
    {
        int v = b;
        v = ((v & 0xF0) >> 4) | ((v & 0x0F) << 4);
        v = ((v & 0xCC) >> 2) | ((v & 0x33) << 2);
        v = ((v & 0xAA) >> 1) | ((v & 0x55) << 1);
        return (byte)v;
    }
}
