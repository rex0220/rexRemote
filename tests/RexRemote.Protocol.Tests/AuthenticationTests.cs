using System.Diagnostics;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using RexRemote.Protocol.Auth;
using RexRemote.Protocol.Tests.Support;
using Xunit.Abstractions;

namespace RexRemote.Protocol.Tests;

public class VncAuthenticatorTests
{
    [Theory]
    [InlineData(0b0000_0001, 0b1000_0000)]
    [InlineData(0b1100_0000, 0b0000_0011)]
    [InlineData(0x70, 0x0E)] // 'p'
    [InlineData(0xFF, 0xFF)]
    [InlineData(0x00, 0x00)]
    public void ReverseBits_ReversesBitOrder(byte input, byte expected) =>
        Assert.Equal(expected, VncAuthenticator.ReverseBits(input));

    [Fact]
    public void DeriveKey_UsesFirstEightBytesReversedAndZeroPads()
    {
        Assert.Equal(new byte[] { 0x0E, 0x86, 0xCE, 0xCE, 0xEE, 0xF6, 0x4E, 0x26 }, VncAuthenticator.DeriveKey("password-long"));
        Assert.Equal(new byte[] { 0x86, 0x46, 0, 0, 0, 0, 0, 0 }, VncAuthenticator.DeriveKey("ab"));
    }

    [Fact]
    public void ComputeResponse_MatchesDesWithReversedKey()
    {
        byte[] challenge = Enumerable.Range(0, 16).Select(i => (byte)(i * 17)).ToArray();
        byte[] response = VncAuthenticator.ComputeResponse(challenge, "password");

        using var des = DES.Create();
        des.Key = [0x0E, 0x86, 0xCE, 0xCE, 0xEE, 0xF6, 0x4E, 0x26];
        Assert.Equal(des.EncryptEcb(challenge, PaddingMode.None), response);
    }

    [Fact]
    public void ComputeResponse_EmptyPassword_WeakKeyDoesNotThrow()
    {
        // 空パスワード → 鍵が全 0 → .NET の DES は弱鍵として拒否する
        Assert.True(DES.IsWeakKey(new byte[8]));
        byte[] challenge = RandomNumberGenerator.GetBytes(16);

        byte[] response = VncAuthenticator.ComputeResponse(challenge, "");

        Assert.Equal(16, response.Length);
        Assert.Equal(DesCipher.EncryptEcb(new byte[8], challenge), response);
    }

    [Fact]
    public void ComputeResponse_RejectsWrongChallengeLength() =>
        Assert.Throws<RfbProtocolException>(() => VncAuthenticator.ComputeResponse(new byte[15], "x"));
}

public class DesCipherTests
{
    [Fact]
    public void KnownAnswer_ClassicTextbookVector()
    {
        // key 133457799BBCDFF1, plaintext 0123456789ABCDEF → 85E813540F0AB405
        byte[] key = Convert.FromHexString("133457799BBCDFF1");
        byte[] plain = Convert.FromHexString("0123456789ABCDEF");
        Assert.Equal(Convert.FromHexString("85E813540F0AB405"), DesCipher.EncryptEcb(key, plain));
    }

    [Fact]
    public void MatchesDotNetDes_ForRandomKeys()
    {
        var random = new Random(12345);
        using var des = DES.Create();
        for (int i = 0; i < 200; i++)
        {
            var key = new byte[8];
            var data = new byte[16];
            random.NextBytes(key);
            random.NextBytes(data);
            if (DES.IsWeakKey(key) || DES.IsSemiWeakKey(key))
            {
                continue;
            }

            des.Key = key;
            Assert.Equal(des.EncryptEcb(data, PaddingMode.None), DesCipher.EncryptEcb(key, data));
        }
    }

    [Fact]
    public void WeakKey_EncryptingTwiceIsIdentity()
    {
        // 弱鍵では E_K(E_K(x)) = x になる
        byte[] key = Convert.FromHexString("0101010101010101");
        byte[] plain = Convert.FromHexString("0123456789ABCDEF");
        Assert.Equal(plain, DesCipher.EncryptEcb(key, DesCipher.EncryptEcb(key, plain)));
    }
}

public class ArdAuthenticatorTests
{
    private readonly ITestOutputHelper _output;

    public ArdAuthenticatorTests(ITestOutputHelper output) => _output = output;

    public static TheoryData<string, ushort> Groups => new()
    {
        { nameof(TestPrimes.Oakley1024), 2 },
        { nameof(TestPrimes.Synthetic4096), 5 }, // 実機（macOS 27）は generator=5, keyLength=512
    };

