using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SkiaSharp;
using SnapMark.Annotations;
using SnapMark.Export;
using SnapMark.Platform;
using SnapMark.Services;

var outDir = args.Length > 0 ? args[0] : "out";
Directory.CreateDirectory(outDir);
int failures = 0;
void Check(string name, bool ok, string detail = "")
{
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {name} {detail}");
    if (!ok) failures++;
}

AppBuilder.Configure<Avalonia.Application>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();

// Avalonia поставила свой контекст синхронизации: продолжения await идут в очередь UI-потока,
// поэтому тесты запускаем внутри цикла событий (как в настоящем приложении).
var exitCode = 1;
var done = new CancellationTokenSource();
Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
{
    try { exitCode = await RunAll(); }
    catch (Exception ex) { Console.WriteLine("FATAL " + ex); }
    finally { done.Cancel(); }
});
Avalonia.Threading.Dispatcher.UIThread.MainLoop(done.Token);
return exitCode;

async Task<int> RunAll()
{

// ── Картинка с известным текстом ──
byte[] Sample()
{
    using var surface = SKSurface.Create(new SKImageInfo(900, 260));
    var c = surface.Canvas;
    c.Clear(SKColors.White);
    using var font = new SKFont(SKTypeface.FromFamilyName("Segoe UI"), 44);
    using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true };
    c.DrawText("Hello SnapMark 2026", 40, 90, font, paint);
    c.DrawText("Password: secret42", 40, 180, font, paint);
    using var img = surface.Snapshot();
    using var data = img.Encode(SKEncodedImageFormat.Png, 100);
    return data.ToArray();
}
var png = Sample();
File.WriteAllBytes(Path.Combine(outDir, "sample.png"), png);

// ── 1. OCR ──
try
{
    var res = await TextRecognizer.RecognizeAsync(png);
    var text = res.Text;
    Console.WriteLine("words: " + string.Join(" ", res.Lines.SelectMany(l => l.Words).Select(w => $"{w.Text}@{w.X:0},{w.Y:0},{w.W:0}x{w.H:0}")));
    Check("ocr-boxes", res.Lines.Count == 2 && res.Lines[0].Words.Count >= 3 && res.Lines[0].Words[0].X is > 20 and < 70 && res.Lines[1].Words[0].Y is > 120 and < 170);
    File.WriteAllText(Path.Combine(outDir, "ocr.txt"), text);
    Check("ocr", text.Contains("SnapMark") && text.Contains("secret42"), "→ " + text.Replace(Environment.NewLine, " | "));
}
catch (Exception ex)
{
    Check("ocr", false, ex.GetType().Name + ": " + ex.Message);
}

// ── 2. Скрыть: размытие и пикселизация в итоговой картинке ──
using (var ms = new MemoryStream(png))
{
    var shot = new Bitmap(ms);
    foreach (var mode in new[] { RedactMode.Blur, RedactMode.Pixelate })
    {
        var r = new RedactAnnotation { Start = new Point(30, 120), End = new Point(600, 210), Mode = mode, Source = shot };
        using var rtb = ImageRenderer.Render(shot, new PixelRect(0, 0, 900, 260), new Annotation[] { r });
        var outPng = ImageEncoder.ToPng(rtb);
        File.WriteAllBytes(Path.Combine(outDir, $"redact-{mode}.png"), outPng);
        // Под областью не должно остаться резкого текста: OCR его не находит, а верхняя строка цела.
        try
        {
            var text = (await TextRecognizer.RecognizeAsync(outPng)).Text;
            Check($"redact-{mode}", text.Contains("SnapMark") && !text.Contains("secret42"), "→ " + text.Replace(Environment.NewLine, " | "));
        }
        catch (Exception ex)
        {
            Check($"redact-{mode}", false, ex.Message);
        }
    }
}

