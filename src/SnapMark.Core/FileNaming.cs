namespace SnapMark.Core;

public static class FileNaming
{
    /// <summary>Токены: {date} → 2026-10-31, {time} → 19-45-03, {n} → счётчик.</summary>
    public static string Build(string template, DateTime now, int counter)
    {
        if (string.IsNullOrWhiteSpace(template)) template = "Screenshot_{date}_{time}";
        var name = template
            .Replace("{date}", now.ToString("yyyy-MM-dd"))
            .Replace("{time}", now.ToString("HH-mm-ss"))
            .Replace("{n}", counter.ToString());
        return Sanitize(name);
    }

    public static string Sanitize(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        // Windows-запрещённые символы, если код запущен на Unix.
        foreach (var c in "<>:\"/\\|?*")
            name = name.Replace(c, '_');
        name = name.Trim().TrimEnd('.');
        return name.Length == 0 ? "Screenshot" : name;
    }

    /// <summary>
    /// Меняет название снимка в шаблоне: «Screenshot_{n}_{label}» → «Capture_{n}_{label}».
    /// Трогаем только слово прямо перед «_{» (перед подстановкой), остальной текст шаблона не меняется.
    /// </summary>
    public static string RenameShot(string template, string oldName, string newName)
    {
        if (string.IsNullOrEmpty(template) || string.IsNullOrEmpty(oldName) || oldName == newName) return template;
        return System.Text.RegularExpressions.Regex.Replace(template,
            @"(?<![\p{L}\p{N}_])" + System.Text.RegularExpressions.Regex.Escape(oldName) + @"(?=_\{)", newName.Replace("$", "$$"));
    }

    /// <summary>Если файл существует — добавляет _2, _3…</summary>
    public static string EnsureUnique(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path) ?? "";
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (int i = 2; ; i++)
        {
            var candidate = Path.Combine(dir, $"{stem}_{i}{ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }
}
