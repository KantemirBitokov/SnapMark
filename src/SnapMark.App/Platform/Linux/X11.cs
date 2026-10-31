using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using SnapMark.Core;

namespace SnapMark.Platform.Linux;

/// <summary>Минимальная обвязка libX11.</summary>
[SupportedOSPlatform("linux")]
internal static class Xlib
{
    const string Lib = "libX11.so.6";

    public delegate int ErrorHandler(IntPtr display, IntPtr errorEvent);

    [DllImport(Lib)] public static extern IntPtr XOpenDisplay(IntPtr name);
    [DllImport(Lib)] public static extern int XCloseDisplay(IntPtr display);
    [DllImport(Lib)] public static extern IntPtr XDefaultRootWindow(IntPtr display);
    [DllImport(Lib)] public static extern byte XKeysymToKeycode(IntPtr display, IntPtr keysym);
    [DllImport(Lib)] public static extern int XGrabKey(IntPtr display, int keycode, uint modifiers, IntPtr grabWindow, int ownerEvents, int pointerMode, int keyboardMode);
    [DllImport(Lib)] public static extern int XUngrabKey(IntPtr display, int keycode, uint modifiers, IntPtr grabWindow);
    [DllImport(Lib)] public static extern int XPending(IntPtr display);
    [DllImport(Lib)] public static extern int XNextEvent(IntPtr display, IntPtr eventReturn);
    [DllImport(Lib)] public static extern int XSync(IntPtr display, int discard);
    [DllImport(Lib)] public static extern IntPtr XSetErrorHandler(IntPtr handler);
    [DllImport(Lib)] public static extern IntPtr XGetImage(IntPtr display, IntPtr drawable, int x, int y, uint width, uint height, UIntPtr planeMask, int format);
    [DllImport(Lib)] public static extern int XFree(IntPtr data);

    public const int KeyPress = 2;
    public const int GrabModeAsync = 1;
    public const int ZPixmap = 2;

    public const uint ShiftMask = 1, LockMask = 2, ControlMask = 4, Mod1Mask = 8, Mod2Mask = 16, Mod4Mask = 64;

    /// <summary>Сеанс X11 (или XWayland с DISPLAY, но не чистый Wayland).</summary>
    public static bool IsX11Session =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")) && !IsWayland;

