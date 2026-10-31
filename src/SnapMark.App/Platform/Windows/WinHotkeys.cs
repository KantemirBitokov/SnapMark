using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia.Threading;
using SnapMark.Core;
using static SnapMark.Platform.Windows.Native;

namespace SnapMark.Platform.Windows;

/// <summary>
/// Глобальные горячие клавиши для Windows.
///
/// Как это работает:
///  • Обычные сочетания (Ctrl+Shift+S, F9…) — RegisterHotKey. Это штатный и самый «вежливый» способ.
///  • PrtSc и сочетания с ним — низкоуровневый хук клавиатуры (WH_KEYBOARD_LL). На Windows 11 клавишу
///    PrtSc занимают «Ножницы», RegisterHotKey для неё часто не срабатывает. Хук получает нажатие раньше
///    системной обработки и «съедает» его — так же делают ShareX/PowerToys. Если всё же откроются «Ножницы»,
///    в настройках есть кнопка отключить системную привязку.
///  • Если RegisterHotKey вернул ошибку (сочетание занято другим приложением) — тоже переключаемся на хук.
///
/// Всё живёт в отдельном потоке со своим message loop: хуку нужен цикл сообщений,
/// а UI-поток Avalonia мы не трогаем.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WinHotkeys : IGlobalHotkeys, IPasteSupport
{
    const uint WM_REAPPLY = WM_APP + 1;
    const string ClassName = "SnapMark.HotkeyWindow";

    sealed record Binding(HotkeyAction Action, uint Vk, bool Ctrl, bool Shift, bool Alt, bool Win, string Display);

    public event Action<HotkeyAction>? Triggered;
    public event Action? PasteIntercepted;
    public bool IsSupported => true;
    public bool Intercept { get; set; }
    public bool Suspended { get; set; }
    public string Status { get; private set; } = "";

    readonly object _lock = new();
    readonly ManualResetEventSlim _ready = new(false);
    readonly Thread _thread;

    // Поля ниже меняются только в потоке хоткеев.
    IntPtr _hwnd, _hook;
    WndProc? _wndProc;                // ссылки на делегаты держим, чтобы их не собрал GC
    LowLevelKeyboardProc? _hookProc;
    readonly List<int> _registeredIds = new();
    readonly Dictionary<int, HotkeyAction> _idToAction = new();
    List<Binding> _hookBindings = new();
    uint _swallowUpVk;

    List<Binding> _pending = new();   // защищено _lock

    public WinHotkeys()
    {
        _thread = new Thread(ThreadMain) { IsBackground = true, Name = "SnapMark.Hotkeys" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait(3000);
    }

    public void Apply(AppSettings s)
    {
        var list = new List<Binding>();
        Add(list, HotkeyAction.CaptureRegion, s.CaptureRegionHotkey);
        Add(list, HotkeyAction.CaptureFull, s.CaptureFullHotkey);
        Add(list, HotkeyAction.RecordVideo, s.RecordVideoHotkey);
        lock (_lock) _pending = list;
        if (_hwnd != IntPtr.Zero) PostMessage(_hwnd, WM_REAPPLY, IntPtr.Zero, IntPtr.Zero);
    }

    static void Add(List<Binding> list, HotkeyAction action, Hotkey hk)
    {
        if (hk.IsEmpty) return;
        var vk = KeyMap.ToVirtualKey(hk.Key);
        if (vk == 0) return;
        list.Add(new Binding(action, vk, hk.Ctrl, hk.Shift, hk.Alt, hk.Win, hk.ToString()));
    }

    // ── Поток хоткеев ───────────────────────────────────────────────────
    void ThreadMain()
    {
        _wndProc = WndProcImpl;
        var hInstance = GetModuleHandle(null);
        var wc = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = hInstance,
            lpszClassName = ClassName,
        };
        RegisterClassEx(ref wc);
        _hwnd = CreateWindowEx(0, ClassName, "", 0, 0, 0, 0, 0, HWND_MESSAGE, IntPtr.Zero, hInstance, IntPtr.Zero);

        _hookProc = HookProc;
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _hookProc, hInstance, 0);

        Reapply(); // на случай, если Apply() вызвали раньше, чем окно было создано
        _ready.Set();

        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
    }

    IntPtr WndProcImpl(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_REAPPLY:
                Reapply();
                return IntPtr.Zero;

            case WM_HOTKEY:
                if (_idToAction.TryGetValue(wParam.ToInt32(), out var action)) Raise(action);
                return IntPtr.Zero;

            case WM_DESTROY:
                UnregisterAll();
                PostQuitMessage(0);
                return IntPtr.Zero;
        }
        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    void Reapply()
    {
        List<Binding> bindings;
        lock (_lock) bindings = _pending;

        UnregisterAll();
        var hookList = new List<Binding>();
        var report = new List<string>();
        int id = 1;

        foreach (var b in bindings)
        {
            bool viaHook = b.Vk == VK_SNAPSHOT;
            if (!viaHook)
            {
                uint mods = MOD_NOREPEAT;
                if (b.Ctrl) mods |= MOD_CONTROL;
                if (b.Shift) mods |= MOD_SHIFT;
                if (b.Alt) mods |= MOD_ALT;
                if (b.Win) mods |= MOD_WIN;

                if (RegisterHotKey(_hwnd, id, mods, b.Vk))
                {
                    _registeredIds.Add(id);
                    _idToAction[id] = b.Action;
                    report.Add(L.F("{0}: системная регистрация", b.Display));
                }
                else
                {
                    viaHook = true; // занято кем-то ещё — перехватываем хуком
                }
                id++;
            }

            if (viaHook)
            {
                if (_hook == IntPtr.Zero)
                {
                    report.Add(L.F("{0}: не удалось установить хук клавиатуры", b.Display));
                    continue;
                }
                hookList.Add(b);
                report.Add(L.F("{0}: перехват клавиатурным хуком", b.Display));
            }
        }

        _hookBindings = hookList;
        Status = string.Join(Environment.NewLine, report);
    }

    void UnregisterAll()
    {
        foreach (var id in _registeredIds) UnregisterHotKey(_hwnd, id);
        _registeredIds.Clear();
        _idToAction.Clear();
    }

    IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (Intercept || (!Suspended && _hookBindings.Count > 0)))
        {
            var kb = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            int msg = wParam.ToInt32();
            bool down = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
            bool up = msg == WM_KEYUP || msg == WM_SYSKEYUP;
            bool injected = (kb.flags & LLKHF_INJECTED) != 0;

            // Умная вставка: глотаем настоящий Ctrl+V (наши собственные эмулированные нажатия пропускаем).
            if (Intercept && down && !injected && kb.vkCode == VK_V &&
                Pressed(VK_CONTROL) && !Pressed(VK_MENU) && !Pressed(VK_SHIFT) && !Pressed(VK_LWIN) && !Pressed(VK_RWIN))
            {
                Dispatcher.UIThread.Post(() => PasteIntercepted?.Invoke());
                return new IntPtr(1);
            }

            if (!Suspended && _hookBindings.Count > 0)
            {
                if (up && _swallowUpVk == kb.vkCode)
                {
                    _swallowUpVk = 0;
                    return new IntPtr(1);
                }

                if (down)
                {
                    // Автоповтор при удержании: глотаем, но не срабатываем повторно.
                    if (_swallowUpVk == kb.vkCode) return new IntPtr(1);

                    foreach (var b in _hookBindings)
                    {
                        if (b.Vk == kb.vkCode && ModifiersMatch(b))
                        {
                            _swallowUpVk = kb.vkCode;
                            Raise(b.Action);
                            return new IntPtr(1); // «съедаем» нажатие — система (и «Ножницы») его не увидит
                        }
                    }
                }
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    // ── IPasteSupport ───────────────────────────────────────────────────
    /// <summary>Эмулирует Ctrl+V. Если пользователь ещё держит Ctrl — достаточно нажать V.</summary>
    public void SendPaste()
    {
        bool ctrlHeld = Pressed(VK_CONTROL);
        if (!ctrlHeld) keybd_event((byte)VK_CONTROL, 0, 0, UIntPtr.Zero);
        keybd_event((byte)VK_V, 0, 0, UIntPtr.Zero);
        keybd_event((byte)VK_V, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        if (!ctrlHeld) keybd_event((byte)VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    public string ForegroundProcessName()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            GetWindowThreadProcessId(hwnd, out uint pid);
            using var p = System.Diagnostics.Process.GetProcessById((int)pid);
            return p.ProcessName.ToLowerInvariant();
        }
        catch
        {
            return "";
        }
    }

    public uint ClipboardSequence() => GetClipboardSequenceNumber();

    static bool Pressed(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    static bool ModifiersMatch(Binding b) =>
        Pressed(VK_CONTROL) == b.Ctrl &&
        Pressed(VK_SHIFT) == b.Shift &&
        Pressed(VK_MENU) == b.Alt &&
        (Pressed(VK_LWIN) || Pressed(VK_RWIN)) == b.Win;

    void Raise(HotkeyAction action)
    {
        if (Suspended) return;
        Dispatcher.UIThread.Post(() => Triggered?.Invoke(action));
    }

    public void Dispose()
    {
        if (_hwnd != IntPtr.Zero) PostMessage(_hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
    }
}
