using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace RemoteMe.Web.Catalog;

/// <summary>Contenu de apps.json.</summary>
public sealed class CatalogOptions
{
    public List<AppDefinition> Apps { get; set; } = new();
}

/// <summary>Une application publiée dans le catalogue.</summary>
public sealed class AppDefinition
{
    /// <summary>Identifiant d'URL : minuscules, chiffres, "-" et "_".</summary>
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public string? Description { get; set; }

    public string? Category { get; set; }

    /// <summary>Chemin relatif sous wwwroot (ex. "icons/calc.png") ; vide = pastille avec initiales.</summary>
    public string? Icon { get; set; }

    /// <summary>
    /// Mode de diffusion : "rdp" (défaut, via guacd et une session RDS) ou "window"
    /// (une instance de l'application capturée fenêtre par fenêtre, via l'agent, sans session RDS).
    /// </summary>
    public string Backend { get; set; } = "rdp";

    /// <summary>
    /// backend "rdp" : programme RemoteApp ("||alias" d'une RemoteApp publiée, recommandé), vide = bureau complet.
    /// backend "window" : chemin de l'exécutable à lancer (ex. "notepad.exe" ou "C:\\Apps\\monappli.exe").
    /// </summary>
    public string? Program { get; set; }

    public string? Arguments { get; set; }

    public string? WorkingDirectory { get; set; }

    /// <summary>Surcharge de RemoteMe:Rdp:Hostname pour cette application.</summary>
    public string? Hostname { get; set; }

    /// <summary>Surcharge de RemoteMe:Rdp:LoadBalanceInfo pour cette application.</summary>
    public string? LoadBalanceInfo { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>Paramètres guacd propres à l'application (prioritaires sur les paramètres communs).</summary>
    public Dictionary<string, string> Parameters { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>Catalogue des applications, relu à chaud quand apps.json change.</summary>
public sealed partial class AppCatalog
{
    private readonly IOptionsMonitor<CatalogOptions> _options;
    private readonly ILogger<AppCatalog> _logger;
    private IReadOnlyList<AppDefinition> _apps = Array.Empty<AppDefinition>();

    public AppCatalog(IOptionsMonitor<CatalogOptions> options, ILogger<AppCatalog> logger)
    {
        _options = options;
        _logger = logger;
        Load(options.CurrentValue);
        options.OnChange(Load);
    }

    public IReadOnlyList<AppDefinition> Apps => _apps;

    public AppDefinition? Find(string? id) =>
        string.IsNullOrEmpty(id) ? null : _apps.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.Ordinal));

    private void Load(CatalogOptions options)
    {
        var valid = new List<AppDefinition>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var app in options.Apps)
        {
            if (!app.Enabled)
            {
                continue;
            }

            if (!IdPattern().IsMatch(app.Id) || string.IsNullOrWhiteSpace(app.Name))
            {
                _logger.LogWarning("Application ignorée : identifiant '{Id}' ou nom invalide.", app.Id);
                continue;
            }

            if (!seen.Add(app.Id))
            {
                _logger.LogWarning("Application ignorée : identifiant '{Id}' en double.", app.Id);
                continue;
            }

            app.Backend = string.IsNullOrWhiteSpace(app.Backend) ? "rdp" : app.Backend.Trim().ToLowerInvariant();
            if (app.Backend is not ("rdp" or "window"))
            {
                _logger.LogWarning("Application '{Id}' ignorée : backend '{Backend}' inconnu.", app.Id, app.Backend);
                continue;
            }

            valid.Add(app);
        }

        _apps = valid;
        _logger.LogInformation("Catalogue chargé : {Count} application(s).", valid.Count);
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]{0,63}$")]
    private static partial Regex IdPattern();
}
