using System.Text;

namespace RemoteMe.Web.Guacamole;

/// <summary>
/// Lit un flux guacd et le restitue par instructions complètes : le tunnel WebSocket de
/// guacamole-common-js exige que chaque message ne contienne que des instructions entières.
/// </summary>
public sealed class GuacStreamReader
{
    private readonly Stream _stream;
    private readonly Decoder _decoder = new UTF8Encoding(false, true).GetDecoder();
    private readonly byte[] _bytes;
    private char[] _chars;
    private int _start;
    private int _end;

    public GuacStreamReader(Stream stream, int bufferSize = 16 * 1024)
    {
        _stream = stream;
        _bytes = new byte[bufferSize];
        _chars = new char[bufferSize * 2];
    }

    /// <summary>
    /// Renvoie une ou plusieurs instructions complètes (texte brut, au plus ~<paramref name="maxChars"/>
    /// caractères sauf instruction plus longue), ou null en fin de flux.
    /// </summary>
    public async ValueTask<string?> ReadBatchAsync(int maxChars, CancellationToken cancellationToken)
    {
        while (true)
        {
            var position = _start;
            while (position < _end && position - _start < maxChars)
            {
                var next = GuacParser.FindInstructionEnd(_chars.AsSpan(0, _end), position);
                if (next < 0)
                {
                    break;
                }

                position = next;
            }

            if (position > _start)
            {
                var batch = new string(_chars, _start, position - _start);
                _start = position;
                return batch;
            }

            if (!await FillAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }
        }
    }

    /// <summary>Lit une seule instruction analysée (utilisé pendant la poignée de main).</summary>
    public async ValueTask<GuacInstruction?> ReadInstructionAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var next = GuacParser.FindInstructionEnd(_chars.AsSpan(0, _end), _start, out var elements);
            if (next >= 0)
            {
                _start = next;
                return new GuacInstruction(elements![0], elements.GetRange(1, elements.Count - 1));
            }

            if (!await FillAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }
        }
    }

    private async ValueTask<bool> FillAsync(CancellationToken cancellationToken)
    {
        // Compacte le tampon, puis l'agrandit si une instruction ne tient pas.
        if (_start > 0)
        {
            Array.Copy(_chars, _start, _chars, 0, _end - _start);
            _end -= _start;
            _start = 0;
        }

        // N octets UTF-8 donnent au plus N caractères, plus ceux d'une séquence restée en attente.
        var needed = _end + _bytes.Length + 4;
        if (needed > _chars.Length)
        {
            if (_chars.Length >= GuacParser.MaxElementLength * 4)
            {
                throw new FormatException("Instruction Guacamole trop longue.");
            }

            Array.Resize(ref _chars, Math.Max(_chars.Length * 2, needed));
        }

        var read = await _stream.ReadAsync(_bytes.AsMemory(), cancellationToken).ConfigureAwait(false);
        if (read == 0)
        {
            return false;
        }

        _end += _decoder.GetChars(_bytes, 0, read, _chars, _end, flush: false);
        return true;
    }
}
