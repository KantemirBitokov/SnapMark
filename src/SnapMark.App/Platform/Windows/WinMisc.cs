using System.Runtime.InteropServices;
using SnapMark.Core;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Microsoft.Win32;
using SnapMark.Services;
using static SnapMark.Platform.Windows.Native;

namespace SnapMark.Platform.Windows;

/// <summary>
/// Печать через системный глагол "print": на Windows для картинок открывается мастер
/// «Печать изображений» — выбор принтера, размера и раскладки. Если глагол не зарегистрирован,
/// открываем файл в просмотрщике и просим нажать Ctrl+P.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WinPrint : IPrintService
{
    public Task PrintAsync(string imagePath)
    {
        try
        {
            Process.Start(new ProcessStartInfo(imagePath) { UseShellExecute = true, Verb = "print" });
        }
        catch (Win32Exception)
        {
            ShellOpen.Open(imagePath);
            UI.Toast.Show(L.T("Печать"), L.T("Изображение открыто в просмотрщике — нажми Ctrl+P."));
        }
        return Task.CompletedTask;
    }
}

[SupportedOSPlatform("windows")]
internal sealed class WinAutostart : IAutostart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "SnapMark";

    public bool IsSupported => true;

    public void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe)) key.SetValue(ValueName, $"\"{exe}\" --tray");
        }
        else
        {
            key.DeleteValue(ValueName, false);
        }
    }
}

/// <summary>
/// Windows 11: «Использовать PrtSc для открытия инструмента создания снимков экрана».
/// Значение HKCU\Control Panel\Keyboard\PrintScreenKeyForSnippingEnabled (1 = включено).
/// На новых сборках при отсутствии значения функция включена по умолчанию.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WinPrintScreenConflict : IPrintScreenConflict
{
    const string KeyPath = @"Control Panel\Keyboard";
    const string ValueName = "PrintScreenKeyForSnippingEnabled";

    public bool IsSupported => Environment.OSVersion.Version.Build >= 22000;

    public bool IsSystemToolBoundToPrintScreen()
    {
        if (!IsSupported) return false;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
            var value = key?.GetValue(ValueName);
            if (value is int i) return i == 1;
            return Environment.OSVersion.Version.Build >= 22621; // 22H2+: включено по умолчанию
        }
        catch { return false; }
    }

    public void OpenSystemSettings() => ShellOpen.Open("ms-settings:easeofaccess-keyboard");

    // Флаг «настройку отключил SnapMark» (по нему же установщик вернёт её при удалении программы).
    const string OwnKey = @"Software\SnapMark", OwnValue = "RestorePrintScreenSnipping";

    static bool Managed()
    {
        try
        {
            using var own = Registry.CurrentUser.OpenSubKey(OwnKey);
            return own?.GetValue(OwnValue) is int i && i == 1;
        }
        catch { return false; }
    }

    static bool Write(int value)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
            if (key.GetValue(ValueName) is int cur && cur == value) return true;
            key.SetValue(ValueName, value, RegistryValueKind.DWord);
            Broadcast();
            return true;
        }
        catch { return false; }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr wParam, string lParam, uint flags, uint timeout, out IntPtr result);

    /// <summary>Сообщаем системе, что настройки клавиатуры поменялись, — как это делают «Параметры» Windows.</summary>
    static void Broadcast()
    {
        foreach (var area in new[] { @"Control Panel\Keyboard", "Keyboard" })
            SendMessageTimeout((IntPtr)0xFFFF /*HWND_BROADCAST*/, 0x001A /*WM_SETTINGCHANGE*/, IntPtr.Zero, area, 0x0002 /*SMTO_ABORTIFHUNG*/, 300, out _);
    }

    public bool TryDisableSystemBinding()
    {
        try
        {
            bool wasEnabled = IsSystemToolBoundToPrintScreen();
            if (!Write(0)) return false;
            // Запоминаем, что это сделал SnapMark: при выходе (и при удалении программы) вернём настройку Windows.
            if (wasEnabled)
            {
                using var own = Registry.CurrentUser.CreateSubKey(OwnKey);
                own.SetValue(OwnValue, 1, RegistryValueKind.DWord);
            }
            return true;
        }
        catch { return false; }
    }

    public void ApplyWhileRunning(bool usesPrtSc)
    {
        if (!IsSupported || !Managed()) return;
        // PrtSc — клавиша SnapMark: пока он запущен, «Ножницы» на неё не реагируют; иначе — клавиша снова у Windows.
        Write(usesPrtSc ? 0 : 1);
    }

    public void RestoreOnExit()
    {
        if (IsSupported && Managed()) Write(1);
    }
}

[SupportedOSPlatform("windows")]
internal sealed class WinWindowHelper : IWindowHelper
{
    static IntPtr Hwnd(TopLevel w) => w.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;

    /// <summary>
    /// Windows не даёт фоновому процессу просто так забрать фокус. Временно «пристёгиваемся»
    /// к потоку активного окна — стандартный обход для оверлеев скриншотеров.
    /// </summary>
    public void ForceForeground(Window window)
    {
        var hwnd = Hwnd(window);
        if (hwnd == IntPtr.Zero) return;

        var fg = GetForegroundWindow();
        uint fgThread = GetWindowThreadProcessId(fg, out _);
        uint current = GetCurrentThreadId();

        if (fgThread != 0 && fgThread != current)
        {
            AttachThreadInput(current, fgThread, true);
            SetForegroundWindow(hwnd);
            BringWindowToTop(hwnd);
            AttachThreadInput(current, fgThread, false);
        }
        else
        {
            SetForegroundWindow(hwnd);
        }
        window.Activate();
    }

    public void ExcludeFromCapture(TopLevel window)
    {
        var hwnd = Hwnd(window);
        if (hwnd != IntPtr.Zero) SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE); // Win10 2004+
    }

    public bool IsExcludedFromCapture(TopLevel window)
    {
        var hwnd = Hwnd(window);
        return hwnd != IntPtr.Zero && GetWindowDisplayAffinity(hwnd, out var a) && a == WDA_EXCLUDEFROMCAPTURE;
    }

    public PixelPoint? GetCursorPosition() =>
        GetCursorPos(out var p) ? new PixelPoint(p.X, p.Y) : null;

    /// <summary>
    /// WS_EX_LAYERED + WS_EX_TRANSPARENT — стандартный способ сделать окно «прозрачным для мыши».
    /// LAYERED оставляем навсегда (с полной непрозрачностью), переключаем только TRANSPARENT.
    /// </summary>
    public bool SetClickThrough(Window window, bool enabled)
    {
        var hwnd = Hwnd(window);
        if (hwnd == IntPtr.Zero) return false;
        long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        long next = enabled ? ex | WS_EX_TRANSPARENT | WS_EX_LAYERED : ex & ~WS_EX_TRANSPARENT;
        if (next == ex) return true;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(next));
        if ((ex & WS_EX_LAYERED) == 0 && (next & WS_EX_LAYERED) != 0)
            SetLayeredWindowAttributes(hwnd, 0, 255, LWA_ALPHA); // иначе только что «слоёное» окно станет невидимым
        return true;
    }

    public void BringToFront(Window window)
    {
        var hwnd = Hwnd(window);
        if (hwnd != IntPtr.Zero) SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    public void SetPixelBounds(Window window, PixelRect b)
    {
        var hwnd = Hwnd(window);
        if (hwnd == IntPtr.Zero) return;
        SetWindowPos(hwnd, HWND_TOPMOST, b.X, b.Y, b.Width, b.Height, SWP_NOACTIVATE);
    }
}
