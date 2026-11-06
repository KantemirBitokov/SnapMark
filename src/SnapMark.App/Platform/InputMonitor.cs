using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Avalonia;

namespace SnapMark.Platform
{

public enum ClickButton { Left, Right, Middle }

/// <summary>Глобальный клик. Позиция — в координатах ОС: физические пиксели (Windows, X11) или точки (macOS).</summary>
public readonly record struct InputClick(double X, double Y, ClickButton Button);

/// <summary>
/// Слежение за вводом во всей системе (только чтение, ничего не перехватывается):
/// клики мыши — для пошаговой инструкции и подсветки кликов в видео, сочетания клавиш — для показа на видео.
/// Обычный набор текста не сообщается: только сочетания с Ctrl / Alt / Win / ⌘ и служебные клавиши
/// (Enter, Esc, Tab, стрелки, F1…), чтобы в видео не попали пароли и переписка.
/// События приходят в фоновом потоке. Click на Windows вызывается прямо из хука — ДО того, как
/// приложение получит клик (успевает снять «как было в момент нажатия»), поэтому обработчик должен быть быстрым.
/// </summary>
public interface IInputMonitor
{
    bool IsSupported { get; }
    /// <summary>Позиции кликов в точках, а не в пикселях (macOS).</summary>
    bool LogicalCoordinates { get; }
    /// <summary>Почему не работает (нет разрешения, Wayland…), иначе null.</summary>
    string? Problem { get; }
    event Action<InputClick>? Click;
    /// <summary>Готовая подпись сочетания: «Ctrl + Shift + S», «⌘ ⇧ S», «Enter».</summary>
    event Action<string>? KeyCombo;
    /// <summary>Начать слежение (счётчик ссылок: остановится, когда все подписчики освободят).</summary>
    IDisposable Acquire();
}

public static class InputMonitor
{
    static IInputMonitor? _instance;

    public static IInputMonitor Instance => _instance ??= Create();

    static IInputMonitor Create()
    {
        try
        {
            if (OperatingSystem.IsWindows()) return new Windows.WinInputMonitor();
            if (OperatingSystem.IsMacOS()) return new Mac.MacInputMonitor();
            if (OperatingSystem.IsLinux() && Linux.Xlib.IsX11Session) return new Linux.X11InputMonitor();
        }
        catch { }
        return new NoInputMonitor();
    }

    /// <summary>Сочетание для показа: модификаторы + клавиша. null — обычный набор текста, не показываем.</summary>
    public static string? FormatCombo(bool ctrl, bool alt, bool shift, bool win, string? key, bool special)
    {
        if (string.IsNullOrEmpty(key)) return null;
        if (!(ctrl || alt || win || special)) return null; // буквы, цифры, Shift+буква — это набор текста
        bool mac = OperatingSystem.IsMacOS();
        var parts = new List<string>();
        if (mac)
        {
            if (ctrl) parts.Add("⌃");
            if (alt) parts.Add("⌥");
            if (shift) parts.Add("⇧");
            if (win) parts.Add("⌘");
            parts.Add(key);
            return string.Join(" ", parts);
        }
        if (ctrl) parts.Add("Ctrl");
        if (win) parts.Add("Win");
        if (alt) parts.Add("Alt");
        if (shift) parts.Add("Shift");
        parts.Add(key);
        return string.Join(" + ", parts);
    }
}

/// <summary>Базовая часть: счётчик подписчиков и запуск/остановка.</summary>
internal abstract class InputMonitorBase : IInputMonitor
{
    readonly object _lock = new();
    int _refs;

    public abstract bool IsSupported { get; }
    public virtual bool LogicalCoordinates => false;
    public string? Problem { get; protected set; }
    public event Action<InputClick>? Click;
    public event Action<string>? KeyCombo;

    protected void RaiseClick(InputClick c)
    {
        try { Click?.Invoke(c); } catch { }
    }

    protected void RaiseCombo(string? combo)
    {
        if (combo is null) return;
        try { KeyCombo?.Invoke(combo); } catch { }
    }

    public IDisposable Acquire()
    {
        lock (_lock)
        {
            if (_refs++ == 0)
            {
                try { Start(); }
                catch (Exception ex) { Problem = ex.Message; }
            }
        }
        return new Releaser(this);
    }

    void Release()
    {
        lock (_lock)
        {
            if (_refs == 0) return;
            if (--_refs == 0)
            {
                try { Stop(); } catch { }
            }
        }
    }

    protected abstract void Start();
    protected abstract void Stop();

