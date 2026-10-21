using System.Buffers.Binary;
using System.Text;

namespace RemoteMe.WindowAgent;

/// <summary>
/// Trame du canal agent ↔ serveur (TCP sur localhost) : <c>[u32 longueur BE][octet type][charge]</c>.
/// La longueur compte l'octet de type et la charge. Simple et sans dépendance : l'agent et le
/// serveur n'ont besoin que de ce format.
/// </summary>
public static class AgentProtocol
{
    /// <summary>
    /// Agent → serveur, premier message : jeton partagé passé à l'agent au lancement.
    /// Il authentifie l'agent auprès du serveur (le canal est sur localhost, mais tout processus
    /// local pourrait sinon se connecter au port d'écoute).
    /// </summary>
    public const byte Hello = (byte)'H';

    /// <summary>Agent → serveur : image complète de la fenêtre, au format PNG.</summary>
    public const byte Frame = (byte)'F';

    /// <summary>Agent → serveur : métadonnées JSON (taille, titre, fin de l'application).</summary>
    public const byte Meta = (byte)'M';

    /// <summary>Serveur → agent : évènement clavier/souris JSON venu du navigateur.</summary>
    public const byte Input = (byte)'I';

    /// <summary>Taille maximale d'un message, garde-fou contre un flux corrompu.</summary>
    public const int MaxMessageLength = 32 * 1024 * 1024;

    public static async Task WriteAsync(Stream stream, byte type, ReadOnlyMemory<byte> payload, CancellationToken token)
    {
        var header = new byte[5];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)(payload.Length + 1));
        header[4] = type;
        await stream.WriteAsync(header, token).ConfigureAwait(false);
        await stream.WriteAsync(payload, token).ConfigureAwait(false);
    }

    public static Task WriteJsonAsync(Stream stream, byte type, string json, CancellationToken token) =>
        WriteAsync(stream, type, Encoding.UTF8.GetBytes(json), token);

    /// <summary>Lit un message complet, ou null en fin de flux.</summary>
    public static async Task<(byte Type, byte[] Payload)?> ReadAsync(Stream stream, CancellationToken token)
    {
        var header = new byte[5];
        if (!await ReadExactlyAsync(stream, header, token).ConfigureAwait(false))
        {
            return null;
        }

        var length = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (length is 0 or > MaxMessageLength)
        {
            throw new InvalidDataException($"Longueur de message invalide : {length}.");
        }

        var payload = new byte[length - 1];
        if (payload.Length > 0 && !await ReadExactlyAsync(stream, payload, token).ConfigureAwait(false))
        {
            throw new EndOfStreamException("Message tronqué.");
        }

        return (header[4], payload);
    }

    private static async Task<bool> ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken token)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer[read..], token).ConfigureAwait(false);
            if (n == 0)
            {
                return read == 0 ? false : throw new EndOfStreamException("Flux interrompu en milieu de message.");
            }

            read += n;
        }

        return true;
    }
}
