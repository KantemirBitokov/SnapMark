using System.Text;

namespace SnapMark.Export;

/// <summary>
/// RTF с встроенными PNG (\pngblip). Это формат, который Word, WordPad, Outlook и LibreOffice
/// надёжно вставляют как «картинки + текст вперемешку». HTML с file:///-картинками Word не грузит
/// из соображений безопасности — поэтому раньше вставлялся только текст.
/// </summary>
public sealed class RtfBuilder
{
    // Ширина страницы A4/Letter с полями ≈ 6.5 дюйма = 9360 twips.
    const int MaxWidthTwips = 9360;
    readonly StringBuilder _sb = new();

    public RtfBuilder()
    {
        _sb.Append(@"{\rtf1\ansi\ansicpg1252\deff0\uc1");
        _sb.Append(@"{\fonttbl{\f0\fswiss Segoe UI;}{\f1\fmodern Consolas;}}");
        _sb.Append(@"\f0\fs22 ");
    }

    public RtfBuilder Heading(string text)
    {
        _sb.Append(@"{\b\fs24 ").Append(Escape(text)).Append(@"}\par ");
        return this;
    }

    public RtfBuilder Paragraph(string text)
    {
        _sb.Append(Escape(text)).Append(@"\par ");
        return this;
    }

    public RtfBuilder Spacer()
    {
        _sb.Append(@"\par ");
        return this;
    }

    public RtfBuilder Picture(byte[] png, int widthPx, int heightPx)
    {
        // 1 px при 96 DPI = 15 twips. Широкие картинки уменьшаем до ширины страницы.
        double scale = Math.Min(1.0, MaxWidthTwips / (widthPx * 15.0));
        int goalW = (int)Math.Round(widthPx * 15 * scale);
        int goalH = (int)Math.Round(heightPx * 15 * scale);

        _sb.Append(@"{\pict\pngblip");
        _sb.Append(@"\picw").Append(widthPx).Append(@"\pich").Append(heightPx);
        _sb.Append(@"\picwgoal").Append(goalW).Append(@"\pichgoal").Append(goalH);
        _sb.Append('\n');
        var hex = Convert.ToHexString(png);
        for (int i = 0; i < hex.Length; i += 128)
            _sb.Append(hex, i, Math.Min(128, hex.Length - i)).Append('\n');
        _sb.Append(@"}\par ");
        return this;
    }

    public string Build() => _sb + "}";

    /// <summary>Экранирование: служебные символы и всё не-ASCII через \uN?</summary>
    static string Escape(string text)
    {
        var sb = new StringBuilder(text.Length + 16);
        foreach (var c in text)
        {
            switch (c)
            {
                case '\\': sb.Append(@"\\"); break;
                case '{': sb.Append(@"\{"); break;
                case '}': sb.Append(@"\}"); break;
                case '\r': break;
                case '\n': sb.Append(@"\line "); break;
                case '\t': sb.Append(@"\tab "); break;
                default:
                    if (c < 128) sb.Append(c);
                    else sb.Append(@"\u").Append((short)c).Append('?');
                    break;
            }
        }
        return sb.ToString();
    }
}
