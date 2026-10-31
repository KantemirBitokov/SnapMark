using SnapMark.Platform.Unix;
using SnapMark.Platform.Windows;

namespace SnapMark.Platform;

/// <summary>Точка выбора реализаций под текущую ОС.</summary>
public static class PlatformServices
{
    public static IScreenCapture ScreenCapture { get; }
    public static IGlobalHotkeys Hotkeys { get; }
    public static IRichClipboard Clipboard { get; }
    public static IPrintService Print { get; }
    public static IAutostart Autostart { get; }
    public static IPrintScreenConflict PrintScreenConflict { get; }
    public static IWindowHelper WindowHelper { get; }
    public static IPasteSupport Paste { get; }

    static PlatformServices()
    {
        if (OperatingSystem.IsWindows())
        {
            ScreenCapture = new WinScreenCapture();
            var hotkeys = new WinHotkeys();
            Hotkeys = hotkeys;
            Paste = hotkeys; // перехват Ctrl+V живёт в том же клавиатурном хуке
            Clipboard = new WinClipboard();
            Print = new WinPrint();
            Autostart = new WinAutostart();
            PrintScreenConflict = new WinPrintScreenConflict();
            WindowHelper = new WinWindowHelper();
        }
        else if (OperatingSystem.IsMacOS())
        {
            ScreenCapture = new UnixScreenCapture();
            Hotkeys = new Mac.MacHotkeys();
            Clipboard = new Mac.MacClipboard();
            Print = new UnixPrint();
            Autostart = new Mac.MacAutostart();
            PrintScreenConflict = new NoPrintScreenConflict();
            WindowHelper = new Mac.MacWindowHelper();
            Paste = new NoPasteSupport(); // на Mac буфер сам хранит картинку и текст вместе — вставка без хитростей
        }
        else if (OperatingSystem.IsLinux())
        {
            ScreenCapture = new UnixScreenCapture();
            Hotkeys = new Linux.X11Hotkeys();
            Clipboard = new Linux.LinuxClipboard();
            Print = new UnixPrint();
            Autostart = new Linux.LinuxAutostart();
            PrintScreenConflict = new NoPrintScreenConflict();
            WindowHelper = new NoWindowHelper();
            Paste = new NoPasteSupport();
        }
        else
        {
            ScreenCapture = new UnixScreenCapture();
            Hotkeys = new NoGlobalHotkeys();
            Clipboard = new AvaloniaTextClipboard();
            Print = new UnixPrint();
            Autostart = new NoAutostart();
            PrintScreenConflict = new NoPrintScreenConflict();
            WindowHelper = new NoWindowHelper();
            Paste = new NoPasteSupport();
        }
    }
}
