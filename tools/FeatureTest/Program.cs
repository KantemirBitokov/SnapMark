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
    var text = await TextRecognizer.RecognizeAsync(png);
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
            var text = await TextRecognizer.RecognizeAsync(outPng);
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

Console.WriteLine(failures == 0 ? "ALL PASSED" : $"{failures} FAILED");
return failures == 0 ? 0 : 1;

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
