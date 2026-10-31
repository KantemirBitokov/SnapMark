using System.Globalization;
using System.Reflection;

namespace SnapMark.Core;

/// <summary>
/// Локализация. Ключ — исходная русская строка, переводы лежат в Strings.tsv
/// (колонки: ru, en, de, fr, it, es; «\n» внутри строки — перевод строки).
/// Нет перевода — показывается английский, нет и его — исходная строка.
/// </summary>
public static class L
{
    public static readonly (string Code, string Name)[] Languages =
    {
        ("en", "English"),
        ("ru", "Русский"),
        ("de", "Deutsch"),
        ("fr", "Français"),
        ("it", "Italiano"),
        ("es", "Español"),
    };

    static readonly string[] Columns = { "ru", "en", "de", "fr", "it", "es" };
    static readonly Dictionary<string, string[]> Table = Load();
    static int _column = 1; // английский по умолчанию

    public static string Language => Columns[_column];

    public static void Set(string? code)
    {
        int i = Array.IndexOf(Columns, (code ?? "").Trim().ToLowerInvariant());
        _column = i < 0 ? 1 : i;
        try { CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(Language); } catch { }
    }

    /// <summary>Перевод строки интерфейса.</summary>
    public static string T(string ru)
    {
        if (_column == 0) return ru;
        if (Table.TryGetValue(ru, out var row))
        {
            var s = row[_column];
            if (!string.IsNullOrEmpty(s)) return s;
            if (!string.IsNullOrEmpty(row[1])) return row[1];
        }
        return ru;
    }

    /// <summary>Перевод с подстановкой: L.F("Серия: {0}", n).</summary>
    public static string F(string ru, params object?[] args)
    {
        var format = T(ru);
        try { return string.Format(CultureInfo.CurrentCulture, format, args); }
        catch (FormatException) { return string.Format(CultureInfo.CurrentCulture, ru, args); }
    }

    static Dictionary<string, string[]> Load()
    {
        var table = new Dictionary<string, string[]>(StringComparer.Ordinal);
        try
        {
            var asm = typeof(L).Assembly;
            var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("Strings.tsv", StringComparison.OrdinalIgnoreCase));
            if (name is null) return table;
            using var stream = asm.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (line.Length == 0 || line.StartsWith('#')) continue;
                var parts = line.Split('\t');
                var row = new string[Columns.Length];
                for (int i = 0; i < row.Length; i++)
                    row[i] = i < parts.Length ? Unescape(parts[i]) : "";
                if (row[0].Length > 0) table[row[0]] = row;
            }
        }
        catch { }
        return table;
    }

    static string Unescape(string s) => s.Replace("\\n", "\n").Replace("\\t", "\t");
}
