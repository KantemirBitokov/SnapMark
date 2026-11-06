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
            try { await EffectsTest(); }
            catch (Exception ex) { Program.Log.WriteLine("EFFECTS ERROR " + ex); }
            try { await OcrTest(); }
            catch (Exception ex) { Program.Log.WriteLine("OCR ERROR " + ex); }
            try { await ToastTest(); }
            catch (Exception ex) { Program.Log.WriteLine("TOAST ERROR " + ex); }
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
        // Образец шкалы шрифтов (UI/Typography.cs) на стекле.
        var spec = new StackPanel { Spacing = 6 };
        foreach (var (cls, text) in new[]
                 {
                     ("largetitle", "Large Title 26 — SnapMark"), ("title1", "Title 1 22 — Настройки"),
                     ("title2", "Title 2 17 — Screenshot series"), ("title3", "Title 3 15 — Kantemir Bitokov"),
                     ("headline", "Headline 13 — Сочетания клавиш"), ("body", "Body 13 — Копировать картинку в буфер"),
                     ("callout", "Callout 12 — MP4 · 30 fps"), ("caption", "Footnote 12 — описание под настройкой, которое переносится на несколько строк и должно читаться легко"),
                     ("caption1", "Caption 11 — 1280 × 720"), ("group", "GROUP LABEL 11"),
                 })
            spec.Children.Add(new TextBlock { Text = text, Classes = { cls }, MaxWidth = 380, TextWrapping = TextWrapping.Wrap });
        var specPanel = new GlassPanel { Width = 420, Padding = new Thickness(18, 14), Child = spec };
        specPanel.Classes.Add("pill");
        Canvas.SetLeft(specPanel, 40); Canvas.SetTop(specPanel, 120); canvas.Children.Add(specPanel);

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

    /// <summary>Окно эффектов записи: клик и Ctrl+Z должны появиться на экране (и, значит, в видео).</summary>
    static async Task EffectsTest()
    {
        using var bg = new Bitmap(Program.BgPath);
        var back = new Window { Width = 900, Height = 600, Position = new PixelPoint(0, 0), SystemDecorations = SystemDecorations.None,
            Content = new Image { Source = bg, Stretch = Stretch.Fill } };
        back.Show();
        await Task.Delay(500);
        var fx = new SnapMark.Recording.RecordingEffectsWindow(new PixelRect(0, 0, 900, 600), 1, clicks: true, keys: true);
        fx.Show();
        await Task.Delay(800);
        Program.Log.WriteLine($"effects problem={fx.Problem}");
        Input.Click(420, 260);
        await Task.Delay(120);
        Input.Combo(0x11, 0x5A); // Ctrl+Z
        await Task.Delay(200);
        using (var shot = Grab(0, 0, 900, 600)) shot.Save(Path.Combine(Program.OutDir, "effects-1.png"));
        Input.Combo(0x11, 0x5A);
        Input.Combo(0x11, 0x5A);
        await Task.Delay(250);
        using (var shot = Grab(0, 0, 900, 600)) shot.Save(Path.Combine(Program.OutDir, "effects-2.png"));
        fx.Close();
        back.Close();
        Program.Log.WriteLine("saved effects");
    }

    /// <summary>Окно «текст с картинки»: распознать, протянуть мышью по части слов, снять экран.</summary>
    static async Task OcrTest()
    {
        using var surface = SkiaSharp.SKSurface.Create(new SkiaSharp.SKImageInfo(760, 220));
        var c = surface.Canvas;
        c.Clear(SkiaSharp.SKColors.White);
        using (var font = new SkiaSharp.SKFont(SkiaSharp.SKTypeface.FromFamilyName("Segoe UI"), 38))
        using (var paint = new SkiaSharp.SKPaint { Color = SkiaSharp.SKColors.Black, IsAntialias = true })
        {
            c.DrawText("Order number: 4815-1623-42", 30, 80, font, paint);
            c.DrawText("Delivery to Baker Street 221b", 30, 160, font, paint);
        }
        byte[] png;
        using (var img = surface.Snapshot()) using (var data = img.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100)) png = data.ToArray();
        SnapMark.UI.OcrWindow.Open(png, new PixelRect(80, 80, 760, 220));
        await Task.Delay(6000); // распознавание
        // Протянуть от «4815…» до «Baker»: окно сдвинуто на поле тени (14) относительно картинки.
        Input.Drag(80 + 270, 80 + 60, 80 + 300, 80 + 140);
        await Task.Delay(400);
        using (var shot = Grab(0, 0, 900, 420)) shot.Save(Path.Combine(Program.OutDir, "ocr-select.png"));
        Program.Log.WriteLine("saved ocr");
    }

    /// <summary>Уведомление при «всегда светлом» стекле поверх тёмного фона.</summary>
    static async Task ToastTest()
    {
        Glass.Appearance = GlassAppearance.Light;
        Glass.Transparency = 0.65;
        var screen = new Window { Position = new PixelPoint(0, 0), WindowState = WindowState.FullScreen, SystemDecorations = SystemDecorations.None,
            Background = new SolidColorBrush(Color.Parse("#202024")) };
        screen.Show();
        await Task.Delay(700);
        SnapMark.UI.Toast.Show("Скопировано", "Скриншот в буфере обмена. Вставь в чат или документ.");
        await Task.Delay(1500);
        var b = screen.Screens.Primary!.Bounds;
        using (var shot = Grab(b.Width - 420, b.Height - 220, 420, 220)) shot.Save(Path.Combine(Program.OutDir, "toast-light-on-dark.png"));
        screen.Close();
        Glass.Appearance = GlassAppearance.Auto;
        Program.Log.WriteLine("saved toast");
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

static class Input
{
    [StructLayout(LayoutKind.Sequential)] struct INPUT { public uint type; public U u; }
    [StructLayout(LayoutKind.Explicit)] struct U { [FieldOffset(0)] public MI mi; [FieldOffset(0)] public KI ki; }
    [StructLayout(LayoutKind.Sequential)] struct MI { public int dx, dy; public uint data, flags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] struct KI { public ushort vk, scan; public uint flags, time; public IntPtr extra; }
    [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] i, int size);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    static void Send(params INPUT[] i) => SendInput((uint)i.Length, i, Marshal.SizeOf<INPUT>());
    static INPUT M(uint f) => new() { type = 0, u = new U { mi = new MI { flags = f } } };
    static INPUT K(ushort vk, bool up) => new() { type = 1, u = new U { ki = new KI { vk = vk, flags = up ? 2u : 0u } } };
    public static void Click(int x, int y) { SetCursorPos(x, y); Thread.Sleep(40); Send(M(2), M(4)); }
    public static void Drag(int x1, int y1, int x2, int y2)
    {
        SetCursorPos(x1, y1); Thread.Sleep(60); Send(M(2));
        for (int i = 1; i <= 12; i++) { SetCursorPos(x1 + (x2 - x1) * i / 12, y1 + (y2 - y1) * i / 12); Send(M(1)); Thread.Sleep(25); }
        Send(M(4));
    }
    public static void Combo(params ushort[] vks)
    {
        Send(vks.Select(v => K(v, false)).ToArray());
        Send(vks.Reverse().Select(v => K(v, true)).ToArray());
    }
}
