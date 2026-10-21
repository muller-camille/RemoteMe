using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using Microsoft.Extensions.Options;
using RemoteMe.Web.Catalog;
using RemoteMe.Web.Configuration;
using RemoteMe.Web.Environments;
using RemoteMe.Web.Http;

namespace RemoteMe.Web.Guacamole;

/// <summary>
/// Point de terminaison WebSocket compatible avec <c>Guacamole.WebSocketTunnel</c> :
/// attribue l'environnement du visiteur, ouvre la connexion RDP via guacd puis relaie le flux.
/// </summary>
public sealed class GuacamoleTunnel
{
    public const string SubProtocol = "guacamole";

    private const int MaxBatchChars = 8 * 1024;
    private const int MaxClientMessageBytes = 1024 * 1024;

    private readonly AppCatalog _catalog;
    private readonly EnvironmentPool _pool;
    private readonly IOptions<RemoteMeOptions> _options;
    private readonly ILogger<GuacamoleTunnel> _logger;

    public GuacamoleTunnel(
        AppCatalog catalog, EnvironmentPool pool, IOptions<RemoteMeOptions> options, ILogger<GuacamoleTunnel> logger)
    {
        _catalog = catalog;
        _pool = pool;
        _options = options;
        _logger = logger;
    }

    public async Task HandleAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        if (!RequestGuards.IsSameOrigin(context.Request))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var visitorId = VisitorCookie.Read(context.Request);
        var subProtocol = context.WebSockets.WebSocketRequestedProtocols.Contains(SubProtocol) ? SubProtocol : null;
        using var socket = await context.WebSockets.AcceptWebSocketAsync(subProtocol);
        var sender = new SocketSender(socket);
        var aborted = context.RequestAborted;

        // Les erreurs sont signalées par le motif de fermeture WebSocket : guacamole-common-js
        // le lit comme un code de statut Guacamole et l'affiche dans la page.
        if (visitorId is null)
        {
            await CloseAndDrainAsync(sender, socket, GuacStatus.ClientUnauthorized, aborted);
            return;
        }

        var app = _catalog.Find(context.Request.Query["app"].FirstOrDefault());
        if (app is null)
        {
            await CloseAndDrainAsync(sender, socket, GuacStatus.ResourceNotFound, aborted);
            return;
        }

        var open = _pool.OpenConnection(visitorId, context.Connection.RemoteIpAddress?.ToString());
        if (open.Ticket is null)
        {
            _logger.LogWarning("Connexion refusée pour {Env} : {Failure}.", EnvironmentPool.ShortIdOf(visitorId), open.Failure);
            await CloseAndDrainAsync(sender, socket, open.Status, aborted);
            return;
        }

        using var ticket = open.Ticket;
        using var upstreamCancel = CancellationTokenSource.CreateLinkedTokenSource(aborted, ticket.Aborted);
        var options = _options.Value;
        var client = ClientInfo.FromQuery(context.Request.Query);
        var parameters = RdpParameters.Build(options.Rdp, app, ticket.Account, ticket.EnvironmentKey, client);

        GuacdConnection guacd;
        try
        {
            guacd = await GuacdConnection.ConnectAsync(
                options.Guacd, "rdp", parameters, client, options.Rdp.ClientName, upstreamCancel.Token);
        }
        catch (GuacamoleException e)
        {
            _logger.LogWarning(e, "Ouverture de {App} impossible pour {Env} ({Account}).", app.Id, ticket.EnvironmentShortId, ticket.Account);
            await CloseAndDrainAsync(sender, socket, e.Status, aborted);
            return;
        }
        catch (OperationCanceledException)
        {
            await CloseAndDrainAsync(sender, socket, ticket.AbortReason ?? GuacStatus.Success, aborted);
            return;
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Erreur inattendue à l'ouverture de {App} pour {Env}.", app.Id, ticket.EnvironmentShortId);
            await CloseAndDrainAsync(sender, socket, GuacStatus.ServerError, aborted);
            return;
        }

