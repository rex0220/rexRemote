using System.Text;
using RexRemote.Protocol.Tests.Support;

namespace RexRemote.Protocol.Tests;

public class HandshakeTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Ard_FullHandshake_AuthenticatesAndConfiguresSession()
    {
        var (clientStream, serverStream) = PipeStream.CreatePair();
        var server = new FakeMacServer(serverStream);
        Task<bool> serverTask = server.RunHandshakeAsync();
        var logs = new List<string>();
        await using var client = new RfbClient(clientStream, new RfbClientOptions { Log = logs.Add });

        await client.HandshakeAsync(new RfbCredentials("alice", "s3cret")).WaitAsync(Timeout);

        Assert.True(await serverTask.WaitAsync(Timeout));
        Assert.Equal("RFB 003.008\n", server.ReceivedVersion);
        Assert.Equal(30, server.ChosenSecurityType);
        Assert.Equal("alice", server.ReceivedUserName);
        Assert.Equal((byte)1, server.ClientInitShared);
        Assert.Equal(PixelFormat.Bgr32, server.ReceivedPixelFormat);
        Assert.Equal([16, 1, 0, -239, -223], server.ReceivedEncodings);

        Assert.Equal("RFB 003.889", client.ServerVersion);
        Assert.Equal([30, 33, 36, 35], client.OfferedSecurityTypes);
        Assert.Equal(RfbSecurityType.AppleRemoteDesktop, client.SecurityType);
        Assert.NotNull(client.ServerInit);
        Assert.Equal(320, client.ServerInit.Width);
        Assert.Equal(200, client.ServerInit.Height);
        Assert.Equal("Test Mac", client.ServerInit.Name);
        Assert.Equal(320, client.Framebuffer.Width);
        Assert.Equal(200, client.Framebuffer.Height);

        Assert.DoesNotContain(logs, line => line.Contains("s3cret"));
    }

    [Fact]
    public async Task Ard_WrongPassword_ThrowsWithServerReason()
    {
        var (clientStream, serverStream) = PipeStream.CreatePair();
        var server = new FakeMacServer(serverStream) { FailureReason = "Authentication failure" };
        Task<bool> serverTask = server.RunHandshakeAsync();
        await using var client = new RfbClient(clientStream);

        var ex = await Assert.ThrowsAsync<RfbAuthenticationException>(
            () => client.HandshakeAsync(new RfbCredentials("alice", "wrong")).WaitAsync(Timeout));

        Assert.Contains("Authentication failure", ex.Message);
        Assert.DoesNotContain("wrong", ex.Message);
        Assert.False(await serverTask.WaitAsync(Timeout));
    }

    [Fact]
    public async Task Vnc_UsedWhenNoUserName()
    {
        var (clientStream, serverStream) = PipeStream.CreatePair();
        var server = new FakeMacServer(serverStream) { SecurityTypes = [30, 2], ExpectedPassword = "vncpass" };
        Task<bool> serverTask = server.RunHandshakeAsync();
        await using var client = new RfbClient(clientStream);

        await client.HandshakeAsync(new RfbCredentials(null, "vncpass")).WaitAsync(Timeout);

        Assert.True(await serverTask.WaitAsync(Timeout));
        Assert.Equal(2, server.ChosenSecurityType);
        Assert.Equal(RfbSecurityType.VncAuthentication, client.SecurityType);
    }

    [Fact]
    public async Task ServerRefusal_ThrowsWithReason()
    {
        var (clientStream, serverStream) = PipeStream.CreatePair();
        byte[] reason = Encoding.UTF8.GetBytes("Too many connections");
        await serverStream.WriteAsync("RFB 003.889\n"u8.ToArray());
        await serverStream.WriteAsync((byte[])[0, .. FakeMacServer.U32((uint)reason.Length), .. reason]);
        await using var client = new RfbClient(clientStream);

        var ex = await Assert.ThrowsAsync<RfbException>(
            () => client.HandshakeAsync(new RfbCredentials("a", "b")).WaitAsync(Timeout));
        Assert.Contains("Too many connections", ex.Message);
    }

    [Fact]
    public async Task OldServerVersion_IsRejected()
    {
        var (clientStream, serverStream) = PipeStream.CreatePair();
        await serverStream.WriteAsync("RFB 003.003\n"u8.ToArray());
        await using var client = new RfbClient(clientStream);

        await Assert.ThrowsAsync<RfbProtocolException>(
            () => client.HandshakeAsync(new RfbCredentials("a", "b")).WaitAsync(Timeout));
    }

    [Fact]
    public async Task ServerClosesConnection_ThrowsEndOfStream()
    {
        var (clientStream, serverStream) = PipeStream.CreatePair();
        await serverStream.WriteAsync("RFB 003.8"u8.ToArray());
        serverStream.Dispose();
        await using var client = new RfbClient(clientStream);

        await Assert.ThrowsAsync<EndOfStreamException>(
            () => client.HandshakeAsync(new RfbCredentials("a", "b")).WaitAsync(Timeout));
    }

    [Theory]
    [InlineData(new byte[] { 30, 33, 36, 35 }, true, RfbSecurityType.AppleRemoteDesktop)]
    [InlineData(new byte[] { 2, 30 }, true, RfbSecurityType.AppleRemoteDesktop)]
    [InlineData(new byte[] { 2, 30 }, false, RfbSecurityType.VncAuthentication)]
    [InlineData(new byte[] { 2 }, true, RfbSecurityType.VncAuthentication)]
    public void SelectSecurityType_FollowsPreferenceRules(byte[] offered, bool hasUserName, RfbSecurityType expected) =>
        Assert.Equal(expected, RfbClient.SelectSecurityType(offered, hasUserName));

    [Fact]
    public void SelectSecurityType_ArdOnlyWithoutUserName_AsksForUserName()
    {
        var ex = Assert.Throws<RfbAuthenticationException>(() => RfbClient.SelectSecurityType([30, 33], hasUserName: false));
        Assert.Contains("ユーザー名", ex.Message);
    }

    [Fact]
    public void SelectSecurityType_NothingSupported_Throws()
    {
        var ex = Assert.Throws<RfbAuthenticationException>(() => RfbClient.SelectSecurityType([33, 36, 35], hasUserName: true));
        Assert.Contains("33, 36, 35", ex.Message);
    }

    [Theory]
    [InlineData("RFB 003.889\n", 3, 889)]
    [InlineData("RFB 003.008\n", 3, 8)]
    [InlineData("RFB 004.001\n", 4, 1)]
    public void ParseVersion_ParsesValidStrings(string version, int major, int minor) =>
        Assert.Equal((major, minor), RfbClient.ParseVersion(Encoding.ASCII.GetBytes(version)));

    [Theory]
    [InlineData("HTTP/1.1 400")]
    [InlineData("RFB 003.88x\n")]
    [InlineData("RFB 003.889 ")]
    public void ParseVersion_RejectsGarbage(string version) =>
        Assert.Throws<RfbProtocolException>(() => RfbClient.ParseVersion(Encoding.ASCII.GetBytes(version)));
}
