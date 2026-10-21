using System.Text.Json;

namespace RemoteMe.WindowAgent;

/// <summary>
/// Source de test, sans Windows : dessine une image animée qui réagit aux entrées
/// (un carré suit la souris, change de couleur au clic, et un compteur monte à chaque touche).
/// Sert à valider tout le pipeline navigateur ↔ serveur ↔ agent hors d'un vrai poste Windows.
/// </summary>
public sealed class FakeWindowSource : IWindowSource
{
    private readonly int _width;
    private readonly int _height;
    private readonly byte[] _rgba;
    private int _frame;
    private int _mouseX;
    private int _mouseY;
    private int _buttons;
    private int _keyCount;
    private string _lastInput = "—";

    public FakeWindowSource(int width = 800, int height = 600)
    {
        _width = width;
        _height = height;
        _rgba = new byte[width * height * 4];
        _mouseX = width / 2;
        _mouseY = height / 2;
    }

    public bool HasExited => false;

    public Task StartAsync(CancellationToken token) => Task.CompletedTask;

    public CapturedFrame Render()
    {
        _frame++;
        var phase = _frame * 2;

        // Fond : dégradé diagonal qui défile, pour prouver que les images s'enchaînent.
        for (var y = 0; y < _height; y++)
        {
            var row = y * _width * 4;
            for (var x = 0; x < _width; x++)
            {
                var i = row + (x * 4);
                _rgba[i] = (byte)((x + phase) & 0xFF);
                _rgba[i + 1] = (byte)((y + phase) & 0xFF);
                _rgba[i + 2] = (byte)(((x + y) / 2 + phase) & 0xFF);
                _rgba[i + 3] = 255;
            }
        }

        // Carré qui suit la souris ; rouge si un bouton est enfoncé, sinon blanc.
        var color = _buttons != 0 ? (r: (byte)255, g: (byte)60, b: (byte)60) : (r: (byte)255, g: (byte)255, b: (byte)255);
        FillRect(_mouseX - 20, _mouseY - 20, 40, 40, color.r, color.g, color.b);

        // Barre de progression = nombre de touches reçues (preuve du retour clavier).
        FillRect(0, _height - 16, Math.Min(_width, _keyCount * 12), 16, 80, 200, 120);

        return new CapturedFrame(_rgba, _width, _height);
    }

    CapturedFrame? IWindowSource.Capture() => Render();

    public void SendInput(in InputEvent input)
    {
        switch (input.Kind)
        {
            case InputKind.Mouse:
                _mouseX = Math.Clamp(input.X, 0, _width - 1);
                _mouseY = Math.Clamp(input.Y, 0, _height - 1);
                _buttons = input.Buttons;
                _lastInput = $"mouse {_mouseX},{_mouseY} b={_buttons}";
                break;
            case InputKind.Wheel:
                _lastInput = $"wheel {input.WheelDelta}";
                break;
            case InputKind.Key:
                if (input.Down)
                {
                    _keyCount++;
                }

                _lastInput = $"key {(input.Down ? "down" : "up")} {input.Code}/{input.Key}";
                break;
        }
    }

    /// <summary>Métadonnées renvoyées au navigateur, pour vérifier le trajet aller-retour des entrées.</summary>
    public string StatusJson() => JsonSerializer.Serialize(new
    {
        title = "Agent factice",
        w = _width,
        h = _height,
        echo = _lastInput,
        keys = _keyCount,
    });

    public void Dispose()
    {
    }

    private void FillRect(int x0, int y0, int w, int h, byte r, byte g, byte b)
    {
        var xStart = Math.Max(0, x0);
        var yStart = Math.Max(0, y0);
        var xEnd = Math.Min(_width, x0 + w);
        var yEnd = Math.Min(_height, y0 + h);
        for (var y = yStart; y < yEnd; y++)
        {
            var row = y * _width * 4;
            for (var x = xStart; x < xEnd; x++)
            {
                var i = row + (x * 4);
                _rgba[i] = r;
                _rgba[i + 1] = g;
                _rgba[i + 2] = b;
                _rgba[i + 3] = 255;
            }
        }
    }
}
