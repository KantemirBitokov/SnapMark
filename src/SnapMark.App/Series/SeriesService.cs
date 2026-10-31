using System.Text;
using SnapMark.Core;
using SnapMark.Export;
using SnapMark.Platform;
using SnapMark.Services;

namespace SnapMark.Series;

/// <summary>
/// Серия скриншотов: копим кадры с метками и комментариями, потом одной командой
/// копируем всё — картинки отдельно, комментарии текстом (Screenshot_1_A — …).
/// Файлы лежат во временной папке под именами Screenshot_1.png, Screenshot_2.png…
/// чтобы номера в тексте совпадали с файлами.
/// </summary>
public sealed class SeriesService
{
    readonly List<SeriesShot> _shots = new();
    readonly string _dir = Path.Combine(TempFiles.Root, "series_" + Environment.ProcessId);

    public IReadOnlyList<SeriesShot> Shots => _shots;
    public event Action? Changed;

    public SeriesShot Add(byte[] png, int width, int height, List<SeriesMarker> markers)
    {
        Directory.CreateDirectory(_dir);
        int n = _shots.Count + 1;
        var path = Path.Combine(_dir, $"Screenshot_{n}.png");
        File.WriteAllBytes(path, png);

        var shot = new SeriesShot { Number = n, ImagePath = path, Width = width, Height = height, Markers = markers };
        _shots.Add(shot);
        Changed?.Invoke();
        return shot;
    }

    public void Remove(SeriesShot shot)
    {
        if (!_shots.Remove(shot)) return;
        TryDelete(shot.ImagePath);
        Renumber();
        Changed?.Invoke();
    }

    public void Clear()
    {
        foreach (var s in _shots) TryDelete(s.ImagePath);
        _shots.Clear();
        Changed?.Invoke();
    }

    /// <summary>После удаления сдвигаем номера и переименовываем файлы, чтобы не было «дыр».</summary>
    void Renumber()
    {
        for (int i = 0; i < _shots.Count; i++)
        {
            var shot = _shots[i];
            shot.Number = i + 1;
            var desired = Path.Combine(_dir, $"Screenshot_{shot.Number}.png");
            if (!string.Equals(shot.ImagePath, desired, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    File.Move(shot.ImagePath, desired, true);
                    shot.ImagePath = desired;
                }
                catch { }
            }
        }
    }

    public void NotifyCommentsChanged() => Changed?.Invoke();

    // ── Экспорт ───────────────────────────────────────────────────────
    public static string BuildText(IReadOnlyList<SeriesShot> shots)
    {
        var s = AppServices.Settings;
        return SeriesTextFormatter.Format(shots, s.SeriesLineTemplate, s.SeriesIncludeHeaders, s.SeriesHeaderTemplate);
    }

    /// <summary>RTF для Word/Outlook/WordPad: заголовок, картинка, строки комментариев — всё вместе.</summary>
    public static string BuildRtf(IReadOnlyList<SeriesShot> shots)
    {
        var s = AppServices.Settings;
        var rtf = new RtfBuilder();
        foreach (var shot in shots)
        {
            rtf.Heading(Path.GetFileNameWithoutExtension(shot.ImagePath));
            try
            {
                rtf.Picture(File.ReadAllBytes(shot.ImagePath), shot.Width, shot.Height);
            }
            catch
            {
                // файл пропал — просто без картинки
            }
            foreach (var m in shot.Markers)
                rtf.Paragraph(SeriesTextFormatter.FormatLine(s.SeriesLineTemplate, shot.Number, m.Label, m.Comment,
                    Path.GetFileName(shot.ImagePath)));
            rtf.Spacer();
        }
        return rtf.Build();
    }

    /// <summary>
    /// Все варианты содержимого буфера для умной вставки.
    /// <paramref name="singlePng"/> — если это одиночный скриншот: тогда кладём и саму картинку (PNG/DIB).
    /// </summary>
    public static ClipboardBundle BuildBundle(IReadOnlyList<SeriesShot> shots, byte[]? singlePng = null)
    {
        var mode = AppServices.Settings.SeriesClipboardMode;
        var text = BuildText(shots);
        var files = shots.Select(x => x.ImagePath).ToList();
        var rtf = BuildRtf(shots);
        byte[]? dib = singlePng is null ? null : ImageEncoder.ToDib(singlePng);

        var full = new ClipboardPayload { Text = text };
        if (mode is SeriesClipboardMode.All or SeriesClipboardMode.FilesAndText) full.Files = files;
        if (mode == SeriesClipboardMode.All)
        {
            full.Rtf = rtf;
            full.Png = singlePng;
            full.Dib = dib;
        }

        return new ClipboardBundle
        {
            Full = full,
            Images = singlePng is not null
                ? new ClipboardPayload { Png = singlePng, Dib = dib, Files = files }
                : new ClipboardPayload { Files = files },
            Text = new ClipboardPayload { Text = text },
            Rich = new ClipboardPayload { Rtf = rtf, Text = text },
            Files = new ClipboardPayload { Files = files },
        };
    }

    public async Task CopyAllAsync()
    {
        if (_shots.Count == 0) return;
        await SmartPaste.CopyAsync(BuildBundle(_shots));
    }

    /// <summary>Сохранить всю серию в папку в выбранном формате + comments.txt.</summary>
    public string SaveAllTo(string folder)
    {
        var s = AppServices.Settings;
        var target = Path.Combine(folder, FileNaming.Sanitize($"Series_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}"));
        Directory.CreateDirectory(target);
        foreach (var shot in _shots)
        {
            var png = File.ReadAllBytes(shot.ImagePath);
            var name = $"Screenshot_{shot.Number}.{ImageEncoder.Extension(s.ImageFormat)}";
            File.WriteAllBytes(Path.Combine(target, name), ImageEncoder.Encode(png, s.ImageFormat, s.ImageQuality));
        }
        File.WriteAllText(Path.Combine(target, "comments.txt"), BuildText(_shots), new UTF8Encoding(true));
        return target;
    }

    static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }
}
