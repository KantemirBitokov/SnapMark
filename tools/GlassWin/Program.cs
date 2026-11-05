using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using SkiaSharp;
using SnapMark.UI;

static class Program
{
    public static string OutDir = "out";
    public static string BgPath = "";
    public static StreamWriter Log = null!;

    [STAThread]
    static int Main(string[] args)
    {
        OutDir = args.Length > 0 ? args[0] : "out";
        BgPath = args.Length > 1 ? args[1] : "markers.png";
        Directory.CreateDirectory(OutDir);
        Log = new StreamWriter(Path.Combine(OutDir, "log.txt")) { AutoFlush = true };
        Trace.Listeners.Add(new TextWriterTraceListener(Log));
        try
        {
            AppBuilder.Configure<WinApp>().UsePlatformDetect().LogToTrace(Avalonia.Logging.LogEventLevel.Warning)
                .StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex) { Log.WriteLine("FATAL " + ex); return 1; }
        return 0;
    }
}

class WinApp : Application
{
    public override void Initialize()
    {
        RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
        Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://SnapMark/")) { Source = new Uri("avares://SnapMark/Styles/Colors.axaml") });
        Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://SnapMark/")) { Source = new Uri("avares://SnapMark/Styles/Icons.axaml") });
        Styles.Add(new FluentTheme());
        Styles.Add(new StyleInclude(new Uri("avares://SnapMark/")) { Source = new Uri("avares://SnapMark/Styles/MacStyles.axaml") });
    }

    public override void OnFrameworkInitializationCompleted()
    {
        var life = (IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!;
        life.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                foreach (var (name, t, f, v) in new[]
                         {
                             ("win-t50-f0", 0.5, 0.0, GlassVariant.Regular),
                             ("win-t50-f45", 0.5, 0.45, GlassVariant.Regular),
                             ("win-t50-f100", 0.5, 1.0, GlassVariant.Regular),
                             ("win-clear-f20", 0.5, 0.2, GlassVariant.Clear),
                         })
                {
                    Glass.Transparency = t; Glass.Frost = f; Glass.Variant = v;
                    await Shot(name);
                }
            }
            catch (Exception ex) { Program.Log.WriteLine("ERROR " + ex); }
            life.Shutdown();
        });
        base.OnFrameworkInitializationCompleted();
    }

    static async Task Shot(string name)
    {
        using var bg = new Bitmap(Program.BgPath);
        var window = new Window
        {
            Width = 1100, Height = 700, Position = new PixelPoint(0, 0),
            SystemDecorations = SystemDecorations.None, Topmost = true,
        };
        var root = new Grid();
        root.Children.Add(new Image { Source = bg, Stretch = Stretch.Fill });
        var canvas = new Canvas();
        root.Children.Add(canvas);
        window.Content = root;

        var bar = new GlassPanel { Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Children = {
            UiKit.ToolButton("Icon.Select", "", () => { }, "seg"), Sel(UiKit.ToolButton("Icon.InkPen", "", () => { }, "seg")),
            UiKit.ToolButton("Icon.Pencil", "", () => { }, "seg"), UiKit.ToolButton("Icon.Text", "", () => { }, "seg") } } };
        bar.Classes.Add("pill"); bar.Classes.Add("bar");
        Canvas.SetLeft(bar, 120); Canvas.SetTop(bar, 40); canvas.Children.Add(bar);
        var panel = new GlassPanel { Width = 330, Padding = new Thickness(16, 12), Child = new StackPanel { Spacing = 8, Children = {
            new TextBlock { Text = "STYLE PANEL", Classes = { "group" } },
            new TextBlock { Text = "Text on glass must stay readable", FontSize = 14 },
            UiKit.SoftButton("Soft button", () => { }), UiKit.SoftButton("Primary", () => { }, primary: true) } } };
        panel.Classes.Add("pill");
        Canvas.SetLeft(panel, 520); Canvas.SetTop(panel, 160); canvas.Children.Add(panel);

        // Фон стекла — как в оверлее: картинка, растянутая на всё окно.
        Glass.SetStaticBackdrop(window, bg);
        window.Show();
        await Task.Delay(1800);
        var px = window.RenderScaling;
        Program.Log.WriteLine($"{name}: scaling={px}");
        using var shot = Grab(0, 0, (int)(1100 * px), (int)(700 * px));
        shot.Save(Path.Combine(Program.OutDir, name + ".png"));
        Program.Log.WriteLine("saved " + name);
        window.Close();
        await Task.Delay(300);
    }

    static Button Sel(Button b) { b.Classes.Add("selected"); return b; }

    // Снимок экрана через GDI — то, что реально вывела видеокарта.
    static unsafe Bitmap Grab(int x, int y, int w, int h)
    {
        IntPtr screen = GetDC(IntPtr.Zero), mem = CreateCompatibleDC(screen), bmp = CreateCompatibleBitmap(screen, w, h);
        var old = SelectObject(mem, bmp);
        BitBlt(mem, 0, 0, w, h, screen, x, y, 0x00CC0020 | 0x40000000);
        var info = new BITMAPINFOHEADER { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
        var pixels = new byte[w * h * 4];
        fixed (byte* p = pixels) GetDIBits(mem, bmp, 0, (uint)h, (IntPtr)p, ref info, 0);
        SelectObject(mem, old); DeleteObject(bmp); DeleteDC(mem); ReleaseDC(IntPtr.Zero, screen);
        for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        fixed (byte* p = pixels)
            return new Bitmap(Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Opaque, (IntPtr)p,
                new PixelSize(w, h), new Vector(96, 96), w * 4);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER { public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount; public uint biCompression, biSizeImage; public int biXPelsPerMeter, biYPelsPerMeter; public uint biClrUsed, biClrImportant; }
    [DllImport("user32")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32")] static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
    [DllImport("gdi32")] static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [DllImport("gdi32")] static extern bool BitBlt(IntPtr d, int x, int y, int w, int h, IntPtr s, int sx, int sy, int rop);
    [DllImport("gdi32")] static extern int GetDIBits(IntPtr dc, IntPtr bmp, uint start, uint lines, IntPtr bits, ref BITMAPINFOHEADER bi, uint usage);
    [DllImport("gdi32")] static extern bool DeleteObject(IntPtr o);
    [DllImport("gdi32")] static extern bool DeleteDC(IntPtr dc);
}