        await using (guacd)
        {
            _logger.LogInformation(
                "{App} ouvert pour {Env} ({Account}, guacd {ConnectionId}).",
                app.Id, ticket.EnvironmentShortId, ticket.Account, guacd.ConnectionId);

            var status = await RelayAsync(sender, socket, guacd, ticket, upstreamCancel, aborted);
            _logger.LogInformation("{App} fermé pour {Env} ({Status}).", app.Id, ticket.EnvironmentShortId, status);
        }
    }

    /// <summary>
    /// Relaie le flux dans les deux sens jusqu'à ce que l'un des côtés s'arrête, puis ferme
    /// proprement le WebSocket avec le statut correspondant.
    /// </summary>
    private async Task<GuacStatus> RelayAsync(
        SocketSender sender, WebSocket socket, GuacdConnection guacd, ConnectionTicket ticket,
        CancellationTokenSource upstreamCancel, CancellationToken aborted)
    {
        // Première instruction attendue par le client : l'UUID du tunnel (opcode interne vide).
        await sender.SendAsync(GuacInstruction.Encode("", Guid.NewGuid().ToString()), aborted);

        var upstream = PumpFromGuacdAsync(sender, guacd, upstreamCancel.Token);
        // La réception n'est pas liée à upstreamCancel : annuler une réception WebSocket
        // interrompt la socket et empêcherait d'envoyer le motif de fermeture.
        var downstream = PumpFromBrowserAsync(sender, socket, guacd, aborted);

        var first = await Task.WhenAny(upstream, downstream);
        // Environnement libéré pendant la connexion (expiration, réinitialisation) : ce motif prime.
        var status = ticket.AbortReason ?? (first.IsCompletedSuccessfully
            ? first.Result
            : first == upstream && !upstreamCancel.IsCancellationRequested ? GuacStatus.UpstreamError : GuacStatus.Success);

        upstreamCancel.Cancel();
        await CloseAndDrainAsync(sender, socket, status, aborted, downstream);

        foreach (var task in new[] { upstream, downstream })
        {
            try
            {
                await task;
            }
            catch (Exception e) when (e is OperationCanceledException or IOException or WebSocketException or ObjectDisposedException)
            {
                // Fin de connexion attendue.
            }
        }

        return status;
    }

    private async Task<GuacStatus> PumpFromGuacdAsync(SocketSender sender, GuacdConnection guacd, CancellationToken token)
    {
        try
        {
            while (true)
            {
                var batch = await guacd.Reader.ReadBatchAsync(MaxBatchChars, token);
                if (batch is null)
                {
                    return GuacStatus.Success;
                }

                await sender.SendAsync(batch, token);
            }
        }
        catch (FormatException e)
        {
            _logger.LogWarning(e, "Flux guacd invalide.");
            return GuacStatus.UpstreamError;
        }
    }

    private static async Task<GuacStatus> PumpFromBrowserAsync(
        SocketSender sender, WebSocket socket, GuacdConnection guacd, CancellationToken token)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();

        while (true)
        {
            message.SetLength(0);
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, token);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return GuacStatus.Success;
                }

                message.Write(buffer, 0, result.Count);
                if (message.Length > MaxClientMessageBytes)
                {
                    return GuacStatus.ClientOverrun;
                }
            }
            while (!result.EndOfMessage);

            var text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);

            // Instructions internes au tunnel (opcode vide) : le "ping" est renvoyé tel quel,
            // les autres ne concernent pas guacd.
            if (text.StartsWith("0.,", StringComparison.Ordinal))
            {
                if (text.StartsWith("0.,4.ping,", StringComparison.Ordinal))
                {
                    await sender.SendAsync(text, token);
                }

                continue;
            }

            try
            {
                await guacd.WriteAsync(text, token);
            }
            catch (IOException)
            {
                return GuacStatus.UpstreamError;
            }
        }
    }

    /// <summary>
    /// Envoie la trame de fermeture (motif = code de statut Guacamole), puis attend l'accusé du
    /// navigateur : soit via la boucle de réception déjà en cours, soit en lisant jusqu'à la trame Close.
    /// </summary>
    private static async Task CloseAndDrainAsync(
        SocketSender sender, WebSocket socket, GuacStatus status, CancellationToken token, Task? receiveLoop = null)
    {
        var code = status == GuacStatus.Success ? WebSocketCloseStatus.NormalClosure : WebSocketCloseStatus.InternalServerError;
        await sender.CloseOutputAsync(code, ((int)status).ToString(CultureInfo.InvariantCulture), token);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            if (receiveLoop is not null)
            {
                await receiveLoop.WaitAsync(timeout.Token);
                return;
            }

            var buffer = new byte[1024];
            while (socket.State == WebSocketState.CloseSent)
            {
                var result = await socket.ReceiveAsync(buffer, timeout.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }
            }
        }
        catch (Exception e) when (e is OperationCanceledException or TimeoutException or WebSocketException or ObjectDisposedException or IOException)
        {
            socket.Abort();
        }
    }

    /// <summary>Sérialise les envois : WebSocket n'accepte qu'un envoi à la fois.</summary>
    private sealed class SocketSender
    {
        private readonly WebSocket _socket;
        private readonly SemaphoreSlim _lock = new(1, 1);

        public SocketSender(WebSocket socket) => _socket = socket;

        public async Task SendAsync(string text, CancellationToken token)
        {
            await _lock.WaitAsync(token);
            try
            {
                // Après l'envoi de la trame de fermeture, plus rien ne doit partir.
                if (_socket.State != WebSocketState.Open)
                {
                    return;
                }

                await _socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, token);
            }
            finally
            {
                _lock.Release();
            }
        }

        public async Task CloseOutputAsync(WebSocketCloseStatus code, string description, CancellationToken token)
        {
            if (_socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
            {
                return;
            }

            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                await _lock.WaitAsync(timeout.Token);
                try
                {
                    await _socket.CloseOutputAsync(code, description, timeout.Token);
                }
                finally
                {
                    _lock.Release();
                }
            }
            catch (Exception e) when (e is OperationCanceledException or WebSocketException or ObjectDisposedException)
            {
                // Le navigateur est déjà parti.
            }
        }
    }
}
