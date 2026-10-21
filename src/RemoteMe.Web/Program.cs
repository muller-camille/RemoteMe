using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using RemoteMe.Web.Catalog;
using RemoteMe.Web.Configuration;
using RemoteMe.Web.Environments;
using RemoteMe.Web.Guacamole;
using RemoteMe.Web.Http;

var builder = WebApplication.CreateBuilder(args);

// Catalogue séparé de la configuration technique, rechargé dès qu'il est modifié.
builder.Configuration.AddJsonFile("apps.json", optional: true, reloadOnChange: true);

builder.Services.Configure<RemoteMeOptions>(builder.Configuration.GetSection(RemoteMeOptions.SectionName));
builder.Services.Configure<CatalogOptions>(builder.Configuration);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<AppCatalog>();
builder.Services.AddSingleton<EnvironmentPool>();
builder.Services.AddSingleton<GuacamoleTunnel>();
builder.Services.AddHostedService<EnvironmentJanitor>();

var knownProxies = builder.Configuration.GetSection("RemoteMe:KnownProxies").Get<string[]>() ?? Array.Empty<string>();
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
    foreach (var proxy in knownProxies)
    {
        options.KnownProxies.Add(IPAddress.Parse(proxy));
    }
});

var app = builder.Build();

if (knownProxies.Length > 0)
{
    app.UseForwardedHeaders();
}

app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    var host = context.Request.Host.Value;
    headers.XContentTypeOptions = "nosniff";
    headers.XFrameOptions = "DENY";
    headers["Referrer-Policy"] = "no-referrer";
    headers.ContentSecurityPolicy =
        "default-src 'self'; img-src 'self' data: blob:; media-src 'self' blob:; " +
        $"connect-src 'self' ws://{host} wss://{host}; script-src 'self'; style-src 'self'; " +
        "object-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'";
    if (context.Request.Path.StartsWithSegments("/api"))
    {
        headers.CacheControl = "no-store";
    }

    await next();
});

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(25) });

var api = app.MapGroup("/api");

api.MapGet("/apps", (AppCatalog catalog) => Results.Ok(new
{
    apps = catalog.Apps.Select(a => new { a.Id, a.Name, a.Description, a.Category, a.Icon }),
}));

api.MapGet("/apps/{id}", (string id, AppCatalog catalog) =>
    catalog.Find(id) is { } a
        ? Results.Ok(new { a.Id, a.Name, a.Description, a.Category, a.Icon })
        : Results.NotFound());

// Crée le cookie visiteur au besoin : il doit exister avant l'ouverture du tunnel WebSocket.
api.MapGet("/environment", (HttpContext context, EnvironmentPool pool, IOptions<RemoteMeOptions> options) =>
{
    var visitorId = VisitorCookie.Ensure(context);
    var rdp = options.Value.Rdp;
    return Results.Ok(new
    {
        environment = pool.GetSnapshot(visitorId),
        visitorId = EnvironmentPool.ShortIdOf(visitorId),
        pool = pool.GetStats(),
        features = new
        {
            clipboardCopy = rdp.EnableClipboardCopy,
            clipboardPaste = rdp.EnableClipboardPaste,
            audio = rdp.EnableAudio,
            audioInput = rdp.EnableAudioInput,
        },
    });
});

// Libère l'environnement : ses applications se ferment et le compte part en réinitialisation.
api.MapPost("/environment/reset", (HttpContext context, EnvironmentPool pool) =>
{
    if (!RequestGuards.IsSameOrigin(context.Request))
    {
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }

    pool.Release(VisitorCookie.Read(context.Request), GuacStatus.SessionClosed);
    VisitorCookie.Delete(context);
    return Results.NoContent();
});

app.Map("/tunnel", (HttpContext context, GuacamoleTunnel tunnel) => tunnel.HandleAsync(context));

app.MapGet("/healthz", async (EnvironmentPool pool, IOptions<RemoteMeOptions> options, CancellationToken token) =>
{
    var guacd = options.Value.Guacd;
    var reachable = false;
    try
    {
        using var tcp = new TcpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        await tcp.ConnectAsync(guacd.Host, guacd.Port, timeout.Token);
        reachable = true;
    }
    catch (Exception e) when (e is SocketException or OperationCanceledException)
    {
        // Signalé dans la réponse.
    }

    var body = new { status = reachable ? "ok" : "degraded", guacd = reachable ? "ok" : "unreachable", pool = pool.GetStats() };
    return reachable ? Results.Ok(body) : Results.Json(body, statusCode: StatusCodes.Status503ServiceUnavailable);
});

app.Run();

public partial class Program
{
}
