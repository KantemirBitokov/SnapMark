namespace SnapMark.Core;

public enum ImageFileFormat { Png, Jpg, WebP, Bmp }

public enum VideoFileFormat { Mp4, Mkv, WebM, Gif }

/// <summary>Источник звука для записи видео. System — то, что играет в колонках/наушниках (WASAPI loopback).</summary>
public enum AudioSource { None, Microphone, System, MicrophoneAndSystem }

public enum SeriesClipboardMode
{
    /// <summary>Текст + HTML с картинками + список файлов. Каждое приложение берёт то, что умеет.</summary>
    All,
    /// <summary>Только текст комментариев.</summary>
    TextOnly,
    /// <summary>Файлы картинок + текст (без HTML).</summary>
    FilesAndText,
}

public enum ThemeMode { System, Light, Dark }

/// <summary>Форма метки.</summary>
public enum MarkerShape { Circle, RoundedSquare, Diamond, Pin }

/// <summary>Обозначение меток: A, B, C… или 1, 2, 3…</summary>
public enum MarkerNumbering { Letters, Numbers }

/// <summary>Оформление текста: без всего, подложка, рамка, рамка вместе с подложкой.</summary>
public enum TextFrame { None, Fill, Outline, FillOutline }

public enum ImageSearchMethod
{
    /// <summary>Автоматическая загрузка в Google Lens через локальную страницу (неофициальный способ).</summary>
    LensUpload,
    /// <summary>Копирование картинки в буфер и открытие Google Картинок (вставить Ctrl+V).</summary>
    ClipboardAndOpen,
}

public sealed class AppSettings
{
    // ── Горячие клавиши ──────────────────────────────────────────────
    // На Mac нет клавиши PrtSc — там по умолчанию ⌘⇧1 / ⌘⇧2 / ⌘⇧9 (системные ⌘⇧3/4/5 не трогаем).
    public Hotkey CaptureRegionHotkey { get; set; } = OperatingSystem.IsMacOS()
        ? new() { Key = "D1", Win = true, Shift = true }
        : new() { Key = "PrintScreen" };
    public Hotkey CaptureFullHotkey { get; set; } = OperatingSystem.IsMacOS()
        ? new() { Key = "D2", Win = true, Shift = true }
        : new() { Key = "PrintScreen", Shift = true };
    public Hotkey RecordVideoHotkey { get; set; } = OperatingSystem.IsMacOS()
        ? new() { Key = "D9", Win = true, Shift = true }
        : new() { Key = "PrintScreen", Ctrl = true };

    // ── Основные ─────────────────────────────────────────────────────
    /// <summary>Язык интерфейса: en, ru, de, fr, it, es. По умолчанию — английский.</summary>
    public string Language { get; set; } = "en";
    public ThemeMode Theme { get; set; } = ThemeMode.System;
    public bool LaunchAtStartup { get; set; }
    public bool ShowNotifications { get; set; } = true;
    public bool FirstRunDone { get; set; }
    /// <summary>Раз в сутки проверять, не вышла ли новая версия (только запрос к GitHub, ничего не скачивается).</summary>
    public bool CheckForUpdates { get; set; } = true;
    /// <summary>Куда пользователь перетащил панель серии («x,y» в пикселях экрана; пусто — внизу по центру).</summary>
    public string SeriesHudPosition { get; set; } = "";
    public DateTime LastUpdateCheckUtc { get; set; }

    // ── Скриншоты ────────────────────────────────────────────────────
    public ImageFileFormat ImageFormat { get; set; } = ImageFileFormat.Png;
    public int ImageQuality { get; set; } = 90;
    public string SaveFolder { get; set; } = DefaultFolders.Pictures;
    public string FileNameTemplate { get; set; } = "Screenshot_{date}_{time}";
    public bool AutoSaveCopy { get; set; }
    public ImageSearchMethod ImageSearch { get; set; } = ImageSearchMethod.LensUpload;
    /// <summary>Цвет рамки выделения.</summary>
    public string SelectionColor { get; set; } = "#0A84FF";

