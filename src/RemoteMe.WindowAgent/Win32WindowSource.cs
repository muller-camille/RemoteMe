using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;

namespace RemoteMe.WindowAgent;

/// <summary>
/// Vraie source Windows : lance une application Win32, capture sa fenêtre par son handle
/// (<c>PrintWindow</c>) et lui adresse le clavier/souris (<c>PostMessage</c>), sans toucher à
/// la session globale. Plusieurs instances peuvent donc coexister sur le même bureau, chacune
/// pilotée indépendamment.
///
/// Écrite pour Windows ; elle compile sur toute plateforme (les P/Invoke sont de simples
/// déclarations) mais ne s'exécute que sous Windows.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class Win32WindowSource : IWindowSource
{
    private const uint PwClientOnly = 0x1;
    private const uint PwRenderFullContent = 0x2;

    private readonly string _program;
    private readonly string? _arguments;
    private readonly string? _workingDirectory;

    private Process? _process;
    private IntPtr _hwnd;
    private IntPtr _focus;
    private int _prevButtons;

    // DIB réutilisé entre deux captures tant que la taille ne change pas.
    private IntPtr _memDc;
    private IntPtr _dib;
    private IntPtr _bits;
    private IntPtr _oldObj;
    private int _dibWidth;
    private int _dibHeight;
    private byte[] _frame = Array.Empty<byte>();
    private ulong _lastHash;

    public Win32WindowSource(string program, string? arguments, string? workingDirectory)
    {
        _program = program;
        _arguments = arguments;
        _workingDirectory = string.IsNullOrWhiteSpace(workingDirectory) ? null : workingDirectory;
    }

    public bool HasExited => _process is null or { HasExited: true };

    public async Task StartAsync(CancellationToken token)
    {
        var start = new ProcessStartInfo(_program)
        {
            UseShellExecute = false,
            WorkingDirectory = _workingDirectory ?? "",
        };
        if (!string.IsNullOrWhiteSpace(_arguments))
        {
            start.Arguments = _arguments;
        }

        _process = Process.Start(start) ?? throw new InvalidOperationException($"Impossible de lancer « {_program} ».");

        // Attend que l'application ait créé sa fenêtre principale.
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            token.ThrowIfCancellationRequested();
            if (_process.HasExited)
            {
                throw new InvalidOperationException("L'application s'est fermée avant d'afficher une fenêtre.");
            }

            _process.Refresh();
            var handle = _process.MainWindowHandle;
            if (handle != IntPtr.Zero)
            {
                _hwnd = handle;
                _focus = handle;
                SetForegroundWindow(handle);
                return;
            }

            await Task.Delay(100, token).ConfigureAwait(false);
        }

        throw new TimeoutException("L'application n'a pas affiché de fenêtre à temps.");
    }

    public CapturedFrame? Capture()
    {
        if (_hwnd == IntPtr.Zero || !GetClientRect(_hwnd, out var rect))
        {
            return null;
        }

        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        EnsureDib(width, height);

        if (!PrintWindow(_hwnd, _memDc, PwClientOnly | PwRenderFullContent))
        {
            return null;
        }

        // Le DIB est en BGRA de haut en bas (biHeight négatif) : conversion vers RGBA.
        var count = width * height * 4;
        Marshal.Copy(_bits, _frame, 0, count);
        ulong hash = 1469598103934665603UL;
        for (var i = 0; i < count; i += 4)
        {
            (_frame[i], _frame[i + 2]) = (_frame[i + 2], _frame[i]);
            _frame[i + 3] = 255;
            hash = (hash ^ _frame[i]) * 1099511628211UL;
        }

        if (hash == _lastHash)
        {
            return null;
        }

        _lastHash = hash;
        return new CapturedFrame(_frame, width, height);
    }

    public string StartupJson()
    {
        var title = new StringBuilder(256);
        if (_hwnd != IntPtr.Zero)
        {
            GetWindowTextW(_hwnd, title, title.Capacity);
        }

        GetClientRect(_hwnd, out var rect);
        return JsonSerializer.Serialize(new
        {
            title = title.ToString(),
            w = rect.Right - rect.Left,
            h = rect.Bottom - rect.Top,
        });
    }

    public void SendInput(in InputEvent input)
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        switch (input.Kind)
        {
            case InputKind.Mouse:
                SendMouse(input);
                break;
            case InputKind.Wheel:
                SendWheel(input);
                break;
            case InputKind.Key:
                SendKey(input);
                break;
        }
    }

    private void SendMouse(in InputEvent input)
    {
        var target = RealChildWindowFromPoint(_hwnd, new POINT { X = input.X, Y = input.Y });
        if (target == IntPtr.Zero)
        {
            target = _hwnd;
        }

        // Coordonnées locales à la fenêtre ciblée.
        var pt = new POINT { X = input.X, Y = input.Y };
        ClientToScreen(_hwnd, ref pt);
        ScreenToClient(target, ref pt);
        var lParam = MakeLParam(pt.X, pt.Y);
        var wParam = ButtonsToMk(input.Buttons);

        PostMessage(target, WmMouseMove, wParam, lParam);

        var changed = input.Buttons ^ _prevButtons;
        if ((changed & 1) != 0)
        {
            PostMessage(target, (input.Buttons & 1) != 0 ? WmLButtonDown : WmLButtonUp, wParam, lParam);
            if ((input.Buttons & 1) != 0)
            {
                _focus = target;
            }
        }

        if ((changed & 2) != 0)
        {
            PostMessage(target, (input.Buttons & 2) != 0 ? WmRButtonDown : WmRButtonUp, wParam, lParam);
        }

        if ((changed & 4) != 0)
        {
            PostMessage(target, (input.Buttons & 4) != 0 ? WmMButtonDown : WmMButtonUp, wParam, lParam);
        }

        _prevButtons = input.Buttons;
    }

    private void SendWheel(in InputEvent input)
    {
        var pt = new POINT { X = input.X, Y = input.Y };
        ClientToScreen(_hwnd, ref pt);
        var wParam = (IntPtr)((input.WheelDelta << 16) & unchecked((int)0xFFFF0000));
        PostMessage(_focus == IntPtr.Zero ? _hwnd : _focus, WmMouseWheel, wParam, MakeLParam(pt.X, pt.Y));
    }

    private void SendKey(in InputEvent input)
    {
        var target = _focus == IntPtr.Zero ? _hwnd : _focus;
        var vk = KeyCodes.ToVirtualKey(input.Code);

        if (input.Down)
        {
            if (vk != 0)
            {
                PostMessage(target, WmKeyDown, (IntPtr)vk, IntPtr.Zero);
            }

            // Caractère imprimable : PostMessage ne passe pas par TranslateMessage, donc on
            // envoie aussi WM_CHAR pour que les contrôles de saisie reçoivent le texte.
            if (input.Key.Length == 1 && !char.IsControl(input.Key[0]))
            {
                PostMessage(target, WmChar, (IntPtr)input.Key[0], IntPtr.Zero);
            }
            else if (input.Code == "Enter")
            {
                PostMessage(target, WmChar, (IntPtr)'\r', IntPtr.Zero);
            }
        }
        else if (vk != 0)
        {
            PostMessage(target, WmKeyUp, (IntPtr)vk, IntPtr.Zero);
        }
    }

    private void EnsureDib(int width, int height)
    {
        if (_memDc != IntPtr.Zero && width == _dibWidth && height == _dibHeight)
        {
            return;
        }

        ReleaseDib();

        var screenDc = GetDC(IntPtr.Zero);
        _memDc = CreateCompatibleDC(screenDc);
        ReleaseDC(IntPtr.Zero, screenDc);

        var info = new BITMAPINFO
        {
            biSize = 40, // taille de BITMAPINFOHEADER, pas de la struct complète
            biWidth = width,
            biHeight = -height, // négatif = lignes de haut en bas
            biPlanes = 1,
            biBitCount = 32,
            biCompression = 0,
        };

        _dib = CreateDIBSection(_memDc, ref info, 0, out _bits, IntPtr.Zero, 0);
        _oldObj = SelectObject(_memDc, _dib);
        _dibWidth = width;
        _dibHeight = height;
        _frame = new byte[width * height * 4];
        _lastHash = 0;
    }

    private void ReleaseDib()
    {
        if (_memDc != IntPtr.Zero)
        {
            SelectObject(_memDc, _oldObj);
            DeleteDC(_memDc);
            _memDc = IntPtr.Zero;
        }

        if (_dib != IntPtr.Zero)
        {
            DeleteObject(_dib);
            _dib = IntPtr.Zero;
        }
    }

    public void Dispose()
    {
        ReleaseDib();
        try
        {
            if (_process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }

        _process?.Dispose();
    }

    private static IntPtr MakeLParam(int x, int y) => (IntPtr)((y << 16) | (x & 0xFFFF));

    private static IntPtr ButtonsToMk(int buttons)
    {
        var mk = 0;
        if ((buttons & 1) != 0)
        {
            mk |= 0x0001;
        }

        if ((buttons & 2) != 0)
        {
            mk |= 0x0002;
        }

        if ((buttons & 4) != 0)
        {
            mk |= 0x0010;
        }

        return (IntPtr)mk;
    }

    private const uint WmMouseMove = 0x0200;
    private const uint WmLButtonDown = 0x0201;
    private const uint WmLButtonUp = 0x0202;
    private const uint WmRButtonDown = 0x0204;
    private const uint WmRButtonUp = 0x0205;
    private const uint WmMButtonDown = 0x0207;
    private const uint WmMButtonUp = 0x0208;
    private const uint WmMouseWheel = 0x020A;
    private const uint WmKeyDown = 0x0100;
    private const uint WmKeyUp = 0x0101;
    private const uint WmChar = 0x0102;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public short biPlanes;
        public short biBitCount;
        public int biCompression;
        public int biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public int biClrUsed;
        public int biClrImportant;
        public int colorsPlaceholder;
    }

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr RealChildWindowFromPoint(IntPtr parent, POINT point);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hwnd, ref POINT point);

    [DllImport("user32.dll")]
    private static extern bool ScreenToClient(IntPtr hwnd, ref POINT point);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hwnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO info, uint usage, out IntPtr bits, IntPtr section, uint offset);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);
}
