using SnapMark.Core;
using SnapMark.Series;

namespace SnapMark.Services;

/// <summary>Общие сервисы приложения (простой статический локатор — для каркаса этого достаточно).</summary>
public static class AppServices
{
    static int _shotCounter;

    public static AppSettings Settings { get; private set; } = new();
    public static SeriesService Series { get; } = new();

    public static void Init()
    {
        Settings = SettingsStore.Load();
        L.Set(Settings.Language);
        _shotCounter = 0;
        TempFiles.CleanupOld();
    }

    public static void SaveSettings() => SettingsStore.Save(Settings);

    /// <summary>Значение для токена {n} в имени файла.</summary>
    public static int NextShotNumber() => Interlocked.Increment(ref _shotCounter);
    public static int PeekShotNumber() => _shotCounter + 1;
}

/// <summary>Временные файлы приложения: %TEMP%\SnapMark\...</summary>
public static class TempFiles
{
    public static string Root => Path.Combine(Path.GetTempPath(), "SnapMark");

    /// <summary>Пишет файл с точным именем в отдельную подпапку (чтобы имя совпадало с текстом комментариев).</summary>
    public static string Write(byte[] data, string fileName)
    {
        var dir = Path.Combine(Root, "tmp", DateTime.Now.ToString("HHmmssfff") + "_" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, fileName);
        File.WriteAllBytes(path, data);
        return path;
    }

    /// <summary>Удаляем мусор от прошлых запусков (старше суток).</summary>
    public static void CleanupOld()
    {
        try
        {
            if (!Directory.Exists(Root)) return;
            foreach (var dir in Directory.EnumerateDirectories(Root, "*", SearchOption.AllDirectories).ToList())
            {
                try
                {
                    if (Directory.GetLastWriteTime(dir) < DateTime.Now.AddDays(-1)) Directory.Delete(dir, true);
                }
                catch { }
            }
            foreach (var file in Directory.EnumerateFiles(Root))
            {
                try
                {
                    if (File.GetLastWriteTime(file) < DateTime.Now.AddDays(-1)) File.Delete(file);
                }
                catch { }
            }
        }
        catch { }
    }
}