    // ── Серия / экспорт текста ───────────────────────────────────────
    public string SeriesLineTemplate { get; set; } = "Screenshot_{n}_{label} — {comment}";
    public bool SeriesIncludeHeaders { get; set; }
    public string SeriesHeaderTemplate { get; set; } = "Screenshot_{n}:";
    public SeriesClipboardMode SeriesClipboardMode { get; set; } = SeriesClipboardMode.All;
    /// <summary>
    /// Умная вставка (Windows): после копирования SnapMark перехватывает Ctrl+V и подстраивает буфер
    /// под приложение — в браузер/мессенджер сначала картинки, затем текст; в Word — картинки вместе с текстом.
    /// </summary>
    public bool SmartPaste { get; set; } = true;

    // ── Видео ────────────────────────────────────────────────────────
    public string FfmpegPath { get; set; } = "";
    public VideoFileFormat VideoFormat { get; set; } = VideoFileFormat.Mp4;
    public int VideoFps { get; set; } = 30;
    public AudioSource Audio { get; set; } = AudioSource.None;
    /// <summary>Id микрофона (Windows: Id устройства WASAPI). "" — устройство по умолчанию.</summary>
    public string MicrophoneDevice { get; set; } = "";
    /// <summary>Id устройства вывода, чей звук записывать. "" — по умолчанию.</summary>
    public string SystemAudioDevice { get; set; } = "";
    public string VideoFolder { get; set; } = DefaultFolders.Videos;
    public bool ShowRecordingFrame { get; set; } = true;

    // ── Последний стиль аннотаций (запоминаем между запусками) ──────
    public string LastColor { get; set; } = "#FF3B30";
    public double LastThickness { get; set; } = 4;
    public double LastFontSize { get; set; } = 28;
    public bool LastBold { get; set; } = true;
    public double LastOpacity { get; set; } = 1.0;

    // ── Текст ────────────────────────────────────────────────────────
    public string TextFont { get; set; } = "Segoe UI";
    public int TextWeight { get; set; } = 700;
    public bool TextItalic { get; set; }
    public TextFrame TextFrame { get; set; } = TextFrame.None;
    public bool TextShadow { get; set; } = true;
    /// <summary>Цвет тени текста ("" — автоматически).</summary>
    public string TextShadowColor { get; set; } = "";
    /// <summary>Цвет подложки текста ("" — автоматически: белая под тёмным текстом, тёмная под светлым).</summary>
    public string TextPlateColor { get; set; } = "";
    /// <summary>Цвет рамки текста ("" — цвет самого текста).</summary>
    public string TextBorderColor { get; set; } = "";

    // ── Метки ────────────────────────────────────────────────────────
    public MarkerShape MarkerShape { get; set; } = MarkerShape.Circle;
    public MarkerNumbering MarkerNumbering { get; set; } = MarkerNumbering.Letters;

    // ── Комментарии прямо на картинке ────────────────────────────────
    public bool CommentsOnImage { get; set; }
    public string CommentFont { get; set; } = "Segoe UI";
    public int CommentWeight { get; set; } = 600;
    public bool CommentItalic { get; set; }
    public double CommentSize { get; set; } = 22;
    public string CommentColor { get; set; } = "#1C1C1E";
    public TextFrame CommentFrame { get; set; } = TextFrame.Fill;
    public bool CommentShadow { get; set; } = true;
    public string CommentShadowColor { get; set; } = "";
    public string CommentPlateColor { get; set; } = "";
    /// <summary>"" — цвет метки.</summary>
    public string CommentBorderColor { get; set; } = "";
    public string LastShape { get; set; } = "Arrow";
}

public static class DefaultFolders
{
    public static string Pictures => Combine(Environment.SpecialFolder.MyPictures);
    public static string Videos => Combine(Environment.SpecialFolder.MyVideos);

    static string Combine(Environment.SpecialFolder folder)
    {
        var root = Environment.GetFolderPath(folder);
        if (string.IsNullOrEmpty(root))
            root = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(root, "SnapMark");
    }
}