    sealed class Releaser(InputMonitorBase owner) : IDisposable
    {
        int _done;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0) owner.Release();
        }
    }
}

internal sealed class NoInputMonitor : InputMonitorBase
{
    public NoInputMonitor()
    {
        Problem = OperatingSystem.IsLinux()
            ? "Wayland не разрешает программам следить за кликами во всей системе. Войди в сеанс X11 (Xorg)."
            : "Эта система не поддерживается.";
    }

    public override bool IsSupported => false;
    protected override void Start() { }
    protected override void Stop() { }
}

}

// ═════════════════════════════════ Windows ═════════════════════════════════
namespace SnapMark.Platform.Windows
{
    /// <summary>Низкоуровневые хуки мыши и клавиатуры (WH_MOUSE_LL / WH_KEYBOARD_LL) в своём потоке.</summary>
    [SupportedOSPlatform("windows")]
    internal sealed class WinInputMonitor : InputMonitorBase
    {
        delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData, flags, time; public IntPtr extra; }

        [StructLayout(LayoutKind.Sequential)]
        struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr extra; }

        [StructLayout(LayoutKind.Sequential)]
        struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public POINT pt; }

        [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int id, HookProc fn, IntPtr mod, uint thread);
        [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
        [DllImport("user32.dll")] static extern bool PostThreadMessage(uint thread, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string? name);

        const int WH_KEYBOARD_LL = 13, WH_MOUSE_LL = 14;
        const int WM_LBUTTONDOWN = 0x201, WM_RBUTTONDOWN = 0x204, WM_MBUTTONDOWN = 0x207;
        const int WM_KEYDOWN = 0x100, WM_SYSKEYDOWN = 0x104;
        const uint WM_QUIT = 0x12;
        const uint LLMHF_INJECTED = 1;

        HookProc? _mouseProc, _keyProc; // ссылки держим, иначе сборщик мусора уберёт колбэки
        IntPtr _mouseHook, _keyHook;
        uint _threadId;
        Thread? _thread;

        public override bool IsSupported => true;

        protected override void Start()
        {
            using var ready = new ManualResetEventSlim();
            _thread = new Thread(() =>
            {
                _threadId = GetCurrentThreadId();
                _mouseProc = MouseProc;
                _keyProc = KeyProc;
                var mod = GetModuleHandle(null);
                _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, mod, 0);
                _keyHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyProc, mod, 0);
                Problem = _mouseHook == IntPtr.Zero ? "Не удалось установить хук мыши." : null;
                ready.Set();
                while (GetMessage(out _, IntPtr.Zero, 0, 0) > 0) { }
                if (_mouseHook != IntPtr.Zero) UnhookWindowsHookEx(_mouseHook);
                if (_keyHook != IntPtr.Zero) UnhookWindowsHookEx(_keyHook);
                _mouseHook = _keyHook = IntPtr.Zero;
            }) { IsBackground = true, Name = "SnapMark input monitor" };
            _thread.Start();
            ready.Wait(2000);
        }

        protected override void Stop()
        {
            if (_threadId != 0) PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            _thread = null;
            _threadId = 0;
        }

        IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int msg = wParam.ToInt32();
                if (msg is WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN)
                {
                    var m = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                    if ((m.flags & LLMHF_INJECTED) == 0)
                        RaiseClick(new InputClick(m.pt.X, m.pt.Y, msg switch
                        {
                            WM_RBUTTONDOWN => ClickButton.Right,
                            WM_MBUTTONDOWN => ClickButton.Middle,
                            _ => ClickButton.Left,
                        }));
                }
            }
            return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
        }

        IntPtr KeyProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int msg = wParam.ToInt32();
                if (msg is WM_KEYDOWN or WM_SYSKEYDOWN)
                {
                    var k = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                    var (name, special) = KeyName(k.vkCode);
                    if (name is not null)
                        RaiseCombo(InputMonitor.FormatCombo(Down(0x11), Down(0x12), Down(0x10), Down(0x5B) || Down(0x5C), name, special));
                }
            }
            return CallNextHookEx(_keyHook, nCode, wParam, lParam);
        }

        static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

        /// <summary>Имя клавиши по VK-коду; special — показывать и без модификаторов. null — модификатор.</summary>
        static (string? Name, bool Special) KeyName(uint vk)
        {
            if (vk is >= 0x41 and <= 0x5A) return (((char)vk).ToString(), false);
            if (vk is >= 0x30 and <= 0x39) return (((char)vk).ToString(), false);
            if (vk is >= 0x60 and <= 0x69) return ("Num " + (vk - 0x60), false);
            if (vk is >= 0x70 and <= 0x87) return ("F" + (vk - 0x6F), true);
            return vk switch
            {
                0x08 => ("Backspace", true), 0x09 => ("Tab", true), 0x0D => ("Enter", true), 0x1B => ("Esc", true),
                0x20 => ("Space", false), 0x21 => ("PgUp", true), 0x22 => ("PgDn", true), 0x23 => ("End", true),
                0x24 => ("Home", true), 0x25 => ("←", true), 0x26 => ("↑", true), 0x27 => ("→", true), 0x28 => ("↓", true),
                0x2C => ("PrtSc", true), 0x2D => ("Ins", true), 0x2E => ("Del", true),
                0xBA => (";", false), 0xBB => ("=", false), 0xBC => (",", false), 0xBD => ("-", false),
                0xBE => (".", false), 0xBF => ("/", false), 0xC0 => ("`", false), 0xDB => ("[", false),
                0xDC => ("\\", false), 0xDD => ("]", false), 0xDE => ("'", false),
                0x6A => ("Num *", false), 0x6B => ("Num +", false), 0x6D => ("Num -", false), 0x6F => ("Num /", false),
                _ => (null, false), // модификаторы и прочее
            };
        }
    }
}

