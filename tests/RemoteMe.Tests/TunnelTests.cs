using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using RemoteMe.Web.Guacamole;
using Xunit.Abstractions;

namespace RemoteMe.Tests;

/// <summary>Tunnel complet : navigateur (client WebSocket de test) ↔ RemoteMe ↔ faux guacd.</summary>
public sealed class TunnelTests : IAsyncLifetime
{
    private readonly FakeGuacd _guacd = new();
    private readonly ITestOutputHelper _output;
    private WebApplicationFactory<Program> _factory = null!;

    public TunnelTests(ITestOutputHelper output) => _output = output;

    public Task InitializeAsync()
    {
        _factory = CreateFactory(accounts: 1);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        _guacd.Dispose();
    }

    [Fact]
    public async Task Relays_instructions_both_ways_and_answers_pings()
    {
        var cookie = await GetVisitorCookieAsync(_factory);
        using var socket = await OpenTunnelAsync(_factory, cookie, "app=testapp&width=1280&height=720&dpi=96&tz=Europe/Paris&image=image/png");

        var uuid = await ReceiveAsync(socket);
        Assert.Matches("^0\\.,36\\.[0-9a-f-]{36};$", uuid);

        var session = await _guacd.NextSessionAsync();
        Assert.Equal("rdp", session.Select);
        Assert.Equal("rds.test", session.Parameters["hostname"]);
        Assert.Equal("kiosk01", session.Parameters["username"]);
        Assert.Equal("pw-kiosk", session.Parameters["password"]);
        Assert.Equal("||testapp", session.Parameters["remote-app"]);
        Assert.Equal("VERSION_1_5_0", session.Parameters["VERSION_1_5_0"]);
        Assert.Equal(new[] { "1280", "720", "96" }, session.Size);
        Assert.Equal("Europe/Paris", session.Timezone);

        await session.SendAsync("4.sync,4.1234;");
        Assert.Equal("4.sync,4.1234;", await ReceiveAsync(socket));

        await SendAsync(socket, "0.,4.ping,13.1700000000000;");
        Assert.Equal("0.,4.ping,13.1700000000000;", await ReceiveAsync(socket));

        await SendAsync(socket, "3.key,5.65307,1.1;");
        Assert.Equal("3.key,5.65307,1.1;", await session.ReadInstructionTextAsync());

        // Fin de session côté serveur RDP → fermeture WebSocket avec le statut Guacamole "0".
        session.Close();
        var (status, reason) = await ReceiveCloseAsync(socket);
        Assert.Equal(WebSocketCloseStatus.NormalClosure, status);
        Assert.Equal("0", reason);
    }

    [Fact]
    public async Task Rejects_a_tunnel_without_visitor_cookie()
    {
        using var socket = await OpenTunnelAsync(_factory, cookie: null, "app=testapp");
        var (_, reason) = await ReceiveCloseAsync(socket);
        Assert.Equal(((int)GuacStatus.ClientUnauthorized).ToString(), reason);
    }

    [Fact]
    public async Task Rejects_an_unknown_application()
    {
        var cookie = await GetVisitorCookieAsync(_factory);
        using var socket = await OpenTunnelAsync(_factory, cookie, "app=does-not-exist");
        var (_, reason) = await ReceiveCloseAsync(socket);
        Assert.Equal(((int)GuacStatus.ResourceNotFound).ToString(), reason);
    }

    [Fact]
    public async Task Second_visitor_is_told_the_pool_is_busy()
    {
        var first = await GetVisitorCookieAsync(_factory);
        using var firstSocket = await OpenTunnelAsync(_factory, first, "app=testapp");
        await ReceiveAsync(firstSocket);

        var second = await GetVisitorCookieAsync(_factory);
        Assert.NotEqual(first, second);
        using var secondSocket = await OpenTunnelAsync(_factory, second, "app=testapp");
        var (_, reason) = await ReceiveCloseAsync(secondSocket);
        Assert.Equal(((int)GuacStatus.ServerBusy).ToString(), reason);
    }

    [Fact]
    public async Task Guacd_errors_are_forwarded_as_close_reason()
    {
        _guacd.FailWith = "5.error,12.Login failed,3.769;";
        var cookie = await GetVisitorCookieAsync(_factory);
        using var socket = await OpenTunnelAsync(_factory, cookie, "app=testapp");
        var (_, reason) = await ReceiveCloseAsync(socket);
        Assert.Equal(((int)GuacStatus.ClientUnauthorized).ToString(), reason);
    }

    [Fact]
    public async Task Resetting_the_environment_closes_open_applications()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/environment");
        var cookie = ExtractCookie(response);

        using var socket = await OpenTunnelAsync(_factory, cookie, "app=testapp");
        await ReceiveAsync(socket);
        await _guacd.NextSessionAsync();