    public static bool IsWayland =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")) ||
        string.Equals(Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"), "wayland", StringComparison.OrdinalIgnoreCase);

    /// <summary>Имя клавиши Avalonia → X11 keysym.</summary>
    public static long? ToKeysym(string keyName)
    {
        if (!Enum.TryParse<Key>(keyName, true, out var key)) return null;
        if (key >= Key.A && key <= Key.Z) return 0x61 + (key - Key.A);
        if (key >= Key.D0 && key <= Key.D9) return 0x30 + (key - Key.D0);
        if (key >= Key.F1 && key <= Key.F24) return 0xFFBE + (key - Key.F1);
        if (key >= Key.NumPad0 && key <= Key.NumPad9) return 0xFFB0 + (key - Key.NumPad0);
        return key switch
        {
            Key.PrintScreen => 0xFF61,
            Key.Pause => 0xFF13,
            Key.Scroll => 0xFF14,
            Key.Insert => 0xFF63,
            Key.Delete => 0xFFFF,
            Key.Home => 0xFF50,
            Key.End => 0xFF57,
            Key.PageUp => 0xFF55,
            Key.PageDown => 0xFF56,
            Key.Space => 0x20,
            Key.Enter => 0xFF0D,
            Key.Tab => 0xFF09,
            Key.Left => 0xFF51,
            Key.Up => 0xFF52,
            Key.Right => 0xFF53,
            Key.Down => 0xFF54,
            Key.OemTilde => 0x60,
            Key.OemMinus => 0x2D,
            Key.OemPlus => 0x3D,
            _ => null,
        };
    }
}

/// <summary>
/// Глобальные горячие клавиши в сеансе X11 (XGrabKey). Своё соединение с X-сервером и свой поток.
/// Под Wayland глобальный перехват клавиш программам запрещён — там назначается системное сочетание
/// на команду «snapmark --region» (см. CommandLine).
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed class X11Hotkeys : IGlobalHotkeys
{
    readonly ConcurrentQueue<Action> _queue = new();
    readonly Dictionary<(int Code, uint Mods), HotkeyAction> _grabs = new();
    readonly Thread? _thread;
    IntPtr _display;
    IntPtr _root;
    volatile bool _running = true;
    static int _lastErrorCount;
    static Xlib.ErrorHandler? _errorHandler;

    public event Action<HotkeyAction>? Triggered;
    public bool IsSupported => Xlib.IsX11Session;
    public bool Suspended { get; set; }
    public string Status { get; private set; } = "";

    public X11Hotkeys()
    {
        if (!IsSupported)
        {
            Status = L.T("В сеансе Wayland программы не могут перехватывать клавиши. Назначь сочетание в настройках системы на команду: snapmark --region (или --full, --record).");
            return;
        }
        _thread = new Thread(Loop) { IsBackground = true, Name = "SnapMark X11 hotkeys" };
        _thread.Start();
    }

    public void Apply(AppSettings settings)
    {
        if (!IsSupported) return;
        var bindings = new[]
        {
            (HotkeyAction.CaptureRegion, settings.CaptureRegionHotkey.Clone()),
            (HotkeyAction.CaptureFull, settings.CaptureFullHotkey.Clone()),
            (HotkeyAction.RecordVideo, settings.RecordVideoHotkey.Clone()),
        };
        _queue.Enqueue(() => Grab(bindings));
    }

    void Loop()
    {
        _display = Xlib.XOpenDisplay(IntPtr.Zero);
        if (_display == IntPtr.Zero)
        {
            Status = L.T("Не удалось подключиться к X-серверу.");
            return;
        }
        _root = Xlib.XDefaultRootWindow(_display);
        var ev = Marshal.AllocHGlobal(256); // XEvent — 192 байта
        try
        {
            while (_running)
            {
                while (_queue.TryDequeue(out var job)) job();
                if (Xlib.XPending(_display) > 0)
                {
                    Xlib.XNextEvent(_display, ev);
                    if (Marshal.ReadInt32(ev) == Xlib.KeyPress)
                    {
                        // XKeyEvent (64 бита): state — смещение 80, keycode — 84.
                        uint state = (uint)Marshal.ReadInt32(ev, 80);
                        int keycode = Marshal.ReadInt32(ev, 84);
                        uint mods = state & (Xlib.ShiftMask | Xlib.ControlMask | Xlib.Mod1Mask | Xlib.Mod4Mask);
                        if (!Suspended && _grabs.TryGetValue((keycode, mods), out var action))
                            Dispatcher.UIThread.Post(() => Triggered?.Invoke(action));
                    }
                }
                else
                {
                    Thread.Sleep(15);
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(ev);
            UngrabAll();
            Xlib.XCloseDisplay(_display);
        }
    }

    void Grab((HotkeyAction Action, Hotkey Key)[] bindings)
    {
        UngrabAll();
        var report = new List<string>();

        // Ошибку «клавиша уже занята» (BadAccess) Xlib по умолчанию считает фатальной и закрывает программу.
        // На время регистрации ставим свой обработчик и потом возвращаем прежний.
        _errorHandler ??= (_, _) => { Interlocked.Increment(ref _lastErrorCount); return 0; };
        var previous = Xlib.XSetErrorHandler(Marshal.GetFunctionPointerForDelegate(_errorHandler));
        try
        {
            foreach (var (action, hk) in bindings)
            {
                if (hk.IsEmpty) continue;
                var sym = Xlib.ToKeysym(hk.Key);
                int code = sym is null ? 0 : Xlib.XKeysymToKeycode(_display, (IntPtr)sym.Value);
                if (code == 0)
                {
                    report.Add(L.F("{0}: клавиша не поддерживается", hk));
                    continue;
                }
                uint mods = (hk.Shift ? Xlib.ShiftMask : 0) | (hk.Ctrl ? Xlib.ControlMask : 0) |
                            (hk.Alt ? Xlib.Mod1Mask : 0) | (hk.Win ? Xlib.Mod4Mask : 0);

                int before = _lastErrorCount;
                // Те же сочетания с включёнными CapsLock / NumLock — иначе при NumLock клавиша «не работает».
                foreach (var extra in new[] { 0u, Xlib.LockMask, Xlib.Mod2Mask, Xlib.LockMask | Xlib.Mod2Mask })
                    Xlib.XGrabKey(_display, code, mods | extra, _root, 0, Xlib.GrabModeAsync, Xlib.GrabModeAsync);
                Xlib.XSync(_display, 0);

                if (_lastErrorCount != before)
                {
                    report.Add(L.F("{0}: сочетание занято другой программой (например, встроенными скриншотами системы)", hk));
                    foreach (var extra in new[] { 0u, Xlib.LockMask, Xlib.Mod2Mask, Xlib.LockMask | Xlib.Mod2Mask })
                        Xlib.XUngrabKey(_display, code, mods | extra, _root);
                    continue;
                }
                _grabs[(code, mods)] = action;
                report.Add(L.F("{0}: системная регистрация", hk));
            }
            Xlib.XSync(_display, 0);
        }
        finally
        {
            Xlib.XSetErrorHandler(previous);
        }
        Status = string.Join(Environment.NewLine, report);
    }

    void UngrabAll()
    {
        if (_display == IntPtr.Zero) return;
        foreach (var (code, mods) in _grabs.Keys)
            foreach (var extra in new[] { 0u, Xlib.LockMask, Xlib.Mod2Mask, Xlib.LockMask | Xlib.Mod2Mask })
                Xlib.XUngrabKey(_display, code, mods | extra, _root);
        _grabs.Clear();
    }

    public void Dispose() => _running = false;
}

/// <summary>Снимок экрана X11 напрямую через XGetImage — без внешних утилит.</summary>
[SupportedOSPlatform("linux")]
internal static class X11Capture
{
    public static Bitmap? Capture(PixelRect r)
    {
        var display = Xlib.XOpenDisplay(IntPtr.Zero);
        if (display == IntPtr.Zero) return null;
        try
        {
            var image = Xlib.XGetImage(display, Xlib.XDefaultRootWindow(display), r.X, r.Y,
                (uint)r.Width, (uint)r.Height, new UIntPtr(ulong.MaxValue), Xlib.ZPixmap);
            if (image == IntPtr.Zero) return null;
            try
            {
                // struct XImage (64 бита): data — 16, bytes_per_line — 44, bits_per_pixel — 48.
                var data = Marshal.ReadIntPtr(image, 16);
                int stride = Marshal.ReadInt32(image, 44);
                int bpp = Marshal.ReadInt32(image, 48);
                if (bpp != 32 || data == IntPtr.Zero) return null;

                var bmp = new WriteableBitmap(new PixelSize(r.Width, r.Height), new Vector(96, 96),
                    PixelFormat.Bgra8888, AlphaFormat.Opaque);
                using (var fb = bmp.Lock())
                {
                    var row = new byte[r.Width * 4];
                    for (int y = 0; y < r.Height; y++)
                    {
                        Marshal.Copy(data + y * stride, row, 0, row.Length);
                        for (int i = 3; i < row.Length; i += 4) row[i] = 255; // у X11 альфа обычно 0
                        Marshal.Copy(row, 0, fb.Address + y * fb.RowBytes, row.Length);
                    }
                }
                return bmp;
            }
            finally
            {
                var data = Marshal.ReadIntPtr(image, 16);
                if (data != IntPtr.Zero) Xlib.XFree(data);
                Xlib.XFree(image);
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            Xlib.XCloseDisplay(display);
        }
    }
}
