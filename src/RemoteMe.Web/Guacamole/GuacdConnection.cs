using System.Globalization;
using System.Net.Sockets;
using System.Text;
using RemoteMe.Web.Configuration;

namespace RemoteMe.Web.Guacamole;

/// <summary>Connexion TCP à guacd, établie avec la poignée de main du protocole Guacamole 1.5.</summary>
public sealed class GuacdConnection : IAsyncDisposable
{
    private const string LatestVersion = "VERSION_1_5_0";
    private static readonly string[] KnownVersions = { "VERSION_1_0_0", "VERSION_1_1_0", "VERSION_1_3_0", LatestVersion };

    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;

    private GuacdConnection(TcpClient tcp)
    {
        _tcp = tcp;
        _stream = tcp.GetStream();
        Reader = new GuacStreamReader(_stream);
    }

    public GuacStreamReader Reader { get; }

    /// <summary>Identifiant de connexion attribué par guacd (instruction "ready").</summary>
    public string ConnectionId { get; private set; } = "";

    public static async Task<GuacdConnection> ConnectAsync(
        GuacdOptions guacd,
        string protocol,
        IReadOnlyDictionary<string, string> parameters,
        ClientInfo client,
        string clientName,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, guacd.ConnectTimeoutSeconds)));

        var tcp = new TcpClient { NoDelay = true };
        try
        {
            await tcp.ConnectAsync(guacd.Host, guacd.Port, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is SocketException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            tcp.Dispose();
            throw new GuacamoleException(GuacStatus.UpstreamNotFound, "guacd injoignable.", e);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }

        var connection = new GuacdConnection(tcp);
        try
        {
            await connection.HandshakeAsync(protocol, parameters, client, clientName, timeout.Token).ConfigureAwait(false);
            return connection;
        }
        catch (Exception e)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw e switch
            {
                OperationCanceledException when !cancellationToken.IsCancellationRequested =>
                    new GuacamoleException(GuacStatus.UpstreamTimeout, "guacd ne répond pas.", e),
                IOException => new GuacamoleException(GuacStatus.UpstreamError, "Connexion à guacd interrompue.", e),
                _ => e,
            };
        }
    }

    public ValueTask WriteAsync(string instructions, CancellationToken cancellationToken) =>
        _stream.WriteAsync(Encoding.UTF8.GetBytes(instructions), cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (_tcp.Connected)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await WriteAsync(GuacInstruction.Encode("disconnect"), cts.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException)
            {
                // guacd a déjà fermé la connexion.
            }
        }

        _tcp.Dispose();
    }

    private async Task HandshakeAsync(
        string protocol,
        IReadOnlyDictionary<string, string> parameters,
        ClientInfo client,
        string clientName,
        CancellationToken cancellationToken)
    {
        await WriteAsync(GuacInstruction.Encode("select", protocol), cancellationToken).ConfigureAwait(false);

        var args = await ExpectAsync("args", cancellationToken).ConfigureAwait(false);

        // Le premier argument annonce la version du protocole ; on répond avec la plus récente commune.
        var version = args.Args.Count > 0 && args.Args[0].StartsWith("VERSION_", StringComparison.Ordinal)
            ? NegotiateVersion(args.Args[0])
            : null;

        var values = args.Args
            .Select((name, index) => index == 0 && version is not null
                ? version
                : parameters.TryGetValue(name, out var value) ? value : "")
            .ToList();

        var dpi = client.Dpi.ToString(CultureInfo.InvariantCulture);
        var handshake = new StringBuilder()
            .Append(GuacInstruction.Encode("size",
                client.Width.ToString(CultureInfo.InvariantCulture),
                client.Height.ToString(CultureInfo.InvariantCulture),
                dpi))
            .Append(GuacInstruction.Encode("audio", client.AudioMimetypes))
            .Append(GuacInstruction.Encode("video"))
            .Append(GuacInstruction.Encode("image", client.ImageMimetypes));

        if (version is not null && version != "VERSION_1_0_0" && client.Timezone is not null)
        {
            handshake.Append(GuacInstruction.Encode("timezone", client.Timezone));
        }

        if (version == LatestVersion)
        {
            handshake.Append(GuacInstruction.Encode("name", clientName));
        }

        handshake.Append(GuacInstruction.Encode("connect", values));
        await WriteAsync(handshake.ToString(), cancellationToken).ConfigureAwait(false);

        var ready = await ExpectAsync("ready", cancellationToken).ConfigureAwait(false);
        ConnectionId = ready.Args.Count > 0 ? ready.Args[0] : "";
    }

    private static string NegotiateVersion(string advertised)
    {
        var index = Array.IndexOf(KnownVersions, advertised);
        return index >= 0 ? advertised : LatestVersion;
    }

    private async Task<GuacInstruction> ExpectAsync(string opcode, CancellationToken cancellationToken)
    {
        GuacInstruction? instruction;
        try
        {
            instruction = await Reader.ReadInstructionAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (FormatException e)
        {
            throw new GuacamoleException(GuacStatus.UpstreamError, "Réponse de guacd invalide.", e);
        }

        if (instruction is null)
        {
            throw new GuacamoleException(GuacStatus.UpstreamError, $"guacd a fermé la connexion avant « {opcode} ».");
        }

        if (instruction.Opcode == "error")
        {
            var status = instruction.Args.Count > 1
                && int.TryParse(instruction.Args[1], NumberStyles.None, CultureInfo.InvariantCulture, out var code)
                ? (GuacStatus)code
                : GuacStatus.ServerError;
            throw new GuacamoleException(status, instruction.Args.Count > 0 ? instruction.Args[0] : "Erreur guacd.");
        }

        if (instruction.Opcode != opcode)
        {
            throw new GuacamoleException(
                GuacStatus.UpstreamError, $"guacd a répondu « {instruction.Opcode} » au lieu de « {opcode} ».");
        }

        return instruction;
    }
}
