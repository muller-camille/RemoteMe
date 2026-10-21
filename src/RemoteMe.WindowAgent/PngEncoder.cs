using System.Buffers.Binary;
using System.IO.Compression;

namespace RemoteMe.WindowAgent;

/// <summary>
/// Encodeur PNG minimal (couleur vraie 8 bits + alpha), sans dépendance à System.Drawing :
/// il fonctionne donc aussi bien sous Windows que dans l'agent factice sous Linux.
/// </summary>
public static class PngEncoder
{
    private static readonly byte[] Signature = { 137, 80, 78, 71, 13, 10, 26, 10 };

    /// <summary>Encode des pixels RGBA (ligne par ligne, de haut en bas) en PNG.</summary>
    public static byte[] Encode(ReadOnlySpan<byte> rgba, int width, int height)
    {
        if (rgba.Length < width * height * 4)
        {
            throw new ArgumentException("Tampon de pixels trop petit.", nameof(rgba));
        }

        using var output = new MemoryStream();
        output.Write(Signature);

        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr[0..], width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr[4..], height);
        ihdr[8] = 8;   // profondeur par canal
        ihdr[9] = 6;   // couleur vraie + alpha (RGBA)
        ihdr[10] = 0;  // compression deflate
        ihdr[11] = 0;  // filtrage standard
        ihdr[12] = 0;  // pas d'entrelacement
        WriteChunk(output, "IHDR", ihdr);

        // Lignes filtrées (filtre 0 = aucun), puis compression zlib.
        var stride = width * 4;
        var raw = new byte[height * (stride + 1)];
        for (var y = 0; y < height; y++)
        {
            var dst = y * (stride + 1);
            raw[dst] = 0;
            rgba.Slice(y * stride, stride).CopyTo(raw.AsSpan(dst + 1));
        }

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            zlib.Write(raw);
        }

        WriteChunk(output, "IDAT", compressed.GetBuffer().AsSpan(0, (int)compressed.Length));
        WriteChunk(output, "IEND", ReadOnlySpan<byte>.Empty);
        return output.ToArray();
    }

    private static void WriteChunk(Stream stream, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> header = stackalloc byte[8];
        BinaryPrimitives.WriteInt32BigEndian(header, data.Length);
        for (var i = 0; i < 4; i++)
        {
            header[4 + i] = (byte)type[i];
        }

        stream.Write(header);
        stream.Write(data);

        var crc = Crc32.Compute(header[4..]);
        crc = Crc32.Continue(crc, data);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc ^ 0xFFFFFFFFu);
        stream.Write(crcBytes);
    }

    private static class Crc32
    {
        private static readonly uint[] Table = BuildTable();

        public static uint Compute(ReadOnlySpan<byte> data) => Continue(0xFFFFFFFFu, data);

        public static uint Continue(uint crc, ReadOnlySpan<byte> data)
        {
            foreach (var b in data)
            {
                crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            }

            return crc;
        }

        private static uint[] BuildTable()
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                var c = n;
                for (var k = 0; k < 8; k++)
                {
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                }

                table[n] = c;
            }

            return table;
        }
    }
}
