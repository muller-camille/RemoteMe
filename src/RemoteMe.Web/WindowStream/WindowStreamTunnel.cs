using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using Microsoft.Extensions.Options;
using RemoteMe.Web.Catalog;
using RemoteMe.Web.Http;
using RemoteMe.WindowAgent;

namespace RemoteMe.Web.WindowStream;

/// <summary>
/// Point de terminaison WebSocket de la diffusion par fenêtre. Pour chaque connexion : lance une
/// instance de l'application via l'agent, puis relaie les images (binaire) vers le navigateur et
/// les entrées clavier/souris (texte JSON) vers l'agent. Chaque visiteur a donc sa propre instance.
/// </summary>
public sealed class WindowStreamTunnel
{
    private readonly AppCatalog _catalog;
    private readonly IOptions<WindowStreamOptions> _options;
    private readonly ILogger<WindowStreamTunnel> _logger;

    private readonly ConcurrentDictionary<string, int> _perIp = new(StringComparer.Ordinal);
    private int _active;

    public WindowStreamTunnel(AppCatalog catalog, IOptions<WindowStreamOptions> options, ILogger<WindowStreamTunnel> logger)
    {
        _catalog = catalog;
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

        var options = _options.Value;
        var app = _catalog.Find(context.Request.Query["app"].FirstOrDefault());
        var ip = context.Connection.RemoteIpAddress?.ToString() ?? "?";

        using var socket = await context.WebSockets.AcceptWebSocketAsync();

        if (string.IsNullOrWhiteSpace(options.AgentCommand))
        {
            await FailAsync(socket, "unconfigured", "La diffusion par fenêtre n'est pas configurée sur ce serveur.", context.RequestAborted);
            return;
        }

        if (app is null || app.Backend != "window")
        {
            await FailAsync(socket, "notfound", "Application introuvable.", context.RequestAborted);
            return;
        }

        if (!TryAcquire(ip))
        {
            _logger.LogWarning("Instance refusée pour {App} (IP {Ip}) : limite atteinte.", app.Id, ip);
            await FailAsync(socket, "busy", "Trop d'instances ouvertes. Réessayez dans un instant.", context.RequestAborted);
            return;
        }

        try
        {
            await RunInstanceAsync(context, socket, app, options, ip);
        }
        finally
        {
            Release(ip);
        }
    }

