using System.Text.Json;

namespace RemoteMe.WindowAgent;

/// <summary>Type d'évènement d'entrée venu du navigateur.</summary>
public enum InputKind
{
    Unknown,
    Mouse,
    Wheel,
    Key,
}

/// <summary>
/// Évènement clavier/souris, tel que le navigateur l'envoie (coordonnées dans la zone client
/// de la fenêtre, en pixels). Analysé à partir d'un petit objet JSON.
/// </summary>
public readonly record struct InputEvent(
    InputKind Kind,
    int X,
    int Y,
    int Buttons,
    int WheelDelta,
    bool Down,
    string Code,
    string Key)
{
    public static bool TryParse(ReadOnlySpan<byte> json, out InputEvent result)
    {
        result = default;
        try
        {
            using var doc = JsonDocument.Parse(json.ToArray());
            var root = doc.RootElement;
            var kind = root.TryGetProperty("t", out var t) ? t.GetString() : null;
            switch (kind)
            {
                case "m":
                    result = new InputEvent(InputKind.Mouse, GetInt(root, "x"), GetInt(root, "y"), GetInt(root, "b"), 0, false, "", "");
                    return true;
                case "w":
                    result = new InputEvent(InputKind.Wheel, GetInt(root, "x"), GetInt(root, "y"), 0, GetInt(root, "d"), false, "", "");
                    return true;
                case "k":
                    result = new InputEvent(
                        InputKind.Key, 0, 0, 0, 0,
                        root.TryGetProperty("d", out var d) && d.GetBoolean(),
                        root.TryGetProperty("c", out var c) ? c.GetString() ?? "" : "",
                        root.TryGetProperty("k", out var k) ? k.GetString() ?? "" : "");
                    return true;
                default:
                    return false;
            }
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static int GetInt(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.TryGetInt32(out var i) ? i : 0;
}
