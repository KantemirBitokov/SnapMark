using SnapMark.Core;
using System.IO.Compression;
using System.Net.Http;

namespace SnapMark.Platform.Common;

/// <summary>
/// Автоматическая установка ffmpeg (только Windows): скачивает официальную сборку и кладёт
/// ffmpeg.exe в %LocalAppData%\SnapMark\ffmpeg — администраторские права не нужны.
/// Источники по очереди: gyan.dev (рекомендуемые сборки с ffmpeg.org), затем GitHub BtbN.
/// </summary>
public static class FfmpegDownloader
{
    static readonly string[] Sources =
    {
        "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip",
        "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip",
    };

    public static string InstallFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SnapMark", "ffmpeg");

    public static string InstalledExe => Path.Combine(InstallFolder, "ffmpeg.exe");

    public static bool IsSupported => OperatingSystem.IsWindows();

    public static bool IsRunning { get; private set; }

    /// <summary>Прогресс 0…1 и текстовое состояние (вызывается в UI-потоке через Progress&lt;T&gt;).</summary>
    public static async Task<string> DownloadAsync(IProgress<(double Fraction, string Status)> progress, CancellationToken ct = default)
    {
        if (!IsSupported) throw new PlatformNotSupportedException(L.T("Автоустановка ffmpeg пока только для Windows."));
        if (IsRunning) throw new InvalidOperationException(L.T("Загрузка уже идёт."));
        IsRunning = true;
        try
        {
            Directory.CreateDirectory(InstallFolder);
            var zipPath = Path.Combine(InstallFolder, "ffmpeg_download.zip");
            Exception? last = null;

            foreach (var url in Sources)
            {
                try
                {
                    await DownloadFileAsync(url, zipPath, progress, ct);
                    progress.Report((1, L.T("Распаковка…")));
                    await Task.Run(() => ExtractFfmpeg(zipPath), ct);
                    TryDelete(zipPath);
                    progress.Report((1, L.T("Готово")));
                    return InstalledExe;
                }
                catch (OperationCanceledException)
                {
                    TryDelete(zipPath);
                    throw;
                }
                catch (Exception ex)
                {
                    last = ex;
                    TryDelete(zipPath);
                    progress.Report((0, L.T("Источник недоступен, пробую другой…")));
                }
            }
            throw new InvalidOperationException(L.T("Не удалось скачать ffmpeg: ") + last?.Message, last);
        }
        finally
        {
            IsRunning = false;
        }
    }

    static async Task DownloadFileAsync(string url, string target, IProgress<(double, string)> progress, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("SnapMark/1.0");
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        long? total = response.Content.Headers.ContentLength;
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        await using var output = File.Create(target);

        var buffer = new byte[81920];
        long done = 0;
        int read;
        var lastReport = DateTime.MinValue;
        while ((read = await input.ReadAsync(buffer, ct)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
            done += read;
            if ((DateTime.Now - lastReport).TotalMilliseconds > 150)
            {
                lastReport = DateTime.Now;
                double fraction = total is > 0 ? (double)done / total.Value : 0;
                string text = total is > 0
                    ? L.F("Загрузка: {0:0.0} из {1:0.0} МБ", done / 1048576.0, total.Value / 1048576.0)
                    : L.F("Загрузка: {0:0.0} МБ", done / 1048576.0);
                progress.Report((fraction, text));
            }
        }
    }

    /// <summary>В архиве ffmpeg.exe лежит в папке вида ffmpeg-7.1-essentials_build/bin/.</summary>
    static void ExtractFfmpeg(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var entry = zip.Entries.FirstOrDefault(e =>
                        e.FullName.EndsWith("/bin/ffmpeg.exe", StringComparison.OrdinalIgnoreCase))
                    ?? zip.Entries.FirstOrDefault(e => e.Name.Equals("ffmpeg.exe", StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidDataException(L.T("В архиве нет ffmpeg.exe."));
        var tmp = InstalledExe + ".tmp";
        entry.ExtractToFile(tmp, true);
        File.Move(tmp, InstalledExe, true);
    }

    static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
