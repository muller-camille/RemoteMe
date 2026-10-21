using System.Globalization;
using RemoteMe.Web.Catalog;
using RemoteMe.Web.Configuration;
using RemoteMe.Web.Environments;

namespace RemoteMe.Web.Guacamole;

/// <summary>Construit les paramètres de connexion RDP transmis à guacd.</summary>
public static class RdpParameters
{
    public static Dictionary<string, string> Build(
        RdpOptions rdp, AppDefinition app, PoolAccount account, string environmentKey, ClientInfo client)
    {
        var p = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["hostname"] = NonEmpty(app.Hostname) ?? rdp.Hostname,
            ["port"] = rdp.Port.ToString(CultureInfo.InvariantCulture),
            ["username"] = account.Username,
            ["password"] = account.Password,
            ["security"] = rdp.Security,
            ["ignore-cert"] = Flag(rdp.IgnoreCertificate),
            ["server-layout"] = rdp.ServerLayout,
            ["client-name"] = rdp.ClientName,
            ["resize-method"] = rdp.ResizeMethod,
            ["disable-audio"] = Flag(!rdp.EnableAudio),
            ["enable-audio-input"] = Flag(rdp.EnableAudioInput),
            ["enable-printing"] = Flag(rdp.EnablePrinting),
            ["disable-copy"] = Flag(!rdp.EnableClipboardCopy),
            ["disable-paste"] = Flag(!rdp.EnableClipboardPaste),
        };

        var domain = account.Domain ?? NonEmpty(rdp.Domain);
        if (domain is not null)
        {
            p["domain"] = domain;
        }

        var loadBalanceInfo = NonEmpty(app.LoadBalanceInfo) ?? NonEmpty(rdp.LoadBalanceInfo);
        if (loadBalanceInfo is not null)
        {
            p["load-balance-info"] = loadBalanceInfo;
        }

        if (NonEmpty(app.Program) is { } program)
        {
            p["remote-app"] = program;
            SetIfPresent(p, "remote-app-dir", app.WorkingDirectory);
            SetIfPresent(p, "remote-app-args", app.Arguments);
        }

        if (client.Timezone is not null)
        {
            p["timezone"] = client.Timezone;
        }

        if (rdp.EnableDrive)
        {
            // Un dossier par environnement (et non par compte, réutilisé par d'autres visiteurs).
            p["enable-drive"] = "true";
            p["create-drive-path"] = "true";
            p["drive-name"] = "RemoteMe";
            p["drive-path"] = $"{rdp.DriveRootPath.TrimEnd('/')}/{environmentKey}";
        }

        foreach (var (name, value) in rdp.ExtraParameters)
        {
            p[name] = value;
        }

        foreach (var (name, value) in app.Parameters)
        {
            p[name] = value;
        }

        return p;
    }

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string Flag(bool value) => value ? "true" : "";

    private static void SetIfPresent(Dictionary<string, string> p, string name, string? value)
    {
        if (NonEmpty(value) is { } v)
        {
            p[name] = v;
        }
    }
}
