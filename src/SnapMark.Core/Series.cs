using System.Text;

namespace SnapMark.Core;

public sealed class SeriesMarker
{
    public string Label { get; set; } = "";
    public string Comment { get; set; } = "";
}

public sealed class SeriesShot
{
    /// <summary>Порядковый номер в серии (1, 2, 3…). Пересчитывается при удалении.</summary>
    public int Number { get; set; }
    public string ImagePath { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public List<SeriesMarker> Markers { get; set; } = new();
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}

/// <summary>
/// Превращает серию в текст вида:
///   Screenshot_1_A — комментарий
///   Screenshot_1_B — комментарий
/// Токены шаблона строки: {n} номер скриншота, {label} метка, {comment} комментарий, {file} имя файла.
/// Токены заголовка: {n}, {file}, {count} число меток.
/// </summary>
public static class SeriesTextFormatter
{
    public static string Format(IReadOnlyList<SeriesShot> shots, string lineTemplate,
        bool includeHeaders, string headerTemplate)
    {
        var sb = new StringBuilder();
        foreach (var shot in shots)
        {
            var file = Path.GetFileName(shot.ImagePath);
            if (includeHeaders)
            {
                if (sb.Length > 0) sb.AppendLine();
                sb.AppendLine(headerTemplate
                    .Replace("{n}", shot.Number.ToString())
                    .Replace("{file}", file)
                    .Replace("{count}", shot.Markers.Count.ToString()));
            }

            foreach (var m in shot.Markers)
                sb.AppendLine(FormatLine(lineTemplate, shot.Number, m.Label, m.Comment, file));
        }
        return sb.ToString().TrimEnd('\r', '\n');
    }

    public static string FormatLine(string template, int n, string label, string comment, string file = "")
    {
        var line = template
            .Replace("{n}", n.ToString())
            .Replace("{label}", label)
            .Replace("{file}", file)
            .Replace("{comment}", comment.Trim());

        // Пустой комментарий: убираем висящий разделитель («Screenshot_1_A —» → «Screenshot_1_A»).
        if (string.IsNullOrWhiteSpace(comment))
            line = line.TrimEnd(' ', '—', '–', '-', ':', '|', '\t');
        return line;
    }
}
