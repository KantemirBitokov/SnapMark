using System.Text.Json;
using System.Text.Json.Serialization;

namespace SnapMark.Core;

/// <summary>Настройки в JSON: %AppData%\SnapMark\settings.json (Windows), ~/.config/SnapMark (Linux/mac).</summary>
public static class SettingsStore
{
    static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Folder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SnapMark");

    public static string FilePath => Path.Combine(Folder, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options) ?? new AppSettings();
        }
        catch
        {
            // Повреждённый файл — откладываем копию и стартуем с настройками по умолчанию.
            try { File.Copy(FilePath, FilePath + ".bak", true); } catch { }
        }
        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Options));
            File.Move(tmp, FilePath, true);
        }
        catch
        {
            // Настройки не критичны: не роняем приложение из-за файловой ошибки.
        }
    }
}
