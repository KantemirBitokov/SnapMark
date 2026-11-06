using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace SnapMark.UI;

/// <summary>
/// Шкала шрифтов SnapMark — текстовые стили Apple в плотной «маковской» шкале (основной текст 13 pt),
/// как в macOS и в Apple HIG → Typography:
///
///   Large Title 26 Bold · Title 1 22 Bold · Title 2 17 Semibold · Title 3 15 Semibold
///   Headline 13 Semibold · Body 13 · Callout 12 · Footnote 12 · Caption 11 · Caption 2 10
///
/// Шрифт — системный: на Windows Segoe UI Variable (у него, как у SF, три оптических размера:
/// Small до 11 pt, Text для обычного текста, Display от 20 pt), на macOS — San Francisco,
/// на Linux — Inter / Ubuntu / Cantarell. Межбуквенный интервал — как у SF: крупное плотнее, мелкое свободнее.
///
/// Аналог Dynamic Type: учитывается системная настройка Windows «Размер текста» (Специальные возможности).
/// Размеры лежат в ресурсах Sm.Size.*, стили в MacStyles.axaml берут их через DynamicResource,
/// поэтому смена размера применяется на лету.
/// </summary>
public static class UiType
{
    public const double LargeTitle = 26, Title1 = 22, Title2 = 17, Title3 = 15,
        Headline = 13, Body = 13, Callout = 12, Footnote = 12, Caption = 11, Caption2 = 10;

    /// <summary>Множитель системного размера текста (1 — обычный). Ограничен, чтобы не ломать панели.</summary>
    public static double Scale { get; private set; } = 1;

    static readonly (string Key, double Size)[] Sizes =
    {
        ("Sm.Size.LargeTitle", LargeTitle), ("Sm.Size.Title1", Title1), ("Sm.Size.Title2", Title2),
        ("Sm.Size.Title3", Title3), ("Sm.Size.Headline", Headline), ("Sm.Size.Body", Body),
        ("Sm.Size.Callout", Callout), ("Sm.Size.Footnote", Footnote), ("Sm.Size.Caption", Caption),
        ("Sm.Size.Caption2", Caption2),
        // Межстрочный интервал многострочных описаний (≈ 1.33 от размера, как у SF Text).
        ("Sm.Line.Body", 18), ("Sm.Line.Footnote", 16),
        // Стандартные элементы Fluent (кнопки, поля, списки) — тем же основным размером, а не 14.
        ("ControlContentThemeFontSize", Body), ("ToolTipContentThemeFontSize", Callout),
    };

    /// <summary>Записать размеры (с учётом системного масштаба текста) в ресурсы приложения.</summary>
    public static void Apply(IResourceDictionary resources)
    {
        foreach (var (key, size) in Sizes)
            resources[key] = Math.Round(size * Scale * 2) / 2; // кратно 0,5 — ровнее на экране
    }

    /// <summary>Перечитать системный размер текста; true — изменился (и ресурсы обновлены).</summary>
    public static bool Refresh(IResourceDictionary? resources)
    {
        double scale = 1;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Accessibility");
                if (key?.GetValue("TextScaleFactor") is int percent && percent > 100)
                    scale = Math.Min(percent / 100.0, 1.4);
            }
        }
        catch { }
        if (Math.Abs(scale - Scale) < 0.01) return false;
        Scale = scale;
        if (resources is not null) Apply(resources);
        return true;
    }

    /// <summary>Размер с учётом системного масштаба — для текста, создаваемого в коде.</summary>
    public static double Scaled(double size) => Math.Round(size * Scale * 2) / 2;

    /// <summary>Межбуквенный интервал как у SF для данного размера (в точках).</summary>
    public static double Tracking(double size) => size switch
    {
        >= 26 => -0.5,
        >= 20 => -0.35,
        >= 17 => -0.2,
        >= 15 => -0.1,
        >= 13 => 0,
        >= 12 => 0.05,
        _ => 0.15,
    };

    /// <summary>Задать стиль текста элементу, созданному в коде (тот же, что у классов в MacStyles).</summary>
    public static T Style<T>(this T block, string textStyle) where T : TextBlock
    {
        block.Classes.Add(textStyle);
        return block;
    }
}