// ── 3. Хук ввода: программные клики и сочетание клавиш ──
if (OperatingSystem.IsWindows())
{
    var mon = InputMonitor.Instance;
    var clicks = new List<InputClick>();
    var combos = new List<string>();
    mon.Click += c => { lock (clicks) clicks.Add(c); };
    mon.KeyCombo += k => { lock (combos) combos.Add(k); };
    using (mon.Acquire())
    {
        Check("hook-installed", mon.Problem is null, mon.Problem ?? "");
        Thread.Sleep(300);
        Win.Click(300, 300);
        Win.Combo(0x11, 0x10, 0x53); // Ctrl+Shift+S
        Win.Key(0x41);               // просто «A» — не показывается
        Win.Key(0x0D);               // Enter — показывается
        Thread.Sleep(800);
    }
    lock (clicks) Check("hook-click", clicks.Any(c => Math.Abs(c.X - 300) < 3 && Math.Abs(c.Y - 300) < 3), string.Join(";", clicks));
    lock (combos)
    {
        Check("hook-combo", combos.Contains("Ctrl + Shift + S"), string.Join(";", combos));
        Check("hook-typing-hidden", !combos.Contains("A") && combos.Contains("Enter"), string.Join(";", combos));
    }
}

// ── 4. Склейка длинного скриншота: страница с закреплёнными шапкой и подвалом ──
{
    const int W = 300, PageH = 2200, H = 400, Head = 40, Foot = 30;
    var rnd = new Random(7);
    using var page = new SKBitmap(W, PageH);
    using (var c = new SKCanvas(page))
    {
        c.Clear(SKColors.White);
        using var font = new SKFont(SKTypeface.FromFamilyName("Segoe UI"), 18);
        using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true };
        for (int y = 30; y < PageH; y += 28)
            if (rnd.NextDouble() > 0.25) c.DrawText($"Line {y / 28}: {Guid.NewGuid():N}"[..Math.Min(34, 12 + rnd.Next(20))], 10, y, font, paint);
    }
    SKBitmap Frame(int off)
    {
        var f = new SKBitmap(W, H);
        using var c = new SKCanvas(f);
        c.DrawBitmap(page, new SKRect(0, off, W, off + H - Head - Foot), new SKRect(0, Head, W, H - Foot));
        using var hp = new SKPaint { Color = new SKColor(20, 90, 200) };
        c.DrawRect(0, 0, W, Head, hp);
        using var fp = new SKPaint { Color = new SKColor(200, 60, 40) };
        c.DrawRect(0, H - Foot, W, Foot, fp);
        return f;
    }
    var st = new SnapMark.Capture.Stitcher(W);
    int body = H - Head - Foot, off = 0;
    while (true)
    {
        using (var f = Frame(off)) st.Add(f);
        if (off >= PageH - body) { using var again = Frame(off); st.Add(again); break; }
        off = Math.Min(off + new[] { 97, 133, 151, 180 }[rnd.Next(4)], PageH - body);
    }
    using var result = st.Result();
    File.WriteAllBytes(Path.Combine(outDir, "stitch.png"), SKImage.FromBitmap(result).Encode(SKEncodedImageFormat.Png, 100).ToArray());
    bool ok = result.Height == Head + PageH + Foot;
    // Сравнить середину результата со страницей построчно (пиксель в пиксель).
    int bad = 0;
    if (ok)
        for (int y = 0; y < PageH; y += 3)
            for (int x = 0; x < W; x += 5)
                if (result.GetPixel(x, Head + y) != page.GetPixel(x, y)) bad++;
    Check("stitch", ok && bad == 0, $"height={result.Height} expected={Head + PageH + Foot} mismatched={bad}");

    // Шапка, которая появляется только после прокрутки (как на GitHub), и шаг почти в половину кадра.
    SKBitmap Sticky(int o)
    {
        var f = new SKBitmap(W, H);
        using var c = new SKCanvas(f);
        c.DrawBitmap(page, new SKRect(0, o, W, o + H), new SKRect(0, 0, W, H));
        if (o > 0)
        {
            using var sp = new SKPaint { Color = new SKColor(30, 30, 30) };
            c.DrawRect(0, 0, W, 64, sp);
            using var tf = new SKFont(SKTypeface.Default, 16);
            using var tp = new SKPaint { Color = SKColors.White, IsAntialias = true };
            c.DrawText("Sticky header", 10, 40, tf, tp);
        }
        return f;
    }
    var st2 = new SnapMark.Capture.Stitcher(W);
    int o2 = 0, skipped = 0;
    while (true)
    {
        using (var f = Sticky(o2)) if (st2.Add(f) < 0) skipped++;
        if (o2 >= PageH - H) break;
        o2 = Math.Min(o2 + H * 45 / 100 + rnd.Next(-20, 20), PageH - H);
    }
    using var r2 = st2.Result();
    int bad2 = 0;
    if (r2.Height == PageH)
        for (int y = 0; y < PageH; y += 3)
            for (int x = 0; x < W; x += 5)
                if (r2.GetPixel(x, y) != page.GetPixel(x, y)) bad2++;
    Check("stitch-sticky", r2.Height == PageH && bad2 == 0 && skipped == 0, $"height={r2.Height} expected={PageH} mismatched={bad2} skipped={skipped}");
}

