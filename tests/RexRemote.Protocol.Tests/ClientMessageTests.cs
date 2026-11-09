namespace RexRemote.Protocol.Tests;

public class ClientMessageTests
{
    [Fact]
    public void PixelFormat_Bgr32_SerializesAndParsesBack()
    {
        var bytes = new byte[PixelFormat.Size];
        PixelFormat.Bgr32.Write(bytes);

        Assert.Equal(new byte[] { 32, 24, 0, 1, 0, 255, 0, 255, 0, 255, 16, 8, 0, 0, 0, 0 }, bytes);
        Assert.Equal(PixelFormat.Bgr32, PixelFormat.Parse(bytes));
    }

    [Fact]
    public void SetPixelFormat_Layout()
    {
        byte[] msg = ClientMessages.SetPixelFormat(PixelFormat.Bgr32);
        Assert.Equal(20, msg.Length);
        Assert.Equal(0, msg[0]);
        Assert.Equal(PixelFormat.Bgr32, PixelFormat.Parse(msg.AsSpan(4)));
    }

    [Fact]
    public void SetEncodings_Layout()
    {
        byte[] msg = ClientMessages.SetEncodings([RfbEncoding.Zrle, RfbEncoding.Raw, RfbEncoding.DesktopSize]);
        Assert.Equal(
            new byte[] { 2, 0, 0, 3, 0, 0, 0, 16, 0, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0x21 },
            msg);
    }

    [Fact]
    public void FramebufferUpdateRequest_Layout()
    {
        Assert.Equal(
            new byte[] { 3, 1, 0x00, 0x0A, 0x00, 0x14, 0x0A, 0x00, 0x05, 0xA0 },
            ClientMessages.FramebufferUpdateRequest(true, 10, 20, 2560, 1440));
        Assert.Equal(0, ClientMessages.FramebufferUpdateRequest(false, 0, 0, 1, 1)[1]);
    }

    [Fact]
    public void PointerEvent_Layout()
    {
        Assert.Equal(
            new byte[] { 5, 0b101, 0x01, 0x00, 0x00, 0x02 },
            ClientMessages.PointerEvent(RfbButtons.Left | RfbButtons.Right, 256, 2));
    }

    [Fact]
    public void PointerEvent_ClampsNegativeCoordinates() =>
        Assert.Equal(new byte[] { 5, 0, 0, 0, 0, 0 }, ClientMessages.PointerEvent(RfbButtons.None, -5, -1));

    [Fact]
    public void KeyEvent_Layout()
    {
        Assert.Equal(
            new byte[] { 4, 1, 0, 0, 0x00, 0x00, 0xFF, 0xEB },
            ClientMessages.KeyEvent(true, 0xFFEB)); // Super_L
    }

    [Fact]
    public void ClientCutText_Utf8_AndNewlinesNormalizedToLf()
    {
        byte[] msg = ClientMessages.ClientCutText("あ\r\nb\rc", System.Text.Encoding.UTF8);
        byte[] body = [0xE3, 0x81, 0x82, (byte)'\n', (byte)'b', (byte)'\n', (byte)'c'];
        Assert.Equal((byte[])[6, 0, 0, 0, 0, 0, 0, (byte)body.Length, .. body], msg);
    }

    [Fact]
    public void ClientCutText_Layout()
    {
        Assert.Equal(
            new byte[] { 6, 0, 0, 0, 0, 0, 0, 3, (byte)'a', 0xE9, (byte)'?' },
            ClientMessages.ClientCutText("aé日")); // Latin-1 の範囲外は '?'
    }
}
