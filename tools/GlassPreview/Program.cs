using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Layout;
using Avalonia.Logging;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using SnapMark.UI;

// Ошибки отрисовки Avalonia — в консоль (их и ищем).
Trace.Listeners.Add(new TextWriterTraceListener(Console.Out));
AppBuilder.Configure<PreviewApp>()
    .UseSkia()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .LogToTrace(LogEventLevel.Warning)
    .SetupWithoutStarting();

var outDir = args.Length > 0 ? args[0] : "out";
Directory.CreateDirectory(outDir);
var bgPath = args.Length > 1 ? args[1] : "../../docs/media/markers.png";

foreach (var (name, transparency, frost, variant) in new[]
         {
             ("t50-f0", 0.5, 0.0, GlassVariant.Regular),
             ("t50-f45", 0.5, 0.45, GlassVariant.Regular),
             ("t50-f100", 0.5, 1.0, GlassVariant.Regular),
             ("t90-f45", 0.9, 0.45, GlassVariant.Regular),
             ("t10-f45", 0.1, 0.45, GlassVariant.Regular),
             ("clear-f20", 0.5, 0.2, GlassVariant.Clear),
         })
{
    Glass.Transparency = transparency;
    Glass.Frost = frost;
    Glass.Variant = variant;
    Dispatcher.UIThread.RunJobs();
    Render(Path.Combine(outDir, name + ".png"));
    Console.WriteLine("rendered " + name);
}

void Render(string path)
{
    using var bg = new Bitmap(bgPath);
    var window = new Window { Width = 1000, Height = 520, SystemDecorations = SystemDecorations.None };
    var root = new Grid();
    root.Children.Add(new Image { Source = bg, Stretch = Stretch.UniformToFill, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top });
    var canvas = new Canvas();
    root.Children.Add(canvas);
    window.Content = root;

    GlassPanel Bar(double x, double y, params Control[] items)
    {
        var stack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        foreach (var i in items) stack.Children.Add(i);
        var p = new GlassPanel { Child = stack };
        p.Classes.Add("pill");
        p.Classes.Add("bar");
        Canvas.SetLeft(p, x);
        Canvas.SetTop(p, y);
        canvas.Children.Add(p);
        return p;
    }
    Button Tool(string icon, bool selected = false)
    {
        var b = UiKit.ToolButton(icon, "", () => { }, "seg");
        if (selected) b.Classes.Add("selected");
        return b;
    }
    Bar(160, 60, Tool("Icon.Select"), Tool("Icon.InkPen", true), Tool("Icon.Pencil"), Tool("Icon.Marker"), Tool("Icon.Arrow"), Tool("Icon.Text"), Tool("Icon.Undo"));
    var panel = new GlassPanel
    {
        Width = 330,
        Child = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = "STYLE PANEL", Classes = { "group" } },
                new TextBlock { Text = "Text on glass must stay readable", FontSize = 14 },
                UiKit.SoftButton("Soft button", () => { }),
                UiKit.SoftButton("Primary", () => { }, primary: true),
            },
        },
        Padding = new Thickness(16, 12),
    };
    panel.Classes.Add("pill");
    Canvas.SetLeft(panel, 560);
    Canvas.SetTop(panel, 160);
    canvas.Children.Add(panel);

    window.Show();
    Dispatcher.UIThread.RunJobs();
    // Фон окна для стекла — сам скриншот, растянутый на окно (как в редакторе).
    var frame0 = window.CaptureRenderedFrame();
    Glass.SetStaticBackdrop(window, frame0!);
    for (int i = 0; i < 5; i++)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
    }
    var frame = window.CaptureRenderedFrame();
    frame?.Save(path);
    window.Close();
}

class PreviewApp : Application
{
    public override void Initialize()
    {
        RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
        Styles.Add(new FluentTheme());
        Styles.Add(new StyleInclude(new Uri("avares://SnapMark/")) { Source = new Uri("avares://SnapMark/Styles/MacStyles.axaml") });
        Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://SnapMark/")) { Source = new Uri("avares://SnapMark/Styles/Colors.axaml") });
        Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://SnapMark/")) { Source = new Uri("avares://SnapMark/Styles/Icons.axaml") });
    }
}