// ── 5. Обрезка и GIF (нужен ffmpeg в PATH) ──
var video = Environment.GetEnvironmentVariable("SNAPMARK_TEST_VIDEO");
if (video is not null && File.Exists(video))
{
    try
    {
        var (dur, vw, vh) = await SnapMark.Recording.VideoTools.ProbeAsync(video);
        Check("video-probe", Math.Abs(dur - 6) < 0.3 && vw == 640, $"{dur:0.00}s {vw}x{vh}");
        var frame = await SnapMark.Recording.VideoTools.FrameAsync(video, 2, 320, CancellationToken.None);
        Check("video-frame", frame is { Length: > 1000 }, $"{frame?.Length} bytes");
        if (frame is not null) File.WriteAllBytes(Path.Combine(outDir, "video-frame.png"), frame);
        var cut = Path.Combine(outDir, "cut.mp4");
        await SnapMark.Recording.VideoTools.TrimAsync(video, 1, 3.5, cut, CancellationToken.None);
        var (cd, _, _) = await SnapMark.Recording.VideoTools.ProbeAsync(cut);
        Check("video-trim", Math.Abs(cd - 2.5) < 0.25, $"{cd:0.00}s");
        var gif = Path.Combine(outDir, "cut.gif");
        await SnapMark.Recording.VideoTools.GifAsync(video, 1, 2.5, 10, 320, gif, CancellationToken.None);
        Check("video-gif", File.Exists(gif) && new FileInfo(gif).Length > 5000, $"{new FileInfo(gif).Length} bytes");
    }
    catch (Exception ex)
    {
        Check("video", false, ex.Message);
    }
}

// ── Общие механизмы программы (важно для урезанной сборки: всё должно работать как в обычной) ──
try
{
    // Настройки: сохранить изменённые значения и прочитать обратно — JSON должен совпасть до символа.
    var custom = new SnapMark.Core.AppSettings();
    custom.ImageFormat = SnapMark.Core.ImageFileFormat.WebP;
    custom.ImageQuality = 77;
    custom.GlassAppearance = "Dark";
    custom.CaptureRegionHotkey = new SnapMark.Core.Hotkey { Key = "F9", Ctrl = true, Alt = true };
    custom.VideoCameraSize = 240;
    custom.LastRedactStrength = 9;
    var path = SnapMark.Core.SettingsStore.FilePath;
    var backup = File.Exists(path) ? File.ReadAllText(path) : null;
    SnapMark.Core.SettingsStore.Save(custom);
    var written = File.Exists(path) ? File.ReadAllText(path) : "";
    var loaded = SnapMark.Core.SettingsStore.Load();
    SnapMark.Core.SettingsStore.Save(loaded);
    var again = File.ReadAllText(path);
    if (backup is null) File.Delete(path); else File.WriteAllText(path, backup);
    Check("settings-roundtrip", written.Length > 500 && written == again && loaded.ImageFormat == SnapMark.Core.ImageFileFormat.WebP
        && loaded.CaptureRegionHotkey.Key == "F9" && loaded.CaptureRegionHotkey.Alt && loaded.ImageQuality == 77,
        $"{written.Length} chars, format={loaded.ImageFormat}, hotkey={loaded.CaptureRegionHotkey}");
}
catch (Exception ex) { Check("settings-roundtrip", false, ex.GetType().Name + ": " + ex.Message); }

try
{
    var langs = new List<string>();
    foreach (var code in new[] { "en", "de", "fr", "it", "es", "ru" })
    {
        SnapMark.Core.L.Set(code);
        langs.Add($"{code}={SnapMark.Core.L.T("Готово")}/{SnapMark.Core.L.F("Кадров: {0} · {1} px", 3, 1200)}");
    }
    SnapMark.Core.L.Set("en");
    Check("languages", langs.Select(x => x.Split('=')[1]).Distinct().Count() >= 5, string.Join("; ", langs));
}
catch (Exception ex) { Check("languages", false, ex.Message); }