    [Theory]
    [MemberData(nameof(Groups))]
    public void ComputeResponse_ServerCanDecryptCredentials(string primeName, ushort generator)
    {
        byte[] prime = primeName == nameof(TestPrimes.Oakley1024) ? TestPrimes.Oakley1024 : TestPrimes.Synthetic4096;
        int keyLength = prime.Length;
        var p = new BigInteger(prime, isUnsigned: true, isBigEndian: true);
        var serverPrivate = new BigInteger(RandomNumberGenerator.GetBytes(64), isUnsigned: true, isBigEndian: true) + 2;
        byte[] serverPublic = ArdAuthenticator.ToFixedLength(BigInteger.ModPow(generator, serverPrivate, p), keyLength);

        var sw = Stopwatch.StartNew();
        var response = ArdAuthenticator.ComputeResponse(generator, prime, serverPublic, "rex", "pässwörd-日本語");
        _output.WriteLine($"{primeName}: ComputeResponse {sw.ElapsedMilliseconds} ms");

        Assert.Equal(128, response.Ciphertext.Length);
        Assert.Equal(keyLength, response.ClientPublicKey.Length);
        Assert.Equal(128 + keyLength, response.ToWireFormat().Length);

        // サーバー側の計算で復号できること
        var clientPublic = new BigInteger(response.ClientPublicKey, isUnsigned: true, isBigEndian: true);
        byte[] shared = ArdAuthenticator.ToFixedLength(BigInteger.ModPow(clientPublic, serverPrivate, p), keyLength);
        using var aes = Aes.Create();
        aes.Key = MD5.HashData(shared);
        byte[] block = aes.DecryptEcb(response.Ciphertext, PaddingMode.None);

        Assert.Equal("rex", ReadField(block.AsSpan(0, 64)));
        Assert.Equal("pässwörd-日本語", ReadField(block.AsSpan(64, 64)));
    }

    [Fact]
    public void BuildCredentialBlock_LayoutIsNameNulRandomPasswordNulRandom()
    {
        byte[] block = ArdAuthenticator.BuildCredentialBlock("user", "pw", span => span.Fill(0xAB));

        Assert.Equal(128, block.Length);
        Assert.Equal("user\0"u8.ToArray(), block[..5]);
        Assert.All(block[5..64], b => Assert.Equal(0xAB, b));
        Assert.Equal("pw\0"u8.ToArray(), block[64..67]);
        Assert.All(block[67..], b => Assert.Equal(0xAB, b));
    }

    [Fact]
    public void BuildCredentialBlock_RejectsTooLongValuesWithoutLeakingThem()
    {
        string longPassword = new('x', 64);
        var ex = Assert.Throws<ArgumentException>(() => ArdAuthenticator.BuildCredentialBlock("u", longPassword, _ => { }));
        Assert.DoesNotContain(longPassword, ex.Message);

        // 63 バイトちょうどは OK
        ArdAuthenticator.BuildCredentialBlock(new string('u', 63), new string('p', 63), _ => { });
    }

    [Fact]
    public void ToFixedLength_LeftPadsWithZeros()
    {
        Assert.Equal(new byte[] { 0, 0, 0, 0x01, 0x00 }, ArdAuthenticator.ToFixedLength(new BigInteger(256), 5));
        Assert.Equal(new byte[] { 0, 0, 0 }, ArdAuthenticator.ToFixedLength(BigInteger.Zero, 3));
        Assert.Equal(new byte[] { 0xFF, 0xFF }, ArdAuthenticator.ToFixedLength(new BigInteger(65535), 2));
    }

    [Fact]
    public void ComputeResponse_RejectsInvalidServerKey()
    {
        byte[] prime = TestPrimes.Oakley1024;
        var one = new byte[prime.Length];
        one[^1] = 1;
        Assert.Throws<RfbProtocolException>(() => ArdAuthenticator.ComputeResponse(2, prime, one, "u", "p"));
        Assert.Throws<RfbProtocolException>(() => ArdAuthenticator.ComputeResponse(2, prime, new byte[10], "u", "p"));
    }

    [Fact]
    public void Credentials_ToStringDoesNotContainPassword()
    {
        var credentials = new RfbCredentials("alice", "hunter2");
        Assert.DoesNotContain("hunter2", credentials.ToString());
    }

    private static string ReadField(ReadOnlySpan<byte> field) => Encoding.UTF8.GetString(field[..field.IndexOf((byte)0)]);
}
