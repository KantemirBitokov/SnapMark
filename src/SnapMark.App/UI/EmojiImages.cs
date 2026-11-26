using System.Collections.Concurrent;
using System.Net.Http;
using System.Text;
using Avalonia.Media.Imaging;
using SnapMark.Core;
using SnapMark.Services;

namespace SnapMark.UI;

/// <summary>
/// Картинки эмодзи Apple — вид «как в iOS» на любой системе. Файлы скачиваются по одному
/// при первом показе и остаются в кэше, так что второй раз сеть не нужна.
/// Нет сети или файла — возвращаем null, и вызывающий код рисует обычный символ шрифтом.
/// </summary>
public static class EmojiImages
{
    // Набор Apple: PNG 160×160 на каждый эмодзи — хватает и на стикер крупного размера.
    // Сначала GitHub (он у проекта и так основной), потом jsDelivr — если один источник недоступен, берём второй.
    static readonly string[] Sources =
    {
        "https://raw.githubusercontent.com/iamcal/emoji-data/master/img-apple-160/",
        "https://cdn.jsdelivr.net/gh/iamcal/emoji-data@master/img-apple-160/",
    };

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    static readonly ConcurrentDictionary<string, Task<Bitmap?>> Tasks = new(StringComparer.Ordinal);
    static readonly ConcurrentDictionary<string, Bitmap> Memory = new(StringComparer.Ordinal);

    /// <summary>Кэш картинок: %LOCALAPPDATA%\SnapMark\emoji\apple160.</summary>
    public static string Folder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SnapMark", "emoji", "apple160");

    /// <summary>Включён ли набор Apple (настройка «Набор эмодзи»).</summary>
    public static bool Enabled => AppServices.Settings.EmojiSet == EmojiSet.Apple;

    /// <summary>Уже скачанная картинка — чтобы показать сразу, без мигания шрифтом.</summary>
    public static Bitmap? Cached(string emoji)
    {
        if (Memory.TryGetValue(emoji, out var cached)) return cached;

        try
        {
            var path = PathOf(emoji);
            if (!File.Exists(path)) return null;
            var bitmap = new Bitmap(path);
            Memory[emoji] = bitmap;
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Картинка эмодзи; null — если скачать не удалось.</summary>
    public static Task<Bitmap?> Get(string emoji) => Tasks.GetOrAdd(emoji, LoadAsync);

    static string PathOf(string emoji) => Path.Combine(Folder, Key(emoji) + ".png");

    /// <summary>Имя файла в наборе — кодовые точки через дефис, как в emoji-datasource.</summary>
    static string Key(string emoji) =>
        string.Join("-", emoji.EnumerateRunes().Select(r => r.Value.ToString("x")));

    static async Task<Bitmap?> LoadAsync(string emoji)
    {
        try
        {
            var path = PathOf(emoji);
            if (!File.Exists(path))
            {
                byte[]? data = await DownloadAsync(Key(emoji)).ConfigureAwait(false);
                if (data is null) return null;

                Directory.CreateDirectory(Folder);
                var tmp = path + ".tmp";
                await File.WriteAllBytesAsync(tmp, data).ConfigureAwait(false);
                File.Move(tmp, path, overwrite: true);
            }

            var bitmap = new Bitmap(path);
            Memory[emoji] = bitmap;
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    static async Task<byte[]?> DownloadAsync(string key)
    {
        foreach (var source in Sources)
        {
            foreach (var candidate in Candidates(key))
            {
                try
                {
                    using var response = await Http.GetAsync(source + candidate + ".png").ConfigureAwait(false);
                    if (response.IsSuccessStatusCode)
                        return await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                }
                catch
                {
                    // Нет сети — просто попробуем следующий вариант написания или источник.
                }
            }
        }

        return null;
    }

    /// <summary>В наборе у части эмодзи в имени есть fe0f, у части нет — пробуем оба.</summary>
    static IEnumerable<string> Candidates(string key)
    {
        yield return key;
        if (key.Contains("-fe0f"))
            yield return key.Replace("-fe0f", "");
    }
}
