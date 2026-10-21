namespace RemoteMe.WindowAgent;

/// <summary>
/// Correspondance entre les codes clavier du navigateur (<c>KeyboardEvent.code</c>) et les codes
/// de touche virtuelle Windows (VK_*), pour les touches non imprimables et de navigation.
/// Le texte imprimable est transmis séparément via WM_CHAR, sans passer par cette table.
/// </summary>
public static class KeyCodes
{
    private static readonly Dictionary<string, int> Map = Build();

    public static int ToVirtualKey(string code) => Map.GetValueOrDefault(code, 0);

    private static Dictionary<string, int> Build()
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["Backspace"] = 0x08,
            ["Tab"] = 0x09,
            ["Enter"] = 0x0D,
            ["ShiftLeft"] = 0x10,
            ["ShiftRight"] = 0x10,
            ["ControlLeft"] = 0x11,
            ["ControlRight"] = 0x11,
            ["AltLeft"] = 0x12,
            ["AltRight"] = 0x12,
            ["Pause"] = 0x13,
            ["CapsLock"] = 0x14,
            ["Escape"] = 0x1B,
            ["Space"] = 0x20,
            ["PageUp"] = 0x21,
            ["PageDown"] = 0x22,
            ["End"] = 0x23,
            ["Home"] = 0x24,
            ["ArrowLeft"] = 0x25,
            ["ArrowUp"] = 0x26,
            ["ArrowRight"] = 0x27,
            ["ArrowDown"] = 0x28,
            ["Insert"] = 0x2D,
            ["Delete"] = 0x2E,
            ["ContextMenu"] = 0x5D,
            ["NumLock"] = 0x90,
            ["ScrollLock"] = 0x91,
        };

        for (var n = 0; n <= 9; n++)
        {
            map[$"Digit{n}"] = 0x30 + n;      // 0-9
            map[$"Numpad{n}"] = 0x60 + n;     // pavé numérique
        }

        for (var c = 'A'; c <= 'Z'; c++)
        {
            map[$"Key{c}"] = c;               // VK_A..VK_Z = 'A'..'Z'
        }

        for (var f = 1; f <= 12; f++)
        {
            map[$"F{f}"] = 0x70 + (f - 1);    // F1-F12
        }

        return map;
    }
}