try
{
    byte[] png;
    using (var sb = new SKBitmap(240, 120))
    using (var sc = new SKCanvas(sb))
    {
        sc.Clear(SKColors.White);
        using var sp = new SKPaint { Color = new SKColor(10, 132, 255), IsAntialias = true };
        sc.DrawCircle(60, 60, 40, sp);
        using var im = SKImage.FromBitmap(sb);
        using var enc = im.Encode(SKEncodedImageFormat.Png, 100);
        png = enc.ToArray();
    }
    var sizes = Enum.GetValues<SnapMark.Core.ImageFileFormat>().Select(f => $"{f}:{ImageEncoder.Encode(png, f, 90).Length}").ToList();
    var dib = ImageEncoder.ToDib(png);
    Check("image-formats", sizes.All(x => int.Parse(x.Split(':')[1]) > 100) && dib.Length > 100, string.Join(" ", sizes) + $" dib:{dib.Length}");
    Check("file-naming", SnapMark.Core.FileNaming.Build(new SnapMark.Core.AppSettings().FileNameTemplate, new DateTime(2026, 11, 8, 3, 25, 0), 7).Length > 5,
        SnapMark.Core.FileNaming.Build(new SnapMark.Core.AppSettings().FileNameTemplate, new DateTime(2026, 11, 8, 3, 25, 0), 7));
}
catch (Exception ex) { Check("image-formats", false, ex.GetType().Name + ": " + ex.Message); }

try
{
    // Понятный текст ошибки (в уведомлениях показываем ex.Message), а не код ресурса.
    try { File.ReadAllText(@"Z:\no\such\file.txt"); }
    catch (Exception ex) { Check("error-messages", ex.Message.Contains(' ') && !ex.Message.StartsWith("IO_"), ex.Message); }
}
catch (Exception ex) { Check("error-messages", false, ex.Message); }

try
{
    // Звук компьютера и микрофон: NAudio открывает устройства через COM.
    using var en = new NAudio.CoreAudioApi.MMDeviceEnumerator();
    var outs = en.EnumerateAudioEndPoints(NAudio.CoreAudioApi.DataFlow.All, NAudio.CoreAudioApi.DeviceState.All).Count;
    Check("audio-com", true, $"endpoints={outs}");
}
catch (Exception ex) { Check("audio-com", ex is System.Runtime.InteropServices.COMException, ex.GetType().Name + ": " + ex.Message); }

try
{
    var r = await UpdateChecker.CheckAsync();
    Check("update-check", r.Ok && r.Latest is not null, $"latest={r.Latest} url={r.Url} error={r.Error}");
}
catch (Exception ex) { Check("update-check", false, ex.GetType().Name + ": " + ex.Message); }

try
{
    // Ресурсы сборки: иконки и строки, встроенные в программу.
    var logo = new Bitmap(Avalonia.Platform.AssetLoader.Open(new Uri("avares://SnapMark/Assets/snapmark_1024.png")));
    Check("assets", logo.PixelSize.Width == 1024, $"{logo.PixelSize}");
}
catch (Exception ex) { Check("assets", false, ex.GetType().Name + ": " + ex.Message); }

Console.WriteLine(failures == 0 ? "ALL PASSED" : $"{failures} FAILED");
return failures == 0 ? 0 : 1;
}

static class Win
{
    [StructLayout(LayoutKind.Sequential)] struct INPUT { public uint type; public InputUnion u; }
    [StructLayout(LayoutKind.Explicit)] struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr extra; }
    [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);

    static void Send(params INPUT[] i) => SendInput((uint)i.Length, i, Marshal.SizeOf<INPUT>());
    static INPUT M(uint flags) => new() { type = 0, u = new InputUnion { mi = new MOUSEINPUT { dwFlags = flags } } };
    static INPUT K(ushort vk, bool up) => new() { type = 1, u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = up ? 2u : 0u } } };

    public static void Click(int x, int y)
    {
        SetCursorPos(x, y);
        Thread.Sleep(50);
        Send(M(0x2), M(0x4));
    }

    public static void Key(ushort vk) => Send(K(vk, false), K(vk, true));

    public static void Combo(params ushort[] vks)
    {
        Send(vks.Select(v => K(v, false)).ToArray());
        Send(vks.Reverse().Select(v => K(v, true)).ToArray());
    }
}