// ═════════════════════════════════ Linux (X11) ═════════════════════════════════
namespace SnapMark.Platform.Linux
{
    /// <summary>
    /// X11: опрос состояния мыши и клавиатуры 60 раз в секунду (XQueryPointer / XQueryKeymap) в своём потоке
    /// и своём соединении — без перехвата, ничего не мешает другим программам.
    /// </summary>
    [SupportedOSPlatform("linux")]
    internal sealed class X11InputMonitor : InputMonitorBase
    {
        const string Lib = "libX11.so.6";
        [DllImport(Lib)] static extern IntPtr XOpenDisplay(IntPtr name);
        [DllImport(Lib)] static extern int XCloseDisplay(IntPtr d);
        [DllImport(Lib)] static extern IntPtr XDefaultRootWindow(IntPtr d);
        [DllImport(Lib)] static extern int XQueryPointer(IntPtr d, IntPtr w, out IntPtr root, out IntPtr child,
            out int rootX, out int rootY, out int winX, out int winY, out uint mask);
        [DllImport(Lib)] static extern int XQueryKeymap(IntPtr d, byte[] keys);
        [DllImport(Lib)] static extern IntPtr XkbKeycodeToKeysym(IntPtr d, byte keycode, int group, int level);
        [DllImport(Lib)] static extern IntPtr XKeysymToString(IntPtr keysym);

        volatile bool _run;
        Thread? _thread;

        public override bool IsSupported => true;

        protected override void Start()
        {
            _run = true;
            _thread = new Thread(Loop) { IsBackground = true, Name = "SnapMark input monitor" };
            _thread.Start();
        }

        protected override void Stop() => _run = false;

        void Loop()
        {
            var d = XOpenDisplay(IntPtr.Zero);
            if (d == IntPtr.Zero) { Problem = "Нет соединения с X-сервером."; return; }
            try
            {
                var root = XDefaultRootWindow(d);
                uint prevMask = 0;
                var prev = new byte[32];
                var keys = new byte[32];
                bool first = true;
                while (_run)
                {
                    if (XQueryPointer(d, root, out _, out _, out int x, out int y, out _, out _, out uint mask) != 0)
                    {
                        if (!first)
                        {
                            if ((mask & 0x100) != 0 && (prevMask & 0x100) == 0) RaiseClick(new InputClick(x, y, ClickButton.Left));
                            if ((mask & 0x400) != 0 && (prevMask & 0x400) == 0) RaiseClick(new InputClick(x, y, ClickButton.Right));
                            if ((mask & 0x200) != 0 && (prevMask & 0x200) == 0) RaiseClick(new InputClick(x, y, ClickButton.Middle));
                        }
                        prevMask = mask;
                    }
                    XQueryKeymap(d, keys);
                    if (!first)
                    {
                        for (int i = 0; i < 256; i++)
                        {
                            bool now = (keys[i >> 3] & (1 << (i & 7))) != 0;
                            bool was = (prev[i >> 3] & (1 << (i & 7))) != 0;
                            if (now && !was) OnKey(d, (byte)i, keys);
                        }
                    }
                    Array.Copy(keys, prev, 32);
                    first = false;
                    Thread.Sleep(16);
                }
            }
            finally
            {
                XCloseDisplay(d);
            }
        }

