namespace RemoteMe.WindowAgent;

/// <summary>Une image capturée : pixels RGBA de haut en bas.</summary>
public readonly record struct CapturedFrame(byte[] Rgba, int Width, int Height);

/// <summary>
/// Source d'une fenêtre à diffuser : capture son contenu et reçoit les entrées qui lui sont
/// destinées. Deux implémentations : la vraie (Win32, une fenêtre d'application) et la factice
/// (image de synthèse, pour tester le pipeline hors Windows).
/// </summary>
public interface IWindowSource : IDisposable
{
    /// <summary>Prépare la source (lance l'application, trouve sa fenêtre…).</summary>
    Task StartAsync(CancellationToken token);

    /// <summary>
    /// Capture l'état courant de la fenêtre, ou null si rien n'a changé depuis la dernière
    /// capture (pour éviter de renvoyer une image identique).
    /// </summary>
    CapturedFrame? Capture();

    /// <summary>Transmet un évènement clavier/souris à la fenêtre.</summary>
    void SendInput(in InputEvent input);

    /// <summary>Vrai quand l'application s'est fermée : la diffusion doit s'arrêter.</summary>
    bool HasExited { get; }
}
