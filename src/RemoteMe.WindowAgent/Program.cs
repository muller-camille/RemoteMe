using System.Globalization;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using RemoteMe.WindowAgent;

// Agent de diffusion d'une fenêtre. Lancé par le serveur RemoteMe, un par connexion de visiteur :
// il se connecte au serveur en TCP sur localhost, diffuse les images de la fenêtre (PNG) et
// applique les entrées clavier/souris reçues.
//
//   RemoteMe.WindowAgent --port <p> --token <t> [--program notepad.exe] [--args ...]
//                        [--dir C:\...] [--fps 15] [--fake] [--width 800] [--height 600]
//
// --fake : source de synthèse qui tourne hors Windows, pour tester tout le pipeline.

var options = AgentOptions.Parse(args);
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var useFake = options.Fake || !RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
if (options.Fake != useFake)
{
    await Console.Error.WriteLineAsync("Hors Windows : bascule automatique sur l'agent factice (--fake).");
}

IWindowSource source = useFake
    ? new FakeWindowSource(options.Width, options.Height)
    : new Win32WindowSource(options.Program, options.Arguments, options.WorkingDirectory);

using var tcp = new TcpClient();
await tcp.ConnectAsync("127.0.0.1", options.Port, cts.Token);
var stream = tcp.GetStream();
var writeLock = new SemaphoreSlim(1, 1);

// Premier message : le jeton reçu au lancement, qui prouve au serveur que c'est bien son agent.
await AgentProtocol.WriteAsync(stream, AgentProtocol.Hello, System.Text.Encoding.UTF8.GetBytes(options.Token), cts.Token);

async Task SendAsync(byte type, ReadOnlyMemory<byte> payload)
{
    await writeLock.WaitAsync(cts.Token);
    try
    {
        await AgentProtocol.WriteAsync(stream, type, payload, cts.Token);
    }
    finally
    {
        writeLock.Release();
    }
}

try
{
    await source.StartAsync(cts.Token);

    var startup = source switch
    {
        FakeWindowSource fake => fake.StatusJson(),
        Win32WindowSource win => win.StartupJson(),
        _ => "{}",
    };
    await SendAsync(AgentProtocol.Meta, System.Text.Encoding.UTF8.GetBytes(startup));

    var input = ReadInputLoopAsync(source, stream, SendAsync, cts.Token);
    var capture = CaptureLoopAsync(source, options.Fps, SendAsync, cts.Token);
    await Task.WhenAny(input, capture);
}
catch (OperationCanceledException)
{
}
catch (Exception e) when (e is IOException or SocketException)
{
    // Le serveur a fermé la connexion : fin normale.
}
catch (Exception e)
{
    await TrySendMetaAsync(SendAsync, $"{{\"error\":{System.Text.Json.JsonSerializer.Serialize(e.Message)}}}");
}
finally
{
    cts.Cancel();
    source.Dispose();
}

return;

static async Task CaptureLoopAsync(IWindowSource source, int fps, Func<byte, ReadOnlyMemory<byte>, Task> send, CancellationToken token)
{
    var period = TimeSpan.FromMilliseconds(1000.0 / Math.Clamp(fps, 1, 60));
    using var timer = new PeriodicTimer(period);
    do
    {
        if (source.HasExited)
        {
            await TrySendMetaAsync(send, "{\"exit\":true}");
            return;
        }

        if (source.Capture() is { } frame)
        {
            var png = PngEncoder.Encode(frame.Rgba, frame.Width, frame.Height);
            await send(AgentProtocol.Frame, png);
        }
    }
    while (await timer.WaitForNextTickAsync(token));
}

static async Task ReadInputLoopAsync(IWindowSource source, Stream stream, Func<byte, ReadOnlyMemory<byte>, Task> send, CancellationToken token)
{
    while (await AgentProtocol.ReadAsync(stream, token) is { } message)
    {
        if (message.Type == AgentProtocol.Input && InputEvent.TryParse(message.Payload, out var evt))
        {
            source.SendInput(evt);

            // L'agent factice renvoie l'entrée reçue, pour vérifier le trajet aller-retour.
            if (source is FakeWindowSource fake)
            {
                await send(AgentProtocol.Meta, System.Text.Encoding.UTF8.GetBytes(fake.StatusJson()));
            }
        }
    }
}

static async Task TrySendMetaAsync(Func<byte, ReadOnlyMemory<byte>, Task> send, string json)
{
    try
    {
        await send(AgentProtocol.Meta, System.Text.Encoding.UTF8.GetBytes(json));
    }
    catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
    {
    }
}

internal sealed record AgentOptions(
    int Port,
    string Token,
    string Program,
    string? Arguments,
    string? WorkingDirectory,
    int Fps,
    bool Fake,
    int Width,
    int Height)
{
    public static AgentOptions Parse(string[] args)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var fake = false;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--fake")
            {
                fake = true;
            }
            else if (args[i].StartsWith("--", StringComparison.Ordinal) && i + 1 < args.Length)
            {
                map[args[i][2..]] = args[++i];
            }
        }

        int Int(string key, int fallback) =>
            map.TryGetValue(key, out var v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : fallback;

        var port = Int("port", 0);
        if (port is <= 0 or > 65535)
        {
            throw new ArgumentException("--port <1-65535> est obligatoire.");
        }

        return new AgentOptions(
            port,
            map.GetValueOrDefault("token", ""),
            map.GetValueOrDefault("program", "notepad.exe"),
            map.GetValueOrDefault("args"),
            map.GetValueOrDefault("dir"),
            Int("fps", 15),
            fake,
            Int("width", 800),
            Int("height", 600));
    }
}
