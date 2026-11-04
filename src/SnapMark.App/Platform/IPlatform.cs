using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using SnapMark.Core;

namespace SnapMark.Platform;

// ─────────────────────────────────────────────────────────────────────────────
// Всё, что зависит от ОС, спрятано за этими интерфейсами. Для порта на macOS/Linux
// нужно реализовать их в Platform/Unix (сейчас там упрощённые реализации и заглушки).
// ─────────────────────────────────────────────────────────────────────────────

public enum HotkeyAction { CaptureRegion, CaptureFull, RecordVideo }

/// <summary>Снимок экрана. Координаты — физические пиксели виртуального рабочего стола.</summary>
public interface IScreenCapture
{
    Bitmap? Capture(PixelRect physicalRect);
}

/// <summary>Глобальные горячие клавиши (работают, когда приложение в фоне).</summary>
public interface IGlobalHotkeys : IDisposable
{
    event Action<HotkeyAction>? Triggered;
    bool IsSupported { get; }
    /// <summary>Временно отключить (например, пока пользователь записывает новое сочетание).</summary>
    bool Suspended { get; set; }
    /// <summary>Человекочитаемое состояние: как зарегистрированы клавиши, ошибки.</summary>
    string Status { get; }
    void Apply(AppSettings settings);
}

/// <summary>Данные для буфера обмена. Заполняется то, что есть; платформа кладёт всё сразу.</summary>
public sealed class ClipboardPayload
{
    public string? Text { get; set; }
    /// <summary>HTML-фрагмент (без обёртки CF_HTML — её добавит платформа).</summary>
    public string? Html { get; set; }
    public byte[]? Png { get; set; }
    /// <summary>Готовый DIB (BITMAPINFOHEADER + пиксели), для Windows-приложений, не понимающих PNG.</summary>
    public byte[]? Dib { get; set; }
    public IReadOnlyList<string>? Files { get; set; }
    /// <summary>RTF (только ASCII, Unicode экранирован). Word/WordPad/Outlook вставляют из него картинки вместе с текстом.</summary>
    public string? Rtf { get; set; }
}

public interface IRichClipboard
{
    Task CopyAsync(ClipboardPayload payload);
}

public interface IPrintService
{
    Task PrintAsync(string imagePath);
}

public interface IAutostart
{
    bool IsSupported { get; }
    void Set(bool enabled);
}

/// <summary>Конфликт с системной обработкой PrtSc (Windows 11 → «Ножницы»).</summary>
public interface IPrintScreenConflict
{
    bool IsSupported { get; }
    bool IsSystemToolBoundToPrintScreen();
    void OpenSystemSettings();
    bool TryDisableSystemBinding();
}

/// <summary>Нативные мелочи для окон.</summary>
public interface IWindowHelper
{
    void ForceForeground(Window window);
    /// <summary>Окно не попадает в скриншоты и запись видео.</summary>
    void ExcludeFromCapture(TopLevel window);
    PixelPoint? GetCursorPosition();
    /// <summary>Точные положение и размер окна в физических пикселях (в обход минимального размера).</summary>
    void SetPixelBounds(Window window, PixelRect bounds);
    /// <summary>Окно-оверлей захвата: поверх всего, включая строку меню macOS. По умолчанию ничего не делает.</summary>
    void MakeOverlay(Window window) { }
    /// <summary>
    /// Окно пропускает мышь насквозь — клики и прокрутка уходят приложениям под ним (холст рисования
    /// во время записи в режиме «курсор»). false — окно снова принимает мышь. Возвращает false, если не умеет.
    /// </summary>
    bool SetClickThrough(Window window, bool enabled) => false;
    /// <summary>Поднять окно над остальными окнами «поверх всех», не забирая фокус.</summary>
    void BringToFront(Window window)
    {
        window.Hide();
        window.Show();
    }
}

/// <summary>
/// Поддержка «умной вставки»: перехват Ctrl+V до того, как приложение прочитает буфер,
/// и эмуляция Ctrl+V после подмены содержимого.
/// </summary>
public interface IPasteSupport
{
    bool IsSupported { get; }
    /// <summary>Перехватывать Ctrl+V (нажатие «глотается», вместо него вызывается PasteIntercepted).</summary>
    bool Intercept { get; set; }
    event Action? PasteIntercepted;
    void SendPaste();
    string ForegroundProcessName();
    uint ClipboardSequence();
}
