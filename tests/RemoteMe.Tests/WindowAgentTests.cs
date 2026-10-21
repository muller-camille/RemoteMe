using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using RemoteMe.WindowAgent;

namespace RemoteMe.Tests;

public class WindowAgentTests
{
    [Fact]
    public async Task Protocol_round_trips_framed_messages()
    {
        using var buffer = new MemoryStream();
        await AgentProtocol.WriteAsync(buffer, AgentProtocol.Hello, Encoding.UTF8.GetBytes("tok"), default);
        await AgentProtocol.WriteAsync(buffer, AgentProtocol.Frame, new byte[] { 1, 2, 3 }, default);
        await AgentProtocol.WriteJsonAsync(buffer, AgentProtocol.Meta, "{\"w\":800}", default);
        buffer.Position = 0;

        var hello = await AgentProtocol.ReadAsync(buffer, default);
        var frame = await AgentProtocol.ReadAsync(buffer, default);
        var meta = await AgentProtocol.ReadAsync(buffer, default);
        var end = await AgentProtocol.ReadAsync(buffer, default);

        Assert.Equal(AgentProtocol.Hello, hello!.Value.Type);
        Assert.Equal("tok", Encoding.UTF8.GetString(hello.Value.Payload));
        Assert.Equal(new byte[] { 1, 2, 3 }, frame!.Value.Payload);
        Assert.Equal("{\"w\":800}", Encoding.UTF8.GetString(meta!.Value.Payload));
        Assert.Null(end);
    }

    [Fact]
    public async Task Protocol_returns_null_on_clean_end_of_stream()
    {
        using var buffer = new MemoryStream(Array.Empty<byte>());
        Assert.Null(await AgentProtocol.ReadAsync(buffer, default));
    }

    [Fact]
    public async Task Protocol_throws_on_partial_header()
    {
        using var buffer = new MemoryStream(new byte[] { 0, 0, 0 }); // en-tête incomplet
        await Assert.ThrowsAsync<EndOfStreamException>(async () => await AgentProtocol.ReadAsync(buffer, default));
    }

    [Fact]
    public async Task Protocol_rejects_oversized_length()
    {
        var header = new byte[5];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)AgentProtocol.MaxMessageLength + 1);
        using var buffer = new MemoryStream(header);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await AgentProtocol.ReadAsync(buffer, default));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 2)]
    [InlineData(16, 9)]
    public void Png_encodes_a_decodable_image(int width, int height)
    {
        var rgba = new byte[width * height * 4];
        for (var i = 0; i < rgba.Length; i++)
        {
            rgba[i] = (byte)((i * 37) & 0xFF);
        }

        var png = PngEncoder.Encode(rgba, width, height);

        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, png[0..8]);
        var (w, h, pixels) = DecodePng(png);
        Assert.Equal(width, w);
        Assert.Equal(height, h);
        Assert.Equal(rgba, pixels);
    }

    [Fact]
    public void Input_parses_mouse_wheel_and_key()
    {
        Assert.True(InputEvent.TryParse(Encoding.UTF8.GetBytes("{\"t\":\"m\",\"x\":12,\"y\":34,\"b\":5}"), out var mouse));
        Assert.Equal(InputKind.Mouse, mouse.Kind);
        Assert.Equal((12, 34, 5), (mouse.X, mouse.Y, mouse.Buttons));

        Assert.True(InputEvent.TryParse(Encoding.UTF8.GetBytes("{\"t\":\"w\",\"x\":1,\"y\":2,\"d\":-120}"), out var wheel));
        Assert.Equal(InputKind.Wheel, wheel.Kind);
        Assert.Equal(-120, wheel.WheelDelta);

        Assert.True(InputEvent.TryParse(Encoding.UTF8.GetBytes("{\"t\":\"k\",\"d\":true,\"c\":\"KeyA\",\"k\":\"a\"}"), out var key));
        Assert.Equal(InputKind.Key, key.Kind);
        Assert.True(key.Down);
        Assert.Equal("KeyA", key.Code);
        Assert.Equal("a", key.Key);
    }

    [Fact]
    public void Input_rejects_malformed_json()
    {
        Assert.False(InputEvent.TryParse(Encoding.UTF8.GetBytes("not json"), out _));
        Assert.False(InputEvent.TryParse(Encoding.UTF8.GetBytes("{\"t\":\"x\"}"), out _));
    }

    [Theory]
    [InlineData("KeyA", 'A')]
    [InlineData("Enter", 0x0D)]
    [InlineData("ArrowLeft", 0x25)]
    [InlineData("Digit7", 0x37)]
    [InlineData("F5", 0x74)]
    public void Key_codes_map_to_virtual_keys(string code, int expected)
    {
        Assert.Equal(expected, KeyCodes.ToVirtualKey(code));
    }

    [Fact]
    public void Unknown_key_code_maps_to_zero()
    {
        Assert.Equal(0, KeyCodes.ToVirtualKey("Unidentified"));
    }

    private static (int Width, int Height, byte[] Pixels) DecodePng(byte[] png)
    {
        var width = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16));
        var height = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20));
        Assert.Equal(8, png[24]);  // profondeur
        Assert.Equal(6, png[25]);  // RGBA

        // Concatène les données IDAT, puis décompresse (zlib).
        using var idat = new MemoryStream();
        var pos = 8;
        while (pos < png.Length)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(pos));
            var type = Encoding.ASCII.GetString(png, pos + 4, 4);
            if (type == "IDAT")
            {
                idat.Write(png, pos + 8, length);
            }

            pos += 12 + length;
        }

        idat.Position = 0;
        using var inflate = new ZLibStream(idat, CompressionMode.Decompress);
        using var raw = new MemoryStream();
        inflate.CopyTo(raw);
        var scanlines = raw.ToArray();

        // Retire l'octet de filtre (0) en tête de chaque ligne.
        var stride = width * 4;
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            Assert.Equal(0, scanlines[y * (stride + 1)]);
            Array.Copy(scanlines, (y * (stride + 1)) + 1, pixels, y * stride, stride);
        }

        return (width, height, pixels);
    }
}
