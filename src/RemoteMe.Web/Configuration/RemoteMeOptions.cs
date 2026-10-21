namespace RemoteMe.Web.Configuration;

/// <summary>Racine de la configuration (section "RemoteMe" d'appsettings.json).</summary>
public sealed class RemoteMeOptions
{
    public const string SectionName = "RemoteMe";

    public GuacdOptions Guacd { get; set; } = new();

    public RdpOptions Rdp { get; set; } = new();

    public EnvironmentOptions Environments { get; set; } = new();

    /// <summary>
    /// Adresses des reverse proxies de confiance (ARR, répartiteur de charge...) : leur en-tête
    /// X-Forwarded-For donne l'IP réelle du visiteur, utilisée par les limites par adresse IP.
    /// </summary>
    public List<string> KnownProxies { get; set; } = new();
}

/// <summary>Démon guacd qui traduit RDP en protocole Guacamole.</summary>
public sealed class GuacdOptions
{
    public string Host { get; set; } = "127.0.0.1";

    public int Port { get; set; } = 4822;

    public int ConnectTimeoutSeconds { get; set; } = 15;
}

/// <summary>Paramètres RDP communs à toutes les applications (surchargeables par application).</summary>
public sealed class RdpOptions
{
    /// <summary>Hôte de session RDS ou Connection Broker.</summary>
    public string Hostname { get; set; } = "";

    public int Port { get; set; } = 3389;

    /// <summary>any, nla, nla-ext, tls, rdp ou vmconnect.</summary>
    public string Security { get; set; } = "nla";

    /// <summary>À n'activer que si le certificat du serveur RDS n'est pas approuvé par guacd.</summary>
    public bool IgnoreCertificate { get; set; }

    public string? Domain { get; set; }

    /// <summary>Ex. "tsv://MS Terminal Services Plugin.1.MaCollection" derrière un Connection Broker.</summary>
    public string? LoadBalanceInfo { get; set; }

    /// <summary>Disposition clavier côté serveur (fr-fr-azerty, en-us-qwerty, ...).</summary>
    public string ServerLayout { get; set; } = "fr-fr-azerty";

    public string ClientName { get; set; } = "RemoteMe";

    /// <summary>display-update (redimensionnement dynamique) ou reconnect.</summary>
    public string ResizeMethod { get; set; } = "display-update";

    public bool EnableAudio { get; set; } = true;

    public bool EnableAudioInput { get; set; }

    public bool EnablePrinting { get; set; }

    /// <summary>Copie du serveur vers le navigateur.</summary>
    public bool EnableClipboardCopy { get; set; } = true;

    /// <summary>Collage du navigateur vers le serveur.</summary>
    public bool EnableClipboardPaste { get; set; } = true;

    /// <summary>Lecteur virtuel exposé dans la session (un dossier par environnement, côté guacd).</summary>
    public bool EnableDrive { get; set; }

    /// <summary>Racine des dossiers de lecteur virtuel, sur la machine guacd.</summary>
    public string DriveRootPath { get; set; } = "/var/lib/guacamole/drives";

    /// <summary>Paramètres guacd supplémentaires (nom → valeur), appliqués à toutes les applications.</summary>
    public Dictionary<string, string> ExtraParameters { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Pool de comptes Windows. Chaque visiteur anonyme reçoit un compte dédié le temps de sa visite :
/// c'est ce compte qui lui donne une session, un profil et des fichiers distincts des autres visiteurs.
/// </summary>
public sealed class EnvironmentOptions
{
    /// <summary>Comptes explicites (chacun avec son propre mot de passe).</summary>
    public List<PoolAccountOptions> Accounts { get; set; } = new();

    /// <summary>Génération de comptes par motif, ex. "kiosk{0:00}" → kiosk01, kiosk02, ...</summary>
    public string? UsernamePattern { get; set; }

    public int PatternStart { get; set; } = 1;

    public int PatternCount { get; set; }

    /// <summary>Mot de passe commun aux comptes générés par motif (à fournir par variable d'environnement).</summary>
    public string? SharedPassword { get; set; }

    /// <summary>Domaine par défaut des comptes du pool.</summary>
    public string? Domain { get; set; }

    /// <summary>Durée sans aucune connexion ouverte au-delà de laquelle l'environnement est libéré.</summary>
    public int IdleTimeoutMinutes { get; set; } = 15;

    /// <summary>Durée de vie maximale d'un environnement, même actif.</summary>
    public int MaxLifetimeMinutes { get; set; } = 240;

    /// <summary>Délai de quarantaine d'un compte libéré avant de le confier à un autre visiteur.</summary>
    public int CooldownSeconds { get; set; } = 90;

    /// <summary>Nombre maximal d'applications ouvertes simultanément par un même visiteur.</summary>
    public int MaxConnectionsPerEnvironment { get; set; } = 3;

    /// <summary>Nombre maximal d'environnements simultanés pour une même adresse IP.</summary>
    public int MaxEnvironmentsPerIp { get; set; } = 3;

    /// <summary>Nombre maximal de créations d'environnement par adresse IP et par heure.</summary>
    public int MaxNewEnvironmentsPerIpPerHour { get; set; } = 20;

    /// <summary>
    /// Commande de réinitialisation exécutée à la libération d'un compte (fermeture de session, purge du profil).
    /// Vide = pas de commande : la quarantaine et les stratégies RDS assurent seules la remise à zéro.
    /// </summary>
    public string? ResetCommand { get; set; }

    /// <summary>Arguments de la commande ; jetons acceptés : {username}, {domain}, {hostname}.</summary>
    public List<string> ResetArguments { get; set; } = new();

    public int ResetTimeoutSeconds { get; set; } = 120;
}

public sealed class PoolAccountOptions
{
    public string Username { get; set; } = "";

    public string Password { get; set; } = "";

    public string? Domain { get; set; }
}
