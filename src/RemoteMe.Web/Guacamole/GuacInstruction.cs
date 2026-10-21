using System.Globalization;
using System.Text;

namespace RemoteMe.Web.Guacamole;

/// <summary>
/// Instruction du protocole Guacamole : <c>LONGUEUR.VALEUR,LONGUEUR.VALEUR,...;</c>,
/// où chaque longueur est exprimée en points de code Unicode.
/// </summary>
public sealed class GuacInstruction
{
    public GuacInstruction(string opcode, params string[] args)
        : this(opcode, (IReadOnlyList<string>)args)
    {
    }

    public GuacInstruction(string opcode, IReadOnlyList<string> args)
    {
        Opcode = opcode;
        Args = args;
    }

    public string Opcode { get; }

    public IReadOnlyList<string> Args { get; }

    public static string Encode(string opcode, IEnumerable<string> args)
    {
        var builder = new StringBuilder();
        AppendElement(builder, opcode);
        foreach (var arg in args)
        {
            builder.Append(',');
            AppendElement(builder, arg);
        }

        return builder.Append(';').ToString();
    }

    public static string Encode(string opcode, params string[] args) => Encode(opcode, (IEnumerable<string>)args);

    /// <summary>Analyse le texte d'une ou plusieurs instructions complètes.</summary>
    public static List<GuacInstruction> ParseAll(string text)
    {
        var result = new List<GuacInstruction>();
        var index = 0;
        while (index < text.Length)
        {
            var end = GuacParser.FindInstructionEnd(text.AsSpan(), index, out var elements);
            if (end < 0)
            {
                throw new FormatException("Instruction Guacamole incomplète.");
            }

            result.Add(new GuacInstruction(elements![0], elements.GetRange(1, elements.Count - 1)));
            index = end;
        }

        return result;
    }

    public override string ToString() => Encode(Opcode, Args);

    private static void AppendElement(StringBuilder builder, string value)
    {
        var codePoints = 0;
        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsHighSurrogate(value[i]) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                i++;
            }

            codePoints++;
        }

        builder.Append(codePoints.ToString(CultureInfo.InvariantCulture)).Append('.').Append(value);
    }
}

/// <summary>Découpage d'instructions Guacamole dans un tampon de caractères.</summary>
public static class GuacParser
{
    /// <summary>Taille maximale d'un élément, au-delà de laquelle le flux est considéré comme corrompu.</summary>
    public const int MaxElementLength = 1 << 20;

    /// <summary>
    /// Cherche la fin de l'instruction qui commence à <paramref name="start"/>.
    /// Renvoie l'index qui suit le ';' final, ou -1 si l'instruction n'est pas encore complète.
    /// </summary>
    /// <exception cref="FormatException">Le flux n'est pas du protocole Guacamole valide.</exception>
    public static int FindInstructionEnd(ReadOnlySpan<char> buffer, int start) =>
        Scan(buffer, start, null);

    /// <inheritdoc cref="FindInstructionEnd(ReadOnlySpan{char}, int)"/>
    public static int FindInstructionEnd(ReadOnlySpan<char> buffer, int start, out List<string>? elements)
    {
        var collected = new List<string>();
        var end = Scan(buffer, start, collected);
        elements = end < 0 ? null : collected;
        return end;
    }

    private static int Scan(ReadOnlySpan<char> buffer, int start, List<string>? elements)
    {
        var position = start;
        while (true)
        {
            // Longueur décimale, puis '.'
            var length = 0;
            var digits = 0;
            while (true)
            {
                if (position >= buffer.Length)
                {
                    return -1;
                }

                var c = buffer[position];
                if (c == '.')
                {
                    break;
                }

                if (c < '0' || c > '9' || ++digits > 8)
                {
                    throw new FormatException("Longueur d'élément invalide.");
                }

                length = (length * 10) + (c - '0');
                position++;
            }

            if (digits == 0 || length > MaxElementLength)
            {
                throw new FormatException("Longueur d'élément invalide.");
            }

            position++;

            // Valeur : 'length' points de code (une paire de substitution compte pour un).
            var valueStart = position;
            for (var remaining = length; remaining > 0; remaining--)
            {
                if (position >= buffer.Length)
                {
                    return -1;
                }

                if (char.IsHighSurrogate(buffer[position]))
                {
                    if (position + 1 >= buffer.Length)
                    {
                        return -1;
                    }

                    position++;
                }

                position++;
            }

            if (position >= buffer.Length)
            {
                return -1;
            }

            elements?.Add(buffer[valueStart..position].ToString());

            var terminator = buffer[position++];
            if (terminator == ';')
            {
                return position;
            }

            if (terminator != ',')
            {
                throw new FormatException("Séparateur d'élément invalide.");
            }
        }
    }
}
