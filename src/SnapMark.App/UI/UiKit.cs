using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;

namespace SnapMark.UI;

/// <summary>Мелкие помощники для окон, собранных в коде.</summary>
public static class UiKit
{
    public static Geometry? Geometry(string key) =>
        Application.Current?.FindResource(key) as Geometry;

    /// <summary>Иконка-обводка из Icons.axaml (внутри Button.tool цвет задаёт стиль).</summary>
    public static Avalonia.Controls.Shapes.Path Icon(string key) => new() { Data = Geometry(key) };

    public static Button ToolButton(string iconKey, string tip, Action onClick, params string[] extraClasses)
    {
        var b = new Button { Content = Icon(iconKey) };
        b.Classes.Add("tool");
        foreach (var c in extraClasses) b.Classes.Add(c);
        ToolTip.SetTip(b, tip);
        b.Click += (_, _) => onClick();
        return b;
    }

    public static Button SoftButton(string text, Action onClick, bool primary = false)
    {
        var b = new Button { Content = text };
        b.Classes.Add(primary ? "primary" : "soft");
        b.Click += (_, _) => onClick();
        return b;
    }

    public static Border VSep()
    {
        var b = new Border();
        b.Classes.Add("vsep");
        return b;
    }

    public static TextBlock Caption(string text)
    {
        var t = new TextBlock { Text = text };
        t.Classes.Add("caption");
        return t;
    }

    /// <summary>Окно-«пилюля» без рамки: прозрачный фон, вся отрисовка — внутри Border.pill.</summary>
    public static void MakeFloating(Window w)
    {
        w.SystemDecorations = SystemDecorations.None;
        w.Topmost = true;
        w.ShowInTaskbar = false;
        w.CanResize = false;
        w.SizeToContent = SizeToContent.WidthAndHeight;
        w.Background = Brushes.Transparent;
        w.TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        w.WindowStartupLocation = WindowStartupLocation.Manual;
        w.FontFamily = Annotations.UiFonts.Family;
    }

    public static Ellipse Dot(double size, IBrush fill) => new() { Width = size, Height = size, Fill = fill };
}
