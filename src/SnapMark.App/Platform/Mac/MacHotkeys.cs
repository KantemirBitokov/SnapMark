using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia.Input;
using Avalonia.Threading;
using SnapMark.Core;

namespace SnapMark.Platform.Mac;

/// <summary>
/// Глобальные горячие клавиши macOS через Carbon RegisterEventHotKey.
/// Этот способ не требует разрешения «Универсальный доступ» и работает из строки меню.
/// Регистрация и события — в главном потоке (там живёт цикл событий приложения).
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed class MacHotkeys : IGlobalHotkeys
{
    [StructLayout(LayoutKind.Sequential)]
    struct EventHotKeyID { public uint Signature; public uint Id; }

    [StructLayout(LayoutKind.Sequential)]
    struct EventTypeSpec { public uint EventClass; public uint EventKind; }

    delegate int EventHandlerProc(IntPtr callRef, IntPtr eventRef, IntPtr userData);

    [DllImport(MacNative.Carbon)] static extern IntPtr GetApplicationEventTarget();
    [DllImport(MacNative.Carbon)] static extern int InstallEventHandler(IntPtr target, EventHandlerProc handler, uint numTypes, EventTypeSpec[] list, IntPtr userData, out IntPtr handlerRef);
    [DllImport(MacNative.Carbon)] static extern int RegisterEventHotKey(uint keyCode, uint modifiers, EventHotKeyID id, IntPtr target, uint options, out IntPtr hotKeyRef);
    [DllImport(MacNative.Carbon)] static extern int UnregisterEventHotKey(IntPtr hotKeyRef);
    [DllImport(MacNative.Carbon)] static extern int GetEventParameter(IntPtr evt, uint name, uint desiredType, IntPtr actualType, uint bufferSize, IntPtr actualSize, out EventHotKeyID data);

    const uint KEventClassKeyboard = 0x6B657962; // 'keyb'
    const uint KEventHotKeyPressed = 5;
    const uint KEventParamDirectObject = 0x2D2D2D2D; // '----'
    const uint TypeEventHotKeyID = 0x686B6964; // 'hkid'
    const uint Signature = 0x534E4150; // 'SNAP'

    const uint CmdKey = 0x0100, ShiftKey = 0x0200, OptionKey = 0x0800, ControlKey = 0x1000;

    static EventHandlerProc? _handler; // держим ссылку, иначе сборщик мусора уберёт колбэк
    readonly Dictionary<uint, HotkeyAction> _actions = new();
    readonly List<IntPtr> _refs = new();
    AppSettings? _settings;
    bool _suspended;
    bool _installed;

    public event Action<HotkeyAction>? Triggered;
    public bool IsSupported => true;
    public string Status { get; private set; } = "";

    public bool Suspended
    {
        get => _suspended;
        set
        {
            if (_suspended == value) return;
            _suspended = value;
            // Пока пользователь вводит новое сочетание — снимаем регистрацию, иначе система «съест» нажатие.
            Dispatcher.UIThread.Post(() =>
            {
                if (_suspended) UnregisterAll();
                else if (_settings is not null) Register(_settings);
            });
        }
    }

    public void Apply(AppSettings settings)
    {
        _settings = settings;
        Dispatcher.UIThread.Post(() =>
        {
            if (!_suspended) Register(settings);
        });
    }

    void EnsureHandler()
    {
        if (_installed) return;
        _handler = OnHotKeyEvent;
        var spec = new[] { new EventTypeSpec { EventClass = KEventClassKeyboard, EventKind = KEventHotKeyPressed } };
        InstallEventHandler(GetApplicationEventTarget(), _handler, 1, spec, IntPtr.Zero, out _);
        _installed = true;
    }

    void Register(AppSettings s)
    {
        var report = new List<string>();
        try
        {
            EnsureHandler();
            UnregisterAll();
            uint id = 1;
            foreach (var (action, hk) in new[]
                     {
                         (HotkeyAction.CaptureRegion, s.CaptureRegionHotkey),
                         (HotkeyAction.CaptureFull, s.CaptureFullHotkey),
                         (HotkeyAction.RecordVideo, s.RecordVideoHotkey),
                         (HotkeyAction.StepGuide, s.StepGuideHotkey),
                     })
            {
                if (hk.IsEmpty) continue;
                var code = ToMacKeyCode(hk.Key);
                if (code is null)
                {
                    report.Add(L.F("{0}: клавиша не поддерживается на Mac", hk));
                    continue;
                }
                uint mods = (hk.Win ? CmdKey : 0) | (hk.Shift ? ShiftKey : 0) | (hk.Alt ? OptionKey : 0) | (hk.Ctrl ? ControlKey : 0);
                var hkId = new EventHotKeyID { Signature = Signature, Id = id };
                if (RegisterEventHotKey(code.Value, mods, hkId, GetApplicationEventTarget(), 0, out var r) == 0)
                {
                    _refs.Add(r);
                    _actions[id] = action;
                    report.Add(L.F("{0}: системная регистрация", hk));
                }
                else
                {
                    report.Add(L.F("{0}: сочетание занято другой программой", hk));
                }
                id++;
            }
        }
        catch (Exception ex)
        {
            report.Add(ex.Message);
        }
        Status = string.Join(Environment.NewLine, report);
    }

    int OnHotKeyEvent(IntPtr callRef, IntPtr evt, IntPtr userData)
    {
        try
        {
            if (GetEventParameter(evt, KEventParamDirectObject, TypeEventHotKeyID, IntPtr.Zero,
                    (uint)Marshal.SizeOf<EventHotKeyID>(), IntPtr.Zero, out var hk) == 0
                && hk.Signature == Signature && !_suspended && _actions.TryGetValue(hk.Id, out var action))
            {
                Dispatcher.UIThread.Post(() => Triggered?.Invoke(action));
            }
        }
        catch { }
        return 0; // noErr
    }

    void UnregisterAll()
    {
        foreach (var r in _refs)
        {
            try { UnregisterEventHotKey(r); } catch { }
        }
        _refs.Clear();
        _actions.Clear();
    }

    public void Dispose() => UnregisterAll();

    /// <summary>Имя клавиши Avalonia → виртуальный код клавиатуры Mac (kVK_*, раскладка ANSI).</summary>
    static uint? ToMacKeyCode(string keyName)
    {
        if (!Enum.TryParse<Key>(keyName, true, out var key)) return null;
        uint[] letters = { 0x00, 0x0B, 0x08, 0x02, 0x0E, 0x03, 0x05, 0x04, 0x22, 0x26, 0x28, 0x25, 0x2E, 0x2D, 0x1F, 0x23, 0x0C, 0x0F, 0x01, 0x11, 0x20, 0x09, 0x0D, 0x07, 0x10, 0x06 };
        uint[] digits = { 0x1D, 0x12, 0x13, 0x14, 0x15, 0x17, 0x16, 0x1A, 0x1C, 0x19 };
        uint[] fkeys = { 0x7A, 0x78, 0x63, 0x76, 0x60, 0x61, 0x62, 0x64, 0x65, 0x6D, 0x67, 0x6F, 0x69, 0x6B, 0x71, 0x6A, 0x40, 0x4F, 0x50, 0x5A };
        uint[] keypad = { 0x52, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5B, 0x5C };

        if (key >= Key.A && key <= Key.Z) return letters[key - Key.A];
        if (key >= Key.D0 && key <= Key.D9) return digits[key - Key.D0];
        if (key >= Key.F1 && key <= Key.F20) return fkeys[key - Key.F1];
        if (key >= Key.NumPad0 && key <= Key.NumPad9) return keypad[key - Key.NumPad0];
        return key switch
        {
            Key.PrintScreen => 0x69, // на PC-клавиатуре под Mac PrtSc приходит как F13
            Key.Space => 0x31,
            Key.Enter => 0x24,
            Key.Tab => 0x30,
            Key.Home => 0x73,
            Key.End => 0x77,
            Key.PageUp => 0x74,
            Key.PageDown => 0x79,
            Key.Delete => 0x75,
            Key.Left => 0x7B,
            Key.Right => 0x7C,
            Key.Down => 0x7D,
            Key.Up => 0x7E,
            Key.OemTilde => 0x32,
            Key.OemMinus => 0x1B,
            Key.OemPlus => 0x18,
            _ => null,
        };
    }
}