        void OnKey(IntPtr d, byte keycode, byte[] keys)
        {
            long sym = XkbKeycodeToKeysym(d, keycode, 0, 0).ToInt64();
            if (sym is >= 0xFFE1 and <= 0xFFEE) return; // модификаторы
            bool ctrl = false, alt = false, shift = false, super = false;
            for (int i = 8; i < 256; i++)
            {
                if ((keys[i >> 3] & (1 << (i & 7))) == 0) continue;
                long s = XkbKeycodeToKeysym(d, (byte)i, 0, 0).ToInt64();
                if (s is 0xFFE3 or 0xFFE4) ctrl = true;
                else if (s is 0xFFE9 or 0xFFEA) alt = true;
                else if (s is 0xFFE1 or 0xFFE2) shift = true;
                else if (s is 0xFFEB or 0xFFEC) super = true;
            }
            var (name, special) = sym switch
            {
                >= 0x61 and <= 0x7A => (((char)(sym - 0x20)).ToString(), false),
                >= 0x30 and <= 0x39 => (((char)sym).ToString(), false),
                >= 0xFFBE and <= 0xFFD5 => ("F" + (sym - 0xFFBD), true),
                0xFF0D => ("Enter", true), 0xFF1B => ("Esc", true), 0xFF09 => ("Tab", true), 0xFF08 => ("Backspace", true),
                0xFFFF => ("Del", true), 0xFF51 => ("←", true), 0xFF52 => ("↑", true), 0xFF53 => ("→", true), 0xFF54 => ("↓", true),
                0xFF50 => ("Home", true), 0xFF57 => ("End", true), 0xFF55 => ("PgUp", true), 0xFF56 => ("PgDn", true),
                0xFF61 => ("PrtSc", true), 0x20 => ("Space", false),
                _ => (Marshal.PtrToStringAnsi(XKeysymToString(new IntPtr(sym))), false),
            };
            RaiseCombo(InputMonitor.FormatCombo(ctrl, alt, shift, super, name, special));
        }
    }
}

// ═════════════════════════════════ macOS ═════════════════════════════════
namespace SnapMark.Platform.Mac
{
    /// <summary>
    /// macOS: CGEventTap только для чтения (kCGEventTapOptionListenOnly) в своём потоке с CFRunLoop.
    /// Нужно разрешение «Мониторинг ввода» (Конфиденциальность и безопасность); без него тап не создаётся.
    /// </summary>
    [SupportedOSPlatform("macos")]
    internal sealed class MacInputMonitor : InputMonitorBase
    {
        const string CG = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
        const string CF = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

        delegate IntPtr TapCallback(IntPtr proxy, uint type, IntPtr evt, IntPtr info);

        [StructLayout(LayoutKind.Sequential)] struct CGPoint { public double X, Y; }

        [DllImport(CG)] static extern IntPtr CGEventTapCreate(uint tap, uint place, uint options, ulong mask, TapCallback cb, IntPtr info);
        [DllImport(CG)] static extern void CGEventTapEnable(IntPtr tap, [MarshalAs(UnmanagedType.I1)] bool enable);
        [DllImport(CG)] static extern CGPoint CGEventGetLocation(IntPtr evt);
        [DllImport(CG)] static extern ulong CGEventGetFlags(IntPtr evt);
        [DllImport(CG)] static extern long CGEventGetIntegerValueField(IntPtr evt, uint field);
        [DllImport(CF)] static extern IntPtr CFMachPortCreateRunLoopSource(IntPtr alloc, IntPtr port, long order);
        [DllImport(CF)] static extern IntPtr CFRunLoopGetCurrent();
        [DllImport(CF)] static extern void CFRunLoopAddSource(IntPtr rl, IntPtr source, IntPtr mode);
        [DllImport(CF)] static extern void CFRunLoopRun();
        [DllImport(CF)] static extern void CFRunLoopStop(IntPtr rl);
        [DllImport(CF)] static extern void CFRelease(IntPtr obj);
        [DllImport("/usr/lib/libSystem.dylib")] static extern IntPtr dlopen(string path, int mode);
        [DllImport("/usr/lib/libSystem.dylib")] static extern IntPtr dlsym(IntPtr handle, string name);

        const uint LeftDown = 1, RightDown = 3, KeyDown = 10, OtherDown = 25;
        const uint DisabledByTimeout = 0xFFFFFFFE, DisabledByUser = 0xFFFFFFFF;