        var reset = new HttpRequestMessage(HttpMethod.Post, "/api/environment/reset");
        reset.Headers.Add("Cookie", cookie);
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(reset)).StatusCode);

        var (_, reason) = await ReceiveCloseAsync(socket);
        Assert.Equal(((int)GuacStatus.SessionClosed).ToString(), reason);
    }

    [Fact]
    public async Task Cross_origin_reset_is_refused()
    {
        var client = _factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/environment/reset");
        request.Headers.Add("Origin", "https://evil.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task Catalog_lists_enabled_apps_without_server_details()
    {
        var json = await _factory.CreateClient().GetStringAsync("/api/apps");
        Assert.Contains("\"testapp\"", json);
        Assert.DoesNotContain("||testapp", json);
        Assert.DoesNotContain("rds.test", json);
    }

    private WebApplicationFactory<Program> CreateFactory(int accounts) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureLogging(logging => logging.AddProvider(new XunitLoggerProvider(_output)));
            builder.ConfigureAppConfiguration((_, config) =>
            {
                var settings = new Dictionary<string, string?>
                {
                    ["RemoteMe:Guacd:Host"] = "127.0.0.1",
                    ["RemoteMe:Guacd:Port"] = _guacd.Port.ToString(),
                    ["RemoteMe:Rdp:Hostname"] = "rds.test",
                    ["RemoteMe:Environments:UsernamePattern"] = "kiosk{0:00}",
                    ["RemoteMe:Environments:PatternCount"] = accounts.ToString(),
                    ["RemoteMe:Environments:SharedPassword"] = "pw-kiosk",
                    ["RemoteMe:Environments:CooldownSeconds"] = "0",
                    ["Apps:0:Id"] = "testapp",
                    ["Apps:0:Name"] = "Test",
                    ["Apps:0:Program"] = "||testapp",
                    ["Apps:0:Enabled"] = "true",
                };
                config.AddInMemoryCollection(settings);
            });
        });

    private static async Task<string> GetVisitorCookieAsync(WebApplicationFactory<Program> factory)
    {
        var response = await factory.CreateClient().GetAsync("/api/environment");
        response.EnsureSuccessStatusCode();
        return ExtractCookie(response);
    }

    private static string ExtractCookie(HttpResponseMessage response)
    {
        var header = response.Headers.GetValues("Set-Cookie").Single(h => h.StartsWith("RemoteMe.Visitor=", StringComparison.Ordinal));
        return header.Split(';')[0];
    }

    private static async Task<WebSocket> OpenTunnelAsync(WebApplicationFactory<Program> factory, string? cookie, string query)
    {
        var client = factory.Server.CreateWebSocketClient();
        client.SubProtocols.Add("guacamole");
        if (cookie is not null)
        {
            client.ConfigureRequest = request => request.Headers["Cookie"] = cookie;
        }

        return await client.ConnectAsync(new Uri($"ws://localhost/tunnel?{query}"), CancellationToken.None);
    }

    private static Task SendAsync(WebSocket socket, string text) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None);

    private static async Task<string> ReceiveAsync(WebSocket socket)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var buffer = new byte[64 * 1024];
        var result = await socket.ReceiveAsync(buffer, timeout.Token);
        Assert.Equal(WebSocketMessageType.Text, result.MessageType);
        Assert.True(result.EndOfMessage);
        return Encoding.UTF8.GetString(buffer, 0, result.Count);
    }

    private static async Task<(WebSocketCloseStatus? Status, string? Reason)> ReceiveCloseAsync(WebSocket socket)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, timeout.Token);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return (result.CloseStatus, result.CloseStatusDescription);
            }
        }
    }
}

/// <summary>Faux guacd : rejoue la poignée de main du protocole et expose ce qu'il a reçu.</summary>
internal sealed class FakeGuacd : IDisposable
{
    private static readonly string[] ArgNames = { "VERSION_1_5_0", "hostname", "port", "username", "password", "domain", "remote-app", "security" };

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly Channel<FakeSession> _sessions = Channel.CreateUnbounded<FakeSession>();
    private readonly CancellationTokenSource _stop = new();

    public FakeGuacd()
    {
        _listener.Start();
        _ = AcceptLoopAsync();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>Si renseigné, envoyé à la place de "ready".</summary>
    public string? FailWith { get; set; }

    public async Task<FakeSession> NextSessionAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        return await _sessions.Reader.ReadAsync(timeout.Token);
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var tcp = await _listener.AcceptTcpClientAsync(_stop.Token);
                _ = HandleAsync(tcp);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException)
        {
        }
    }

    private async Task HandleAsync(TcpClient tcp)
    {
        var stream = tcp.GetStream();
        var reader = new GuacStreamReader(stream);
        var session = new FakeSession(tcp, reader);

        var select = await reader.ReadInstructionAsync(_stop.Token);
        session.Select = select!.Args[0];
        await stream.WriteAsync(Encoding.UTF8.GetBytes(GuacInstruction.Encode("args", ArgNames)));

        while (await reader.ReadInstructionAsync(_stop.Token) is { } instruction)
        {
            switch (instruction.Opcode)
            {
                case "size":
                    session.Size = instruction.Args.ToArray();
                    break;
                case "timezone":
                    session.Timezone = instruction.Args[0];
                    break;
                case "connect":
                    for (var i = 0; i < ArgNames.Length; i++)
                    {
                        session.Parameters[ArgNames[i]] = instruction.Args[i];
                    }

                    if (FailWith is not null)
                    {
                        await stream.WriteAsync(Encoding.UTF8.GetBytes(FailWith));
                        tcp.Close();
                        return;
                    }

                    await stream.WriteAsync(Encoding.UTF8.GetBytes("5.ready,5.$test;"));
                    await _sessions.Writer.WriteAsync(session);
                    return;
            }
        }
    }
}

internal sealed class FakeSession
{
    private readonly TcpClient _tcp;
    private readonly GuacStreamReader _reader;

    public FakeSession(TcpClient tcp, GuacStreamReader reader)
    {
        _tcp = tcp;
        _reader = reader;
    }

    public string Select { get; set; } = "";

    public string[] Size { get; set; } = Array.Empty<string>();

    public string? Timezone { get; set; }

    public Dictionary<string, string> Parameters { get; } = new();

    public Task SendAsync(string text) => _tcp.GetStream().WriteAsync(Encoding.UTF8.GetBytes(text)).AsTask();

    public async Task<string> ReadInstructionTextAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var instruction = await _reader.ReadInstructionAsync(timeout.Token);
        return instruction!.ToString();
    }

    public void Close() => _tcp.Close();
}
