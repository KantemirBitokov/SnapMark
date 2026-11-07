using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SnapMark.Core;

namespace SnapMark.Platform.Unix;

// ─────────────────────────────────────────────────────────────────────────────
// Предварительные реализации для macOS и Linux. Каркас уже запускается там из трея,
// но для полноценного порта нужно доделать пункты, помеченные TODO.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Снимок экрана на macOS и Linux.
///   • macOS — системная утилита screencapture, весь дисплей в родном (Retina) разрешении.
///     Нужно разрешение «Запись экрана», иначе вместо окон будут только обои.
///   • Linux/X11 — напрямую через XGetImage, без внешних программ.
///   • Linux/Wayland — grim (Sway/Hyprland), gnome-screenshot (GNOME) или spectacle (KDE):
///     снимаем весь экран и вырезаем нужную область.
/// </summary>
internal sealed class UnixScreenCapture : IScreenCapture
{
    public Bitmap? Capture(PixelRect r)
    {
        if (OperatingSystem.IsLinux() && Linux.Xlib.IsX11Session)
            return Linux.X11Capture.Capture(r) ?? CaptureWithTools(r);
        return CaptureWithTools(r);
    }

    static Bitmap? CaptureWithTools(PixelRect r)
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"snapmark_{Guid.NewGuid():N}.png");
        try
        {
            bool cropNeeded = false;
            if (OperatingSystem.IsMacOS())
            {
                // Снимаем дисплей целиком (-D: 1 — основной, дальше по порядку). Так не нужно гадать,
                // в точках или пикселях Avalonia отдаёт координаты: снимок всегда в родном разрешении,
                // а редактор сам сопоставляет его с размером окна.
                var screens = App.GetScreens().ToList();
                int index = screens.FindIndex(s => s.Bounds == r);
                if (index < 0) index = Math.Max(0, screens.FindIndex(s => s.Bounds.Intersects(r)));
                Run("screencapture", "-x", "-D", (index + 1).ToString(), tmp);
            }
            else if (Run("grim", "-g", $"{r.X},{r.Y} {r.Width}x{r.Height}", tmp)) { }
            else if (Run("gnome-screenshot", "-f", tmp) || Run("spectacle", "-b", "-n", "-f", "-o", tmp)
                     || Run("import", "-window", "root", tmp))
            {
                cropNeeded = true;
            }

            if (!File.Exists(tmp)) return null;
            Bitmap full;
            using (var fs = File.OpenRead(tmp)) full = new Bitmap(fs);
            if (!cropNeeded) return full;
            using (full) return Crop(full, r);
        }
        catch
        {
            return null;
        }
        finally
        {
            try { File.Delete(tmp); } catch { }
        }
    }

    /// <summary>Вырезать область из снимка всего рабочего стола.</summary>
    static Bitmap? Crop(Bitmap full, PixelRect r)
    {
        var area = r.Intersect(new PixelRect(full.PixelSize));
        if (area.Width <= 0 || area.Height <= 0) return null;
        var result = new WriteableBitmap(area.Size, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var fb = result.Lock();
        full.CopyPixels(area, fb.Address, fb.RowBytes * area.Height, fb.RowBytes);
        return result;
    }

    /// <summary>Запустить утилиту; true — если она есть и создала файл.</summary>
    static bool Run(string file, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            p?.WaitForExit(8000);
            return p is not null && args.Length > 0 && File.Exists(args[^1]);
        }
        catch
        {
            return false; // утилиты нет
        }
    }
}

/// <summary>TODO(mac): Carbon RegisterEventHotKey / CGEventTap. TODO(linux): org.freedesktop.portal.GlobalShortcuts.</summary>
internal sealed class NoGlobalHotkeys : IGlobalHotkeys
{
#pragma warning disable CS0067 // событие пока не вызывается на этих ОС
    public event Action<HotkeyAction>? Triggered;
#pragma warning restore CS0067
    public bool IsSupported => false;
    public bool Suspended { get; set; }
    public string Status => L.T("Глобальные горячие клавиши на этой ОС пока не реализованы — используй меню в трее.");
    public void Apply(AppSettings settings) { }
    public void Dispose() { }
}

/// <summary>Кроссплатформенный буфер Avalonia: пока только текст. TODO: картинки и файлы (NSPasteboard / wl-copy).</summary>
internal sealed class AvaloniaTextClipboard : IRichClipboard
{
    public async Task CopyAsync(ClipboardPayload payload)
    {
        var clipboard = App.ClipboardHost?.Clipboard;
        if (clipboard is null) return;
        var text = payload.Text;
        if (string.IsNullOrEmpty(text) && payload.Files is { Count: > 0 })
            text = string.Join(Environment.NewLine, payload.Files);
        if (!string.IsNullOrEmpty(text)) await clipboard.SetTextAsync(text);
    }
}

internal sealed class UnixPrint : IPrintService
{
    public Task PrintAsync(string imagePath)
    {
        try
        {
            if (OperatingSystem.IsMacOS())
                Process.Start(new ProcessStartInfo("open") { ArgumentList = { "-a", "Preview", imagePath } });
            else
                Process.Start(new ProcessStartInfo("lp") { ArgumentList = { imagePath } });
        }
        catch { }
        return Task.CompletedTask;
    }
}

internal sealed class NoAutostart : IAutostart
{
    public bool IsSupported => false;
    public void Set(bool enabled) { }
}

internal sealed class NoPrintScreenConflict : IPrintScreenConflict
{
    public bool IsSupported => false;
    public bool IsSystemToolBoundToPrintScreen() => false;
    public void OpenSystemSettings() { }
    public bool TryDisableSystemBinding() => false;
    public void ApplyWhileRunning(bool usesPrtSc) { }
    public void RestoreOnExit() { }
}

internal class NoWindowHelper : IWindowHelper
{
    public void ForceForeground(Window window) => window.Activate();
    public void ExcludeFromCapture(TopLevel window) { }
    public PixelPoint? GetCursorPosition() => null;
    public void SetPixelBounds(Window window, PixelRect bounds) => window.Position = bounds.Position;
    public virtual bool SetClickThrough(Window window, bool enabled) => false;
    public virtual void BringToFront(Window window)
    {
        window.Hide();
        window.Show();
    }
}

/// <summary>TODO(mac): CGEventTap для Cmd+V. TODO(linux): зависит от окружения, пока без умной вставки.</summary>
internal sealed class NoPasteSupport : IPasteSupport
{
#pragma warning disable CS0067
    public event Action? PasteIntercepted;
#pragma warning restore CS0067
    public bool IsSupported => false;
    public bool Intercept { get; set; }
    public void SendPaste() { }
    public string ForegroundProcessName() => "";
    public uint ClipboardSequence() => 0;
}
