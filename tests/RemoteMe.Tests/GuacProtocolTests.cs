using System.Text;
using RemoteMe.Web.Guacamole;

namespace RemoteMe.Tests;

public class GuacProtocolTests
{
    [Fact]
    public void Encode_counts_code_points_not_utf16_units()
    {
        // "é" = 1 point de code, "😀" = 1 point de code (2 unités UTF-16).
        Assert.Equal("4.name,3.é😀a;", GuacInstruction.Encode("name", "é😀a"));
    }

    [Fact]
    public void Encode_handles_empty_opcode_and_no_args()
    {
        Assert.Equal("0.,4.ping;", GuacInstruction.Encode("", "ping"));
        Assert.Equal("5.video;", GuacInstruction.Encode("video"));
    }

    [Fact]
    public void ParseAll_round_trips()
    {
        var text = GuacInstruction.Encode("args", "VERSION_1_5_0", "hostname", "😀") + GuacInstruction.Encode("ready", "$abc");
        var parsed = GuacInstruction.ParseAll(text);

        Assert.Equal(2, parsed.Count);
        Assert.Equal("args", parsed[0].Opcode);
        Assert.Equal(new[] { "VERSION_1_5_0", "hostname", "😀" }, parsed[0].Args);
        Assert.Equal("ready", parsed[1].Opcode);
        Assert.Equal("$abc", Assert.Single(parsed[1].Args));
    }

    [Theory]
    [InlineData("4.sync")]
    [InlineData("4.sync,")]
    [InlineData("4.sync,2.1")]
    [InlineData("4.sync,2.12")]
    [InlineData("1")]
    public void FindInstructionEnd_reports_incomplete_input(string text)
    {
        Assert.Equal(-1, GuacParser.FindInstructionEnd(text.AsSpan(), 0));
    }

    [Theory]
    [InlineData("x.sync;")]
    [InlineData(".sync;")]
    [InlineData("4.sync!")]
    [InlineData("999999999.a;")]
    public void FindInstructionEnd_rejects_malformed_input(string text)
    {
        Assert.Throws<FormatException>(() => GuacParser.FindInstructionEnd(text.AsSpan(), 0));
    }

    [Fact]
    public async Task Reader_returns_only_whole_instructions_even_when_bytes_trickle_in()
    {
        var instructions = new[]
        {
            GuacInstruction.Encode("sync", "1234"),
            GuacInstruction.Encode("name", "Café 😀 ünïcode"),
            GuacInstruction.Encode("blob", "0", new string('A', 5000)),
            GuacInstruction.Encode("sync", "1235"),
        };
        var all = string.Concat(instructions);
        var reader = new GuacStreamReader(new TrickleStream(Encoding.UTF8.GetBytes(all)), bufferSize: 64);

        var received = new StringBuilder();
        while (await reader.ReadBatchAsync(64, CancellationToken.None) is { } batch)
        {
            // Chaque lot doit pouvoir être analysé seul : aucune instruction coupée.
            Assert.NotEmpty(GuacInstruction.ParseAll(batch));
            received.Append(batch);
        }

        Assert.Equal(all, received.ToString());
    }

    [Fact]
    public async Task Reader_parses_single_instructions()
    {
        var bytes = Encoding.UTF8.GetBytes(GuacInstruction.Encode("args", "VERSION_1_5_0", "hostname") + "5.ready,4.$id1;");
        var reader = new GuacStreamReader(new TrickleStream(bytes));

        var args = await reader.ReadInstructionAsync(CancellationToken.None);
        var ready = await reader.ReadInstructionAsync(CancellationToken.None);
        var end = await reader.ReadInstructionAsync(CancellationToken.None);

        Assert.Equal("args", args!.Opcode);
        Assert.Equal("hostname", args.Args[1]);
        Assert.Equal("$id1", ready!.Args[0]);
        Assert.Null(end);
    }

    /// <summary>Flux qui ne rend qu'un octet par lecture (pire cas de découpage TCP).</summary>
    private sealed class TrickleStream : MemoryStream
    {
        public TrickleStream(byte[] data)
            : base(data)
        {
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], cancellationToken);
    }
}
