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
            try { await AppLongShotTest(); }
            catch (Exception ex) { Program.Log.WriteLine("APP LONGSHOT ERROR " + ex); }
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
    /// Длинный скриншот настоящих страниц в Chrome, Edge и Firefox при масштабе 100–150 %. Строка N закрашена
    /// своим цветом — по колонке результата проверяем, что все 200 строк есть, по порядку, без пропусков
    /// и повторов, а вставки между ними (карусель, звёздное небо) — своей высоты.
    ///   base    — шапка, прилипающая при прокрутке (GitHub);
    ///   anim    — + карусель с полосой прогресса, мерцающие звёзды на canvas с крупным заголовком (yellowimages.com),
    ///             плавающая кнопка чата и закреплённая полоса снизу;
    ///   sidebar — неподвижное меню слева (документация, почта).
    /// </summary>
    static async Task BrowserLongShotTest()
    {
        CloseAll<OcrWindow>();
        int sw = GetSystemMetrics(0), sh = GetSystemMetrics(1);
        Program.Log.WriteLine($"browser-longshot: screen {sw}x{sh}");
        var chrome = @"C:\Program Files\Google\Chrome\Application\chrome.exe";
        var edge = @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe";
        var firefox = @"C:\Program Files\Mozilla Firefox\firefox.exe";
        var cases = new (string Page, string Browser, string Exe, double Dsf)[]
        {
            ("base", "chrome", chrome, 1.0), ("base", "chrome", chrome, 1.5), ("base", "edge", edge, 1.5),
            ("base", "firefox", firefox, 1.0), ("base", "firefox", firefox, 1.5),
            ("anim", "chrome", chrome, 1.5), ("anim", "edge", edge, 1.25), ("anim", "firefox", firefox, 1.25),
            ("sidebar", "chrome", chrome, 1.25), ("sidebar", "firefox", firefox, 1.0),
        };
        int ok = 0, total = 0;
        foreach (var (pageKind, browser, exe, dsf) in cases)
        {
            var name = $"{pageKind}-{browser}-{(int)(dsf * 100)}";
            if (!File.Exists(exe)) { Program.Log.WriteLine($"{name}: no browser"); continue; }
            total++;
            var html = Path.Combine(Path.GetTempPath(), $"snapmark-{pageKind}.html");
            File.WriteAllText(html, TestPage(pageKind));
            var profile = Path.Combine(Path.GetTempPath(), "snapmark-" + name);
            Directory.CreateDirectory(profile);
            var psi = new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = false };
            var url = new Uri(html).AbsoluteUri;
            var dsfText = dsf.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (browser == "firefox")
            {
                File.WriteAllText(Path.Combine(profile, "user.js"), string.Join("\n", new[]
                {
                    $"user_pref(\"layout.css.devPixelsPerPx\", \"{dsfText}\");",
                    "user_pref(\"browser.shell.checkDefaultBrowser\", false);",
                    "user_pref(\"browser.aboutwelcome.enabled\", false);",
                    "user_pref(\"datareporting.policy.dataSubmissionPolicyBypassNotification\", true);",
                    "user_pref(\"toolkit.telemetry.reportingpolicy.firstRun\", false);",
                    "user_pref(\"browser.startup.homepage_override.mstone\", \"ignore\");",
                    "user_pref(\"trailhead.firstrun.didSeeAboutWelcome\", true);",
                    "user_pref(\"browser.startup.upgradeDialog.enabled\", false);",
                    "user_pref(\"browser.shell.skipDefaultBrowserCheckOnFirstRun\", true);",
                    "user_pref(\"browser.messaging-system.whatsNewPanel.enabled\", false);",
                    "user_pref(\"messaging-system.rsexperimentloader.enabled\", false);",
                    "user_pref(\"browser.newtabpage.activity-stream.asrouter.userprefs.cfr.features\", false);",
                    "user_pref(\"datareporting.policy.firstRunURL\", \"\");",
                }));
                foreach (var a in new[] { "-kiosk", "-no-remote", "-new-instance", "-profile", profile, url }) psi.ArgumentList.Add(a);
            }
            else
                foreach (var a in new[] { "--kiosk", "--no-first-run", "--no-default-browser-check", "--disable-features=Translate,msEdgeWelcomePage",
                             "--user-data-dir=" + profile, "--force-device-scale-factor=" + dsfText, url })
                    psi.ArgumentList.Add(a);
            using var proc = System.Diagnostics.Process.Start(psi)!;
            await Task.Delay(1500);
            foreach (var bp in System.Diagnostics.Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe)))
                if (bp.MainWindowHandle != IntPtr.Zero) { SetForegroundWindow(bp.MainWindowHandle); break; }
            // Ждём, пока страница реально появится (бирюзовая полоса сверху), — первый запуск браузера долгий.
            bool ready = false;
            for (int t = 0; t < 50 && !ready; t++)
            {
                await Task.Delay(500);
                using var probe = Grab(600, 30, 4, 4);
                using var sk = SnapMark.Capture.Stitcher.ToSk(probe);
                var px = sk.GetPixel(1, 1);
                ready = px.Red < 30 && px.Green > 170 && px.Blue > 170;
            }
            await Task.Delay(800);
            var rect = new PixelRect(20, 0, Math.Min(760, sw - 80), sh - 40);
            using (var pre = Grab(rect.X, rect.Y, rect.Width, rect.Height)) pre.Save(Path.Combine(Program.OutDir, name + "-start.png"));
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            int frames = 0;
            bool manual = false;
            SnapMark.Capture.LongShotController.Trace = m => Program.Log.WriteLine($"  {name}: {m}");
            var timer = System.Diagnostics.Stopwatch.StartNew();
            using var result = await SnapMark.Capture.LongShotController.CaptureAsync(rect, dsf, true, cts.Token, (f, _) => frames = f, () => { manual = true; cts.CancelAfter(3000); });
            using (var post = Grab(rect.X, rect.Y, rect.Width, rect.Height)) post.Save(Path.Combine(Program.OutDir, name + "-end.png"));
            try { proc.Kill(true); } catch { }
            foreach (var bp in System.Diagnostics.Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe))) try { bp.Kill(true); } catch { }
            await Task.Delay(2000);
            if (result is null) { Program.Log.WriteLine($"{name}: FAIL null"); continue; }
            using (var img = SkiaSharp.SKImage.FromBitmap(result))
            using (var data = img.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100))
                File.WriteAllBytes(Path.Combine(Program.OutDir, name + ".png"), data.ToArray());

            // Разбор колонки результата (левее текста строк): номера строк, их высота и промежутки между ними.
            int sampleX = pageKind == "sidebar" ? (int)Math.Round(250 * dsf) - rect.X : 12;
            var seq = new List<(int N, int Start, int Len)>();
            for (int y = 0; y < result.Height; y++)
            {
                var c = result.GetPixel(sampleX, y);
                int n = Decode(c.Red, c.Green, c.Blue);
                if (seq.Count > 0 && seq[^1].N == n) seq[^1] = (n, seq[^1].Start, seq[^1].Len + 1);
                else seq.Add((n, y, 1));
            }
            var rows = seq.Where(x => x.N > 0 && x.Len > 4).ToList();
            int expectLen = (int)Math.Round(40 * dsf);
            bool ordered = rows.Count == 200 && rows.Select((x, i) => x.N == i + 1).All(b => b);
            int badLen = rows.Count(x => Math.Abs(x.Len - expectLen) > 2);
            bool end = seq.Any(x => x.N == -2 && x.Len > 50);
            var missing = Enumerable.Range(1, 200).Except(rows.Select(x => x.N)).Take(10).ToList();
            var dup = rows.GroupBy(x => x.N).Where(g => g.Count() > 1).Select(g => g.Key).Take(10).ToList();
            // Вставки: после строки 30 — карусель 300 px, после 60 — звёздное небо 1000 px (только на anim).
            var gaps = new List<string>();
            bool gapsOk = true;
            for (int k = 0; k + 1 < rows.Count; k++)
            {
                int gap = rows[k + 1].Start - (rows[k].Start + rows[k].Len);
                int want = pageKind == "anim" && rows[k].N == 30 ? (int)(300 * dsf) : pageKind == "anim" && rows[k].N == 60 ? (int)(1000 * dsf) : 0;
                if (Math.Abs(gap - want) > Math.Max(3, want / 50)) { gapsOk = false; gaps.Add($"{rows[k].N}->{rows[k + 1].N}:{gap}(want {want})"); }
            }
            bool pass = ordered && badLen == 0 && end && gapsOk && !manual;
            if (pass) ok++;
            Program.Log.WriteLine($"{name}: {(pass ? "OK" : "FAIL")} frames={frames} size={result.Width}x{result.Height} rows={rows.Count} badLen={badLen} end={end} manual={manual} ready={ready} " +
                $"missing=[{string.Join(",", missing)}] dup=[{string.Join(",", dup)}] gaps=[{string.Join(" ", gaps.Take(6))}] time={timer.Elapsed.TotalSeconds:0.0}s");
        }
        Program.Log.WriteLine($"browser-longshot: {ok}/{total} OK");
        SnapMark.Capture.LongShotController.Trace = null;

        static (int, int, int) RowColor(int n) => (40 + n % 10 * 20, 40 + n / 10 % 10 * 20, 40 + n / 100 * 60);
        static int Decode(int r, int g, int b)
        {
            if (r > 230 && g < 30 && b > 230) return -2; // конец страницы
            int a = (int)Math.Round((r - 40) / 20.0), bb = (int)Math.Round((g - 40) / 20.0), cc = (int)Math.Round((b - 40) / 60.0);
            if (a is < 0 or > 9 || bb is < 0 or > 9 || cc is < 0 or > 2) return 0;
            if (Math.Abs(r - (40 + a * 20)) > 6 || Math.Abs(g - (40 + bb * 20)) > 6 || Math.Abs(b - (40 + cc * 60)) > 6) return 0;
            return cc * 100 + bb * 10 + a;
        }

        static string TestPage(string kind)
        {
            var sb = new System.Text.StringBuilder();
            bool side = kind == "sidebar", anim = kind == "anim";
            sb.Append("<!doctype html><html><head><meta charset=utf-8><style>body{margin:0;font:16px Segoe UI,sans-serif}"
                + (side ? "main{margin-left:240px}" : "")
                + ".r{height:40px;line-height:40px;padding-left:" + (side ? "90" : "90") + "px;color:#fff;white-space:nowrap}"
                + "#top{height:120px;background:#00c8c8}#end{height:150px;background:#ff00ff}"
                + "#sticky{position:fixed;top:0;left:0;right:0;height:60px;background:#1e1e1e;color:#fff;line-height:60px;padding-left:20px;display:none;z-index:5}"
                + "#side{position:fixed;top:0;bottom:0;left:0;width:220px;background:#e8e8ee;padding:20px;box-sizing:border-box;line-height:34px}"
                + "#car{height:300px;position:relative;overflow:hidden;background:#fac800;color:#000;font-size:40px;text-align:center;line-height:260px}"
                + "#bar{position:absolute;left:30%;right:30%;bottom:20px;height:8px;background:#ddd;border-radius:4px}#bar i{display:block;height:100%;background:#000;animation:p 2s linear infinite}"
                + "@keyframes p{from{width:0}to{width:100%}}"
                + "#stars{height:1000px;position:relative;background:#000;color:#fff;text-align:center}#stars canvas{position:absolute;inset:0}"
                + "#stars h1{position:relative;font-size:84px;margin:0;padding-top:330px;line-height:1.05}#stars b{position:relative;display:inline-block;margin-top:40px;background:#ff3d00;padding:18px 34px;border-radius:30px}"
                + "#chat{position:fixed;right:24px;bottom:84px;width:64px;height:64px;border-radius:32px;background:#0a84ff;z-index:6}"
                + "#cookie{position:fixed;left:0;right:0;bottom:0;height:56px;background:#333;color:#fff;line-height:56px;padding-left:20px;z-index:6}"
                + "</style></head><body>");
            sb.Append("<div id=sticky>Sticky header — appears after scrolling, like on GitHub</div>");
            if (side)
            {
                sb.Append("<div id=side>");
                foreach (var t in new[] { "Getting started", "Installation", "Markers", "Comments", "Series", "Recording", "Scrolling shots", "OCR", "Settings", "FAQ" }) sb.Append($"<div>{t}</div>");
                sb.Append("</div>");
            }
            sb.Append("<main><div id=top></div>");
            for (int n = 1; n <= 200; n++)
            {
                var (r, g, b) = RowColor(n);
                sb.Append($"<div class=r style=\"background:rgb({r},{g},{b})\">Row {n} — the quick brown fox jumps over the lazy dog {n * 7919 % 1000}</div>");
                if (anim && n == 30) sb.Append("<div id=car><span id=slide>Slide 1</span><div id=bar><i></i></div></div>");
                if (anim && n == 60) sb.Append("<div id=stars><canvas id=cv></canvas><h1>Members save 90%<br>off regular price</h1><b>Join and save 90% on every item</b></div>");
            }
            sb.Append("<div id=end></div></main>");
            if (anim) sb.Append("<div id=chat></div><div id=cookie>We use cookies — fixed bar at the bottom</div>");
            sb.Append("<script>addEventListener('scroll',()=>sticky.style.display=scrollY>200?'block':'none');");
            if (anim)
                sb.Append("const cols=['#fac800','#fa6464','#00c8fa'];let k=0;setInterval(()=>{k++;car.style.background=cols[k%3];slide.textContent='Slide '+(k%3+1)},700);"
                    + "const cv=document.getElementById('cv'),cx=cv.getContext('2d');function sz(){cv.width=cv.clientWidth*devicePixelRatio;cv.height=cv.clientHeight*devicePixelRatio}sz();"
                    + "const st=[];for(let i=0;i<900;i++)st.push([Math.random(),Math.random(),Math.random()*6]);"
                    + "function fr(t){cx.fillStyle='#000';cx.fillRect(0,0,cv.width,cv.height);for(const s of st){const a=.35+.65*Math.abs(Math.sin(t/300+s[2]));cx.fillStyle='rgba(255,255,255,'+a+')';cx.fillRect(s[0]*cv.width,s[1]*cv.height,2,2)}requestAnimationFrame(fr)}requestAnimationFrame(fr);");
            sb.Append("</script></body></html>");
            return sb.ToString();
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool GetClientRect(IntPtr h, out RECT r);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr h, ref POINT p);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    struct RECT { public int L, T, R, B; }
    struct POINT { public int X, Y; }

    /// <summary>
    /// Длинный скриншот в обычных программах: Блокнот (текст на 400 строк) — проверка распознаванием текста:
    /// номера строк идут подряд, без повторов, до конца файла.
    /// </summary>
    static async Task AppLongShotTest()
    {
        var file = Path.Combine(Path.GetTempPath(), "snapmark-notepad.txt");
        File.WriteAllLines(file, Enumerable.Range(1, 400).Select(i => $"Line {i:0000}  {new string("abcdefghij"[i % 10], 8 + i % 30)} end"));
        foreach (var (name, exe, args) in new[] { ("notepad", "notepad.exe", file) })
        {
            using var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, '"' + args + '"') { UseShellExecute = true })!;
            IntPtr hwnd = IntPtr.Zero;
            for (int t = 0; t < 40 && hwnd == IntPtr.Zero; t++)
            {
                await Task.Delay(500);
                foreach (var p in System.Diagnostics.Process.GetProcessesByName(name))
                    if (p.MainWindowHandle != IntPtr.Zero) { hwnd = p.MainWindowHandle; break; }
            }
            if (hwnd == IntPtr.Zero) { Program.Log.WriteLine($"app-{name}: no window"); continue; }
            ShowWindow(hwnd, 3); // развернуть
            SetForegroundWindow(hwnd);
            await Task.Delay(1500);
            GetClientRect(hwnd, out var cr);
            var tl = new POINT();
            ClientToScreen(hwnd, ref tl);
            // Без строки состояния снизу и меню сверху (если есть) — только текст.
            var rect = new PixelRect(tl.X + 4, tl.Y + 60, Math.Min(700, cr.R - 40), Math.Max(200, cr.B - 120));
            using (var pre = Grab(rect.X, rect.Y, rect.Width, rect.Height)) pre.Save(Path.Combine(Program.OutDir, $"app-{name}-start.png"));
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            SnapMark.Capture.LongShotController.Trace = m => Program.Log.WriteLine($"  app-{name}: {m}");
            bool manual = false;
            using var result = await SnapMark.Capture.LongShotController.CaptureAsync(rect, 1, true, cts.Token, null, () => { manual = true; cts.CancelAfter(3000); });
            SnapMark.Capture.LongShotController.Trace = null;
            foreach (var p in System.Diagnostics.Process.GetProcessesByName(name)) try { p.Kill(true); } catch { }
            if (result is null) { Program.Log.WriteLine($"app-{name}: FAIL null"); continue; }
            using (var img = SkiaSharp.SKImage.FromBitmap(result))
            using (var data = img.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100))
                File.WriteAllBytes(Path.Combine(Program.OutDir, $"app-{name}.png"), data.ToArray());
            // Распознать кусками (у Windows OCR предел по размеру картинки) и собрать номера строк.
            var numbers = new List<int>();
            for (int y0 = 0; y0 < result.Height; y0 += 1200)
            {
                int hh = Math.Min(1300, result.Height - y0);
                using var part = new SkiaSharp.SKBitmap(result.Width, hh);
                result.ExtractSubset(part, new SkiaSharp.SKRectI(0, y0, result.Width, y0 + hh));
                using var pi = SkiaSharp.SKImage.FromBitmap(part);
                using var pd = pi.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
                var ocr = await SnapMark.Services.TextRecognizer.RecognizeAsync(pd.ToArray());
                if (y0 == 0) Program.Log.WriteLine($"  app-{name} ocr: {ocr.Lines.Count} lines, first: {ocr.Lines.FirstOrDefault()?.Text}");
                // Номер — отдельное слово из 4 цифр у левого края (распознавание может разбить строку на части).
                foreach (var line in ocr.Lines)
                    foreach (var w in line.Words)
                        if (w.X < 160 && (y0 == 0 || w.Y >= 100) && System.Text.RegularExpressions.Regex.IsMatch(w.Text, @"^\d{4}$"))
                            numbers.Add(int.Parse(w.Text));
            }
            var distinct = numbers.Distinct().OrderBy(x => x).ToList();
            int max = distinct.Count > 0 ? distinct[^1] : 0, min = distinct.Count > 0 ? distinct[0] : 0;
            int holes = max - min + 1 - distinct.Count;
            // Повторы внутри результата: одна и та же строка в разных местах картинки.
            int dups = numbers.Count - numbers.Distinct().Count();
            bool pass = max >= 398 && min <= 3 && holes <= 8 && !manual;
            Program.Log.WriteLine($"app-{name}: {(pass ? "OK" : "FAIL")} size={result.Width}x{result.Height} lines {min}..{max} recognized={distinct.Count} holes={holes} dupsInOcr={dups} manual={manual}");
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