    private async Task RunInstanceAsync(
        HttpContext context, WebSocket socket, AppDefinition app, WindowStreamOptions options, string ip)
    {
        var width = ParseSize(context.Request.Query["width"].FirstOrDefault(), 1024, 320, 3840);
        var height = ParseSize(context.Request.Query["height"].FirstOrDefault(), 768, 240, 2160);
        var token = Guid.NewGuid().ToString("N");

        // Écoute sur un port localhost éphémère ; l'agent s'y connecte et s'authentifie par le jeton.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        Process? agentProcess = null;
        TcpClient? agentConn = null;
        try
        {
            using var startTimeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            startTimeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, options.AgentStartTimeoutSeconds)));

            agentProcess = StartAgent(options, app, port, token, width, height);

            try
            {
                agentConn = await listener.AcceptTcpClientAsync(startTimeout.Token);
            }
            catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
            {
                _logger.LogWarning("L'agent de {App} ne s'est pas connecté à temps.", app.Id);
                await FailAsync(socket, "agent", "L'application n'a pas démarré à temps.", context.RequestAborted);
                return;
            }
            finally
            {
                listener.Stop();
            }

            agentConn.NoDelay = true;
            var agentStream = agentConn.GetStream();

            if (!await VerifyHelloAsync(agentStream, token, context.RequestAborted))
            {
                _logger.LogWarning("Agent de {App} rejeté : jeton invalide.", app.Id);
                await FailAsync(socket, "agent", "Agent non authentifié.", context.RequestAborted);
                return;
            }

            _logger.LogInformation("Instance {App} ouverte (IP {Ip}, agent PID {Pid}).", app.Id, ip, agentProcess.Id);

            using var session = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            var toBrowser = PumpAgentToBrowserAsync(agentStream, socket, session);
            var toAgent = PumpBrowserToAgentAsync(socket, agentStream, session);
            await Task.WhenAny(toBrowser, toAgent);
            session.Cancel();
            await Task.WhenAll(
                Swallow(toBrowser),
                Swallow(toAgent));

            await CloseAsync(socket, context.RequestAborted);
            _logger.LogInformation("Instance {App} fermée (IP {Ip}).", app.Id, ip);
        }
        finally
        {
            listener.Stop();
            agentConn?.Dispose();
            KillAgent(agentProcess, app.Id);
        }
    }

    private Process StartAgent(WindowStreamOptions options, AppDefinition app, int port, string token, int width, int height)
    {
        var start = new ProcessStartInfo(options.AgentCommand)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var prefix in options.AgentArgumentPrefix)
        {
            start.ArgumentList.Add(prefix);
        }

        void Add(string name, string value)
        {
            start.ArgumentList.Add("--" + name);
            start.ArgumentList.Add(value);
        }

        Add("port", port.ToString(CultureInfo.InvariantCulture));
        Add("token", token);
        Add("fps", options.Fps.ToString(CultureInfo.InvariantCulture));
        Add("program", app.Program ?? "");
        if (!string.IsNullOrWhiteSpace(app.Arguments))
        {
            Add("args", app.Arguments);
        }

        if (!string.IsNullOrWhiteSpace(app.WorkingDirectory))
        {
            Add("dir", app.WorkingDirectory);
        }

        Add("width", width.ToString(CultureInfo.InvariantCulture));
        Add("height", height.ToString(CultureInfo.InvariantCulture));
        if (options.Fake)
        {
            start.ArgumentList.Add("--fake");
        }

        return Process.Start(start) ?? throw new InvalidOperationException("Impossible de lancer l'agent.");
    }

    private static async Task<bool> VerifyHelloAsync(Stream stream, string token, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var message = await AgentProtocol.ReadAsync(stream, timeout.Token);
            return message is { Type: AgentProtocol.Hello }
                && Encoding.UTF8.GetString(message.Value.Payload) == token;
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or InvalidDataException)
        {
            return false;
        }
    }

    private async Task PumpAgentToBrowserAsync(Stream agentStream, WebSocket socket, CancellationTokenSource session)
    {
        var token = session.Token;
        try
        {
            while (await AgentProtocol.ReadAsync(agentStream, token) is { } message)
            {
                switch (message.Type)
                {
                    case AgentProtocol.Frame:
                        await socket.SendAsync(message.Payload, WebSocketMessageType.Binary, true, token);
                        break;
                    case AgentProtocol.Meta:
                        await socket.SendAsync(message.Payload, WebSocketMessageType.Text, true, token);
                        break;
                }
            }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or WebSocketException or InvalidDataException)
        {
        }
        finally
        {
            session.Cancel();
        }
    }

    private static async Task PumpBrowserToAgentAsync(WebSocket socket, Stream agentStream, CancellationTokenSource session)
    {
        var token = session.Token;
        var buffer = new byte[8 * 1024];
        try
        {
            while (true)
            {
                var result = await socket.ReceiveAsync(buffer, token);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }

                // Seules les entrées texte (JSON) sont relayées à l'agent ; une entrée trop longue
                // ou binaire est ignorée.
                if (result.MessageType == WebSocketMessageType.Text && result.EndOfMessage && result.Count <= buffer.Length)
                {
                    await AgentProtocol.WriteAsync(agentStream, AgentProtocol.Input, buffer.AsMemory(0, result.Count), token);
                }
            }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or WebSocketException)
        {
        }
        finally
        {
            session.Cancel();
        }
    }

    private bool TryAcquire(string ip)
    {
        var options = _options.Value;
        if (Interlocked.Increment(ref _active) > options.MaxInstances)
        {
            Interlocked.Decrement(ref _active);
            return false;
        }

        var ok = true;
        _perIp.AddOrUpdate(ip, 1, (_, current) =>
        {
            if (current >= Math.Max(1, options.MaxInstancesPerIp))
            {
                ok = false;
                return current;
            }

            return current + 1;
        });

        if (!ok)
        {
            Interlocked.Decrement(ref _active);
        }

        return ok;
    }

    private void Release(string ip)
    {
        Interlocked.Decrement(ref _active);
        _perIp.AddOrUpdate(ip, 0, (_, current) => Math.Max(0, current - 1));
    }

    private void KillAgent(Process? process, string appId)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _logger.LogWarning(e, "Arrêt de l'agent de {App} imparfait.", appId);
        }
        finally
        {
            process.Dispose();
        }
    }

    private static int ParseSize(string? value, int fallback, int min, int max) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? Math.Clamp(n, min, max) : fallback;

    private static async Task FailAsync(WebSocket socket, string code, string message, CancellationToken token)
    {
        try
        {
            var json = $"{{\"type\":\"error\",\"code\":{Quote(code)},\"message\":{Quote(message)}}}";
            await socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, token);
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
        }

        await CloseAsync(socket, token);
    }

    private static async Task CloseAsync(WebSocket socket, CancellationToken token)
    {
        if (socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
        {
            return;
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", timeout.Token);
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    private static async Task Swallow(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or WebSocketException or InvalidDataException or ObjectDisposedException)
        {
        }
    }

    private static string Quote(string value) => System.Text.Json.JsonSerializer.Serialize(value);
}
