using SnapMark.Platform;

namespace SnapMark.Services;

/// <summary>
/// Набор вариантов содержимого буфера под разные приложения.
/// </summary>
public sealed class ClipboardBundle
{
    /// <summary>Всё сразу — то, что лежит в буфере «по умолчанию» (Блокнот, прочие программы).</summary>
    public required ClipboardPayload Full { get; init; }
    /// <summary>Шаг 1 для браузеров и мессенджеров: только картинки.</summary>
    public ClipboardPayload? Images { get; init; }
    /// <summary>Шаг 2 для браузеров и мессенджеров: только текст.</summary>
    public ClipboardPayload? Text { get; init; }
    /// <summary>Для Word/Outlook/WordPad: RTF с картинками и текстом.</summary>
    public ClipboardPayload? Rich { get; init; }
    /// <summary>Для Проводника: файлы.</summary>
    public ClipboardPayload? Files { get; init; }
}

/// <summary>
/// Умная вставка — одна Ctrl+V, и в каждое приложение попадает то, что оно умеет принять.
///
/// Почему это нужно: веб-чаты (в т.ч. этот) при вставке берут из буфера ЛИБО файлы, ЛИБО текст;
/// Word не загружает картинки из HTML с локальными путями. Одним содержимым буфера всем не угодить.
///
/// Как работает: после копирования SnapMark через клавиатурный хук перехватывает следующую Ctrl+V,
/// смотрит, какое приложение активно, кладёт в буфер подходящий вариант и сам «нажимает» Ctrl+V:
///   • браузеры и мессенджеры — сначала картинки, через полсекунды текст;
///   • Word, Outlook, WordPad, LibreOffice, OneNote — RTF (картинки и подписи вместе);
///   • Проводник — файлы;
///   • Блокнот, редакторы кода, терминалы — только текст;
///   • остальные — как есть.
/// Если буфер успели перезаписать чем-то другим, SnapMark сразу отключается и ничего не трогает.
/// </summary>
public static class SmartPaste
{
    enum Target { PassThrough, TwoStep, Rich, Files, TextOnly }

    static readonly HashSet<string> TwoStepApps = new(StringComparer.OrdinalIgnoreCase)
    {
        // Браузеры
        "chrome", "msedge", "firefox", "opera", "brave", "vivaldi", "browser", "arc", "chromium", "yandex", "thorium",
        // Мессенджеры и чат-клиенты
        "telegram", "discord", "slack", "whatsapp", "ms-teams", "teams", "viber", "signal", "element",
        "claude", "chatgpt", "olk",
    };

    static readonly HashSet<string> RichApps = new(StringComparer.OrdinalIgnoreCase)
    {
        "winword", "outlook", "wordpad", "write", "soffice", "soffice.bin", "swriter", "onenote", "powerpnt",
    };

    /// <summary>
    /// Текстовые редакторы и терминалы: только текст. Новый Блокнот Windows 11 умеет форматирование
    /// и при наличии RTF пытается вставить его (с картинками) — в итоге не вставлялось ничего.
    /// </summary>
    static readonly HashSet<string> TextOnlyApps = new(StringComparer.OrdinalIgnoreCase)
    {
        "notepad", "notepad++", "code", "code - insiders", "cursor", "sublime_text", "devenv", "rider64", "idea64",
        "pycharm64", "webstorm64", "windowsterminal", "cmd", "powershell", "pwsh", "conhost", "notepads",
        "akelpad", "far", "3dsmax", "unrealeditor",
    };

    static ClipboardBundle? _bundle;
    static uint _sequence;
    static bool _busy;
    static bool _subscribed;

    public static bool IsArmed => _bundle is not null;

    /// <summary>Скопировать вариант «всё сразу» и, если можно, включить перехват следующей вставки.</summary>
    public static async Task CopyAsync(ClipboardBundle bundle)
    {
        await PlatformServices.Clipboard.CopyAsync(bundle.Full);

        var paste = PlatformServices.Paste;
        if (!paste.IsSupported || !AppServices.Settings.SmartPaste)
        {
            Disarm();
            return;
        }

        if (!_subscribed)
        {
            paste.PasteIntercepted += OnPasteIntercepted;
            _subscribed = true;
        }
        _bundle = bundle;
        _sequence = paste.ClipboardSequence();
        paste.Intercept = true;
    }

    /// <summary>Скопировать обычные данные (без умной вставки).</summary>
    public static async Task CopyPlainAsync(ClipboardPayload payload)
    {
        Disarm();
        await PlatformServices.Clipboard.CopyAsync(payload);
    }

    public static void Disarm()
    {
        _bundle = null;
        PlatformServices.Paste.Intercept = false;
    }

    static async void OnPasteIntercepted()
    {
        var paste = PlatformServices.Paste;
        var bundle = _bundle;

        // Буфер перезаписан другим приложением → умная вставка больше не актуальна.
        if (bundle is null || _busy || paste.ClipboardSequence() != _sequence)
        {
            Disarm();
            paste.SendPaste(); // нажатие пользователя мы проглотили — возвращаем его
            return;
        }

        _busy = true;
        paste.Intercept = false; // пока работаем, свои нажатия не перехватываем
        try
        {
            switch (Classify(paste.ForegroundProcessName()))
            {
                case Target.TwoStep:
                    await PasteWith(bundle.Images ?? bundle.Full, 550);
                    if (bundle.Text?.Text is { Length: > 0 })
                        await PasteWith(bundle.Text, 550);
                    break;

                case Target.Rich:
                    await PasteWith(bundle.Rich ?? bundle.Full, 500);
                    break;

                case Target.TextOnly:
                    if (bundle.Text?.Text is { Length: > 0 }) await PasteWith(bundle.Text, 450);
                    else paste.SendPaste();
                    break;

                case Target.Files:
                    await PasteWith(bundle.Files ?? bundle.Full, 500);
                    break;

                default:
                    paste.SendPaste();
                    break;
            }

            // Возвращаем «всё сразу», чтобы можно было вставить ещё раз куда угодно.
            await PlatformServices.Clipboard.CopyAsync(bundle.Full);
            _sequence = paste.ClipboardSequence();
        }
        catch
        {
            Disarm();
            return;
        }
        finally
        {
            _busy = false;
        }

        if (_bundle is not null) paste.Intercept = true;
    }

    /// <summary>Положить вариант в буфер, нажать Ctrl+V и дать приложению время прочитать буфер.</summary>
    static async Task PasteWith(ClipboardPayload payload, int waitMs)
    {
        await PlatformServices.Clipboard.CopyAsync(payload);
        PlatformServices.Paste.SendPaste();
        await Task.Delay(waitMs);
    }

    static Target Classify(string process)
    {
        if (process.Length == 0) return Target.PassThrough;
        if (TextOnlyApps.Contains(process)) return Target.TextOnly;
        if (TwoStepApps.Contains(process)) return Target.TwoStep;
        if (RichApps.Contains(process)) return Target.Rich;
        if (process == "explorer") return Target.Files;
        return Target.PassThrough;
    }
}
