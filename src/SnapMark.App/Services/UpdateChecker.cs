using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using SnapMark.Core;
using SnapMark.UI;

namespace SnapMark.Services;

/// <summary>
/// Проверка новой версии по релизам GitHub (раз в сутки при запуске или вручную в «О программе»).
/// Ничего не скачивает сам — показывает уведомление со ссылкой на страницу релиза.
/// </summary>
public static class UpdateChecker
{
    public const string ReleasesPage = "https://github.com/KantemirBitokov/SnapMark/releases/latest";
    const string LatestApi = "https://api.github.com/repos/KantemirBitokov/SnapMark/releases/latest";

    public static Version CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version is { } v ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : new Version(1, 0, 0);

    public sealed record Result(bool Ok, Version? Latest, string Url, string? Error)
    {
        public bool IsNewer => Ok && Latest is not null && Latest > CurrentVersion;
    }

    /// <summary>Тихая проверка при запуске: не чаще раза в сутки, уведомление — только если есть новая версия.</summary>
    public static async Task CheckOnStartupAsync()
    {
        var s = AppServices.Settings;
        if (!s.CheckForUpdates || DateTime.UtcNow - s.LastUpdateCheckUtc < TimeSpan.FromHours(20)) return;
        var r = await CheckAsync();
        if (!r.Ok) return;
        s.LastUpdateCheckUtc = DateTime.UtcNow;
        AppServices.SaveSettings();
        if (r.IsNewer && r.Latest is not null)
            ShowAvailable(r);
    }

    public static void ShowAvailable(Result r) =>
        Toast.Show(L.F("Доступна версия {0}", r.Latest), L.F("У тебя версия {0}. Новая версия бесплатна, как и раньше.", CurrentVersion),
            L.T("Скачать"), () => ShellOpen.Open(r.Url));

    public static async Task<Result> CheckAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"SnapMark/{CurrentVersion}");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            var json = await http.GetStringAsync(LatestApi);
            using var doc = JsonDocument.Parse(json);
            var tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
            var url = doc.RootElement.TryGetProperty("html_url", out var u) ? u.GetString() ?? ReleasesPage : ReleasesPage;
            return Version.TryParse(tag.TrimStart('v', 'V'), out var latest)
                ? new Result(true, latest, url, null)
                : new Result(false, null, ReleasesPage, tag);
        }
        catch (Exception ex)
        {
            return new Result(false, null, ReleasesPage, ex.Message);
        }
    }
}
