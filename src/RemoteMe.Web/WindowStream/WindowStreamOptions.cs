namespace RemoteMe.Web.WindowStream;

/// <summary>
/// Diffusion « par fenêtre » : chaque connexion lance une instance de l'application via l'agent
/// (RemoteMe.WindowAgent), sans session RDS. Section RemoteMe:WindowStream.
/// </summary>
public sealed class WindowStreamOptions
{
    public const string SectionName = "RemoteMe:WindowStream";

    /// <summary>Exécutable de l'agent : son .exe, ou "dotnet" avec le .dll en préfixe d'arguments.</summary>
    public string AgentCommand { get; set; } = "";

    /// <summary>Arguments placés avant ceux générés (ex. le chemin du .dll quand AgentCommand = "dotnet").</summary>
    public List<string> AgentArgumentPrefix { get; set; } = new();

    /// <summary>Force l'agent factice (démonstration et tests, hors Windows).</summary>
    public bool Fake { get; set; }

    /// <summary>Images par seconde demandées à l'agent.</summary>
    public int Fps { get; set; } = 15;

    /// <summary>Nombre maximal d'instances simultanées sur le serveur.</summary>
    public int MaxInstances { get; set; } = 20;

    /// <summary>Nombre maximal d'instances simultanées par adresse IP.</summary>
    public int MaxInstancesPerIp { get; set; } = 3;

    /// <summary>Délai d'attente de connexion de l'agent après son lancement.</summary>
    public int AgentStartTimeoutSeconds { get; set; } = 20;
}