        TapCallback? _cb;
        IntPtr _tap, _loop;

        public override bool IsSupported => true;
        public override bool LogicalCoordinates => true;

        protected override void Start()
        {
            using var ready = new ManualResetEventSlim();
            var t = new Thread(() =>
            {
                _cb = OnEvent;
                ulong mask = (1UL << (int)LeftDown) | (1UL << (int)RightDown) | (1UL << (int)OtherDown) | (1UL << (int)KeyDown);
                _tap = CGEventTapCreate(1 /*session*/, 0 /*head*/, 1 /*listen only*/, mask, _cb, IntPtr.Zero);
                if (_tap == IntPtr.Zero)
                {
                    Problem = "Нужно разрешение «Мониторинг ввода»: Системные настройки → Конфиденциальность и безопасность → Мониторинг ввода → включи SnapMark.";
                    ready.Set();
                    return;
                }
                Problem = null;
                var src = CFMachPortCreateRunLoopSource(IntPtr.Zero, _tap, 0);
                _loop = CFRunLoopGetCurrent();
                CFRunLoopAddSource(_loop, src, CommonModes());
                CGEventTapEnable(_tap, true);
                ready.Set();
                CFRunLoopRun();
                CGEventTapEnable(_tap, false);
                CFRelease(src);
                CFRelease(_tap);
                _tap = IntPtr.Zero;
            }) { IsBackground = true, Name = "SnapMark input monitor" };
            t.Start();
            ready.Wait(2000);
        }

        protected override void Stop()
        {
            if (_loop != IntPtr.Zero) CFRunLoopStop(_loop);
            _loop = IntPtr.Zero;
        }

        static IntPtr CommonModes()
        {
            var h = dlopen(CF, 1);
            var p = dlsym(h, "kCFRunLoopCommonModes");
            return p == IntPtr.Zero ? IntPtr.Zero : Marshal.ReadIntPtr(p);
        }

        IntPtr OnEvent(IntPtr proxy, uint type, IntPtr evt, IntPtr info)
        {
            try
            {
                if (type is DisabledByTimeout or DisabledByUser)
                {
                    if (_tap != IntPtr.Zero) CGEventTapEnable(_tap, true);
                    return evt;
                }
                if (type is LeftDown or RightDown or OtherDown)
                {
                    var p = CGEventGetLocation(evt);
                    RaiseClick(new InputClick(p.X, p.Y, type == RightDown ? ClickButton.Right : type == OtherDown ? ClickButton.Middle : ClickButton.Left));
                }
                else if (type == KeyDown)
                {
                    ulong f = CGEventGetFlags(evt);
                    long code = CGEventGetIntegerValueField(evt, 9 /*kCGKeyboardEventKeycode*/);
                    var (name, special) = MacKey(code);
                    RaiseCombo(InputMonitor.FormatCombo((f & 0x40000) != 0, (f & 0x80000) != 0, (f & 0x20000) != 0,
                        (f & 0x100000) != 0, name, special));
                }
            }
            catch { }
            return evt;
        }

        // Виртуальные коды клавиш macOS (раскладка ANSI).
        static readonly string[] Letters =
        {
            "A", "S", "D", "F", "H", "G", "Z", "X", "C", "V", "§", "B", "Q", "W", "E", "R", "Y", "T", "1", "2", "3", "4", "6", "5",
            "=", "9", "7", "-", "8", "0", "]", "O", "U", "[", "I", "P", "↩", "L", "J", "'", "K", ";", "\\", ",", "/", "N", "M", ".",
        };

        static (string? Name, bool Special) MacKey(long code) => code switch
        {
            36 => ("↩", true), 48 => ("⇥", true), 49 => ("Space", false), 51 => ("⌫", true), 53 => ("Esc", true),
            117 => ("⌦", true), 123 => ("←", true), 124 => ("→", true), 125 => ("↓", true), 126 => ("↑", true),
            115 => ("Home", true), 119 => ("End", true), 116 => ("PgUp", true), 121 => ("PgDn", true),
            122 => ("F1", true), 120 => ("F2", true), 99 => ("F3", true), 118 => ("F4", true), 96 => ("F5", true),
            97 => ("F6", true), 98 => ("F7", true), 100 => ("F8", true), 101 => ("F9", true), 109 => ("F10", true),
            103 => ("F11", true), 111 => ("F12", true),
            >= 0 and < 48 => (Letters[code], false),
            50 => ("`", false),
            _ => (null, false),
        };
    }
}
