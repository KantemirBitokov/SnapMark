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
            try { await LongShotTest(); }
            catch (Exception ex) { Program.Log.WriteLine("LONGSHOT ERROR " + ex); }
            try { await BrowserLongShotTest(); }
            catch (Exception ex) { Program.Log.WriteLine("BROWSER LONGSHOT ERROR " + ex); }
            try { await TrimTest(); }
            catch (Exception ex) { Program.Log.WriteLine("TRIM ERROR " + ex); }
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
        CloseAll<SnapMark.UI.OcrWindow>();
    }

    /// <summary>
    /// Стекло уведомления («всегда светлое») над тёмным фоном. Само уведомление исключено из захвата экрана,
    /// поэтому снимаем такую же стеклянную карточку в обычном окне.
    /// </summary>
    static async Task ToastTest()
    {
        Glass.Appearance = GlassAppearance.Light;
        Glass.Transparency = 0.65;
        Glass.Frost = 0.6;
        byte[] dark;
        using (var surface = SkiaSharp.SKSurface.Create(new SkiaSharp.SKImageInfo(700, 300)))
        {
            surface.Canvas.Clear(new SkiaSharp.SKColor(0x20, 0x20, 0x24));
            using var p = new SkiaSharp.SKPaint { Color = new SkiaSharp.SKColor(0x3A, 0x3A, 0x44) };
            for (int x = 0; x < 700; x += 60) surface.Canvas.DrawRect(x, 0, 30, 300, p);
            using var img = surface.Snapshot();
            using var data = img.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
            dark = data.ToArray();
        }
        using var bg = new Bitmap(new MemoryStream(dark));
        var window = new Window { Width = 700, Height = 300, Position = new PixelPoint(0, 0), SystemDecorations = SystemDecorations.None };
        var grid = new Grid();
        grid.Children.Add(new Image { Source = bg, Stretch = Stretch.Fill });
        var texts = new StackPanel { Spacing = 1, Children =
        {
            new TextBlock { Text = "Скопировано" }.Style("headline"),
            UiKit.Caption("Скриншот в буфере обмена. Вставь в чат или документ."),
        } };
        var card = new GlassPanel { Width = 300, Padding = new Thickness(10, 8), Margin = new Thickness(40), Child = texts,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top };
        card.Classes.Add("pill");
        grid.Children.Add(card);
        window.Content = grid;
        Glass.SetStaticBackdrop(window, bg);
        window.Show();
        await Task.Delay(1500);
        using (var shot = Grab(0, 0, 700, 300)) shot.Save(Path.Combine(Program.OutDir, "toast-light-on-dark.png"));
        window.Close();
        Glass.Appearance = GlassAppearance.Auto;
        Program.Log.WriteLine("saved toast");
    }

    static void CloseAll<T>() where T : Window
    {
        var life = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
        foreach (var w in life.Windows.OfType<T>().ToList()) w.Close();
    }

    /// <summary>Длинный скриншот настоящего окна с прокруткой: автопрокрутка колесом и склейка.</summary>
    static async Task LongShotTest()
    {
        var list = new StackPanel();
        for (int i = 1; i <= 150; i++)
            list.Children.Add(new Border
            {
                Background = new SolidColorBrush(i % 2 == 0 ? Color.Parse("#F2F2F7") : Colors.White),
                Padding = new Thickness(12, 6),
                Child = new TextBlock { Text = $"Line {i} — the quick brown fox jumps over the lazy dog #{i * 37 % 101}", Foreground = Brushes.Black, FontSize = 15 },
            });
        var header = new Border { Background = new SolidColorBrush(Color.Parse("#0A84FF")), Height = 44,
            Child = new TextBlock { Text = "Sticky header", Foreground = Brushes.White, Margin = new Thickness(12), FontSize = 16 } };
        var dock = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        dock.Children.Add(header);
        dock.Children.Add(new ScrollViewer { Content = list });
        var w = new Window { Width = 520, Height = 420, Position = new PixelPoint(60, 60), SystemDecorations = SystemDecorations.None, Content = dock, Background = Brushes.White, Topmost = true };
        w.Show();
        w.Activate();
        await Task.Delay(800);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        int frames = 0, height = 0;
        using var result = await SnapMark.Capture.LongShotController.CaptureAsync(new PixelRect(60, 60, 520, 420), 1, true, cts.Token, (f, h) => { frames = f; height = h; });
        w.Close();
        if (result is null) { Program.Log.WriteLine("longshot: null"); return; }
        using (var img = SkiaSharp.SKImage.FromBitmap(result))
        using (var data = img.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100))
            File.WriteAllBytes(Path.Combine(Program.OutDir, "longshot.png"), data.ToArray());
        Program.Log.WriteLine($"longshot: frames={frames} size={result.Width}x{result.Height} scrollOk={SnapMark.Capture.AutoScroll.Scroll(310, 270, 0, 1)}");
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern int GetSystemMetrics(int i);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);

    /// <summary>
    /// Длинный скриншот настоящей страницы в браузере (как на GitHub): шапка, прилипающая при прокрутке,
    /// масштаб 100/125/150 %. Строка N закрашена своим цветом — по левому краю результата проверяем,
    /// что все 200 строк есть, по порядку, без пропусков и повторов.
    /// </summary>
    static async Task BrowserLongShotTest()
    {
        CloseAll<OcrWindow>();
        var sb = new System.Text.StringBuilder();
        sb.Append("<!doctype html><html><head><meta charset=utf-8><style>body{margin:0;font:16px Segoe UI,sans-serif}"
            + ".r{height:40px;line-height:40px;padding-left:90px;color:#fff;white-space:nowrap}"
            + "#top{height:120px;background:#00c8c8}#end{height:150px;background:#ff00ff}"
            + "#sticky{position:fixed;top:0;left:0;right:0;height:60px;background:#1e1e1e;color:#fff;line-height:60px;padding-left:20px;display:none}"
            + "</style></head><body><div id=sticky>Sticky header — appears after scrolling, like on GitHub</div><div id=top></div>");
        for (int n = 1; n <= 200; n++)
        {
            var (r, g, b) = RowColor(n);
            sb.Append($"<div class=r style=\"background:rgb({r},{g},{b})\">Row {n} — the quick brown fox jumps over the lazy dog {n * 7919 % 1000}</div>");
        }
        sb.Append("<div id=end></div><script>addEventListener('scroll',()=>sticky.style.display=scrollY>200?'block':'none')</script></body></html>");
        var html = Path.Combine(Path.GetTempPath(), "snapmark-long.html");
        File.WriteAllText(html, sb.ToString());

        int sw = GetSystemMetrics(0), sh = GetSystemMetrics(1);
        Program.Log.WriteLine($"browser-longshot: screen {sw}x{sh}");
        var chrome = @"C:\Program Files\Google\Chrome\Application\chrome.exe";
        var edge = @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe";
        foreach (var (name, exe, dsf) in new[] { ("chrome-100", chrome, 1.0), ("chrome-150", chrome, 1.5), ("edge-125", edge, 1.25), ("edge-150", edge, 1.5) })
        {
            if (!File.Exists(exe)) { Program.Log.WriteLine($"{name}: no browser"); continue; }
            var profile = Path.Combine(Path.GetTempPath(), "snapmark-" + name);
            var psi = new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = false };
            foreach (var a in new[] { "--kiosk", "--no-first-run", "--no-default-browser-check", "--disable-features=Translate,msEdgeWelcomePage",
                         "--user-data-dir=" + profile, "--force-device-scale-factor=" + dsf.ToString(System.Globalization.CultureInfo.InvariantCulture),
                         new Uri(html).AbsoluteUri })
                psi.ArgumentList.Add(a);
            using var proc = System.Diagnostics.Process.Start(psi)!;
            await Task.Delay(1500);
            if (proc.MainWindowHandle == IntPtr.Zero) proc.Refresh();
            foreach (var bp in System.Diagnostics.Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe)))
                if (bp.MainWindowHandle != IntPtr.Zero) { SetForegroundWindow(bp.MainWindowHandle); break; }
            // Ждём, пока страница реально появится (бирюзовая полоса сверху), — первый запуск браузера долгий.
            for (int t = 0; t < 40; t++)
            {
                await Task.Delay(500);
                using var probe = Grab(40, 40, 4, 4);
                using var sk = SnapMark.Capture.Stitcher.ToSk(probe);
                var px = sk.GetPixel(1, 1);
                if (px.Red < 30 && px.Green > 170 && px.Blue > 170) break;
            }
            await Task.Delay(800);
            var rect = new PixelRect(20, 0, Math.Min(760, sw - 80), sh - 40);
            using (var pre = Grab(rect.X, rect.Y, rect.Width, rect.Height)) pre.Save(Path.Combine(Program.OutDir, name + "-start.png"));
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            int frames = 0;
            SnapMark.Capture.LongShotController.Trace = m => Program.Log.WriteLine($"  {name}: {m}");
            var sw2 = System.Diagnostics.Stopwatch.StartNew();
            using var result = await SnapMark.Capture.LongShotController.CaptureAsync(rect, dsf, true, cts.Token, (f, _) => frames = f);
            try { proc.Kill(true); } catch { }
            foreach (var p in System.Diagnostics.Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe))) try { p.Kill(true); } catch { }
            await Task.Delay(1500);
            if (result is null) { Program.Log.WriteLine($"{name}: null"); continue; }
            using (var img = SkiaSharp.SKImage.FromBitmap(result))
            using (var data = img.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100))
                File.WriteAllBytes(Path.Combine(Program.OutDir, name + ".png"), data.ToArray());
            // Разбор левого края: последовательность номеров строк и высота каждой.
            var seq = new List<(int N, int Len)>();
            for (int y = 0; y < result.Height; y++)
            {
                var c = result.GetPixel(12, y);
                int n = Decode(c.Red, c.Green, c.Blue);
                if (seq.Count > 0 && seq[^1].N == n) seq[^1] = (n, seq[^1].Len + 1);
                else seq.Add((n, 1));
            }
            var rows = seq.Where(x => x.N > 0 && x.Len > 4).ToList();
            int expectLen = (int)Math.Round(40 * dsf);
            bool ordered = rows.Count == 200 && rows.Select((x, i) => x.N == i + 1).All(b => b);
            int badLen = rows.Count(x => Math.Abs(x.Len - expectLen) > 2);
            bool end = seq.Any(x => x.N == -2 && x.Len > 50);
            var missing = Enumerable.Range(1, 200).Except(rows.Select(x => x.N)).Take(10);
            var dup = rows.GroupBy(x => x.N).Where(g => g.Count() > 1).Select(g => g.Key).Take(10);
            Program.Log.WriteLine($"{name}: {(ordered && badLen == 0 && end ? "OK" : "FAIL")} frames={frames} size={result.Width}x{result.Height} rows={rows.Count} badLen={badLen} end={end} " +
                $"missing=[{string.Join(",", missing)}] dup=[{string.Join(",", dup)}] time={sw2.Elapsed.TotalSeconds:0.0}s");
        }

        static (int, int, int) RowColor(int n) => (40 + n % 10 * 20, 40 + n / 10 % 10 * 20, 40 + n / 100 * 60);
        static int Decode(int r, int g, int b)
        {
            if (r > 230 && g < 30 && b > 230) return -2; // конец страницы
            int a = (int)Math.Round((r - 40) / 20.0), bb = (int)Math.Round((g - 40) / 20.0), cc = (int)Math.Round((b - 40) / 60.0);
            if (a is < 0 or > 9 || bb is < 0 or > 9 || cc is < 0 or > 2) return 0;
            if (Math.Abs(r - (40 + a * 20)) > 6 || Math.Abs(g - (40 + bb * 20)) > 6 || Math.Abs(b - (40 + cc * 60)) > 6) return 0;
            return cc * 100 + bb * 10 + a;
        }
    }

    /// <summary>Окно обрезки видео (нужен ffmpeg и тестовое видео).</summary>
    static async Task TrimTest()
    {
        var video = Environment.GetEnvironmentVariable("SNAPMARK_TEST_VIDEO");
        if (video is null || !File.Exists(video)) { Program.Log.WriteLine("trim: no video"); return; }
        SnapMark.Recording.TrimWindow.Open(video);
        await Task.Delay(9000);
        var b = new PixelRect(0, 0, 1024, 768);
        using (var shot = Grab(b.X, b.Y, b.Width, b.Height)) shot.Save(Path.Combine(Program.OutDir, "trim-window.png"));
        Program.Log.WriteLine("saved trim");
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
