using SnapMark.Core;
using Avalonia;
using Avalonia.Platform;
using SnapMark.Platform;
using SnapMark.Recording;
using SnapMark.Export;
using SnapMark.Services;
using SnapMark.UI;

namespace SnapMark.Capture;

/// <summary>
/// Запуск захвата: сначала снимаем ВСЕ мониторы (пока на экране ещё нет наших окон),
/// затем показываем по оверлею на каждый.
/// </summary>
public static class CaptureController
{
    static readonly List<OverlayWindow> Open = new();

    public static bool IsActive => Open.Count > 0;

    /// <summary>Два нажатия не дальше этого — «двойное» (как двойной щелчок в Windows по умолчанию).</summary>
    static readonly TimeSpan DoublePressWindow = TimeSpan.FromMilliseconds(500);
    static DateTime _lastRegionPress = DateTime.MinValue;

    public static void Start(CaptureMode mode)
    {
        var now = DateTime.UtcNow;
        if (mode == CaptureMode.Region)
        {
            bool quick = IsActive && now - _lastRegionPress <= DoublePressWindow
                         && AppServices.Settings.DoublePress != DoublePressAction.Off
                         && Open.All(w => w.IsUntouched);
            _lastRegionPress = quick ? DateTime.MinValue : now; // третье нажатие начнёт новую пару
            if (quick)
            {
                _ = QuickFullScreenAsync();
                return;
            }
        }

        if (IsActive)
        {
            Open.FirstOrDefault()?.FocusSurface();
            return;
        }
        if (mode == CaptureMode.VideoRegion && RecordingController.IsRecording)
        {
            _ = RecordingController.StopAsync();
            return;
        }

        var screens = App.GetScreens();
        if (screens.Count == 0)
        {
            Toast.Show(L.T("Скриншот"), L.T("Не удалось получить список мониторов."));
            return;
        }

        var cursor = PlatformServices.WindowHelper.GetCursorPosition();
        var underCursor = cursor is PixelPoint p ? screens.FirstOrDefault(s => s.Bounds.Contains(p)) : null;
        underCursor ??= screens.FirstOrDefault(s => s.IsPrimary) ?? screens[0];

        // «Весь экран» — только монитор под курсором, сразу с выделением на весь экран.
        var targets = mode == CaptureMode.FullScreen ? new List<Screen> { underCursor } : screens.ToList();

        if (OperatingSystem.IsMacOS() && !Platform.Mac.MacScreenPermission.IsGranted())
        {
            Platform.Mac.MacScreenPermission.Request();
            Toast.Show(L.T("Нужно разрешение «Запись экрана»"),
                L.T("Системные настройки → Конфиденциальность и безопасность → Запись экрана: включи SnapMark и перезапусти его."),
                L.T("Открыть настройки"), Platform.Mac.MacScreenPermission.OpenSettings);
        }

        var captured = new List<(Screen screen, Avalonia.Media.Imaging.Bitmap bmp)>();
        foreach (var screen in targets)
        {
            var bmp = PlatformServices.ScreenCapture.Capture(screen.Bounds);
            if (bmp is not null) captured.Add((screen, bmp));
        }

        if (captured.Count == 0)
        {
            Toast.Show(L.T("Скриншот не удался"), L.T("Не удалось получить изображение экрана."));
            return;
        }

        OverlayWindow? focusTarget = null;
        foreach (var (screen, bmp) in captured)
        {
            var w = new OverlayWindow(screen, bmp, mode);
            w.Closed += (_, _) => Open.Remove(w);
            Open.Add(w);
            w.Show();
            if (ReferenceEquals(screen, underCursor)) focusTarget = w;
        }

        var target = focusTarget ?? Open[0];
        target.FocusSurface();
        // Первое окно после запуска программы Windows иногда не делает активным сразу (окно ещё только
        // создаётся) — тогда редактор не получает клавиши, и Esc не закрывает его. Проверяем ещё пару раз.
        foreach (var ms in new[] { 120, 350, 800 })
            Avalonia.Threading.DispatcherTimer.RunOnce(() =>
            {
                if (target.IsVisible && !target.IsActive && Open.Contains(target)) target.FocusSurface();
            }, TimeSpan.FromMilliseconds(ms));
    }

    /// <summary>
    /// Двойное нажатие: весь экран (монитор под курсором) — в буфер обмена и/или в папку, без редактора.
    /// Снимок берём из только что открытого оверлея: он сделан до появления наших окон.
    /// </summary>
    static async Task QuickFullScreenAsync()
    {
        var s = AppServices.Settings;
        var cursor = PlatformServices.WindowHelper.GetCursorPosition();
        var overlay = (cursor is PixelPoint p ? Open.FirstOrDefault(w => w.TargetScreen?.Bounds.Contains(p) == true) : null)
                      ?? Open.FirstOrDefault(w => w.TargetScreen?.IsPrimary == true) ?? Open.FirstOrDefault();
        byte[]? png = null;
        try { png = overlay?.FullShotPng(); }
        catch (Exception ex) { App.LogError(ex); }
        CloseAll();
        if (png is null)
        {
            Toast.Show(L.T("Скриншот не удался"), L.T("Не удалось получить изображение экрана."));
            return;
        }

        bool copy = s.DoublePress is DoublePressAction.Copy or DoublePressAction.CopyAndSave;
        bool save = s.DoublePress is DoublePressAction.Save or DoublePressAction.CopyAndSave;
        string? path = null;
        string? error = null;
        if (save)
        {
            try { path = OverlayWindow.SaveToFolder(png, s.DoublePressFolderOrDefault()); }
            catch (Exception ex) { error = ex.Message; }
        }
        if (copy)
        {
            try { await SmartPaste.CopyPlainAsync(new ClipboardPayload { Png = png, Dib = ImageEncoder.ToDib(png) }); }
            catch (Exception ex) { error ??= ex.Message; copy = false; }
        }

        if (error is not null && path is null && !copy)
        {
            Toast.Show(L.T("Не удалось сохранить"), error);
            return;
        }
        string title = copy && path is not null ? L.T("Весь экран: скопирован и сохранён")
            : copy ? L.T("Весь экран скопирован")
            : L.T("Весь экран сохранён");
        string body = path is not null ? Path.GetFileName(path) : L.T("Скриншот в буфере обмена.");
        if (error is not null) body += Environment.NewLine + error;
        if (path is not null)
            Toast.Show(title, body, L.T("Показать"), () => ShellOpen.ShowInFolder(path));
        else
            Toast.Show(title, body);
    }

    public static void CloseAll()
    {
        foreach (var w in Open.ToList()) w.Close();
        Open.Clear();
    }
}
