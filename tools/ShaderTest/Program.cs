using SkiaSharp;
using SnapMark.UI;

// Рисует стеклянные панели поверх реального скриншота тем же шейдером, что и SnapMark.
// Режим «probe N»: отдельные мини-шейдеры, чтобы найти, что роняет программный Skia (каждый — в своём процессе).
if (args.Length > 0 && args[0] == "probe")
{
    string[] probes =
    {
        "uniform shader backdrop; half4 main(float2 p) { return sample(backdrop, p); }",
        "uniform float2 size; half4 main(float2 p) { return half4(p.x / size.x, p.y / size.y, 0.5, 1.0); }",
        "uniform float2 size; half4 main(float2 p) { float2 q = abs(p - size * 0.5); float d = length(max(q, 0.0)); return half4(d / 500.0, 0.0, 0.0, 1.0); }",
        "uniform float2 size; half4 main(float2 p) { if (p.x > size.x * 0.5) { return half4(0.0); } return half4(1.0, 0.0, 0.0, 1.0); }",
        "half4 main(float2 p) { return half4(fract(sin(dot(p, float2(12.9898, 78.233))) * 43758.5453)); }",
        "uniform float2 size; half4 main(float2 p) { float a = pow(max(p.x / size.x, 0.0), 3.0); return half4(a, a, a, 1.0); }",
        "uniform shader backdrop; half4 main(float2 p) { half4 c = sample(backdrop, p + float2(5.0, 0.0)); half r = sample(backdrop, p).r; return half4(r, c.g, c.b, 1.0); }",
    };
    int n = int.Parse(args[1]);
    var fx = SKRuntimeEffect.Create(probes[n], out var err);
    Console.WriteLine($"probe {n}: compile {(fx is null ? "FAILED " + err : "OK")}");
    if (fx is null) return;
    using var s0 = SKSurface.Create(new SKImageInfo(200, 100));
    using var bmp = SKImage.FromEncodedData("../../docs/media/markers.png");
    var uu = new SKRuntimeEffectUniforms(fx);
    if (uu.Names.Contains("size")) uu["size"] = new[] { 200f, 100f };
    var cc = new SKRuntimeEffectChildren(fx);
    if (cc.Names.Contains("backdrop")) cc["backdrop"] = bmp.ToShader();
    using var sh0 = fx.ToShader(false, uu, cc);
    using var p0 = new SKPaint { Shader = sh0 };
    s0.Canvas.DrawRect(new SKRect(0, 0, 200, 100), p0);
    s0.Canvas.Flush();
    Console.WriteLine($"probe {n}: draw OK");
    return;
}
var bgPath = args.Length > 0 ? args[0] : "../../docs/media/markers.png";
var outPath = args.Length > 1 ? args[1] : "glass-test.png";
using var bg = SKImage.FromEncodedData(bgPath) ?? throw new Exception("no background");

SKRuntimeEffect? effect = null;
for (int i = 0; i < GlassShaderSource.Variants.Length && effect is null; i++)
{
    effect = SKRuntimeEffect.Create(GlassShaderSource.Variants[i], out var errors);
    Console.WriteLine(effect is null ? $"variant {i}: FAILED: {errors}" : $"variant {i}: OK");
}
if (effect is null) { Console.WriteLine("NO SHADER"); Environment.Exit(1); }

int W = bg.Width, H = bg.Height;
using var surface = SKSurface.Create(new SKImageInfo(W, H, SKColorType.Bgra8888, SKAlphaType.Premul));
var c = surface.Canvas;
c.DrawImage(bg, 0, 0);

(SKImage img, float scale) Frost(double frost)
{
    if (frost < 0.02) return (bg, 1f);
    float scale = frost < 0.25 ? 0.5f : 0.25f;
    int w = (int)(W * scale), h = (int)(H * scale);
    float sigma = (float)(1.5 + frost * 30) * scale;
    using var s = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
    using var p = new SKPaint { FilterQuality = SKFilterQuality.Medium, ImageFilter = SKImageFilter.CreateBlur(sigma, sigma, SKShaderTileMode.Clamp, null) };
    s.Canvas.DrawImage(bg, new SKRect(0, 0, w, h), p);
    return (s.Snapshot(), scale);
}

void Panel(SKRect r, float radius, double frost, double tintA, bool dark, string label)
{
    Console.WriteLine($"[{label}] frost"); Console.Out.Flush();
    var (img, scale) = Frost(frost);
    float minSide = Math.Min(r.Width, r.Height);
    c.Save();
    c.Translate(r.Left, r.Top);
    // тень
    using (var sh = new SKPaint { Color = new SKColor(0, 0, 0, 60), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 16), IsAntialias = true })
        c.DrawRoundRect(new SKRect(0, 10, r.Width, r.Height + 10), radius, radius, sh);
    var local = SKMatrix.CreateScaleTranslation(1 / scale, 1 / scale, -r.Left, -r.Top);
    using var imageShader = img.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, local);
    Console.WriteLine("  uniforms"); Console.Out.Flush();
    var u = new SKRuntimeEffectUniforms(effect!)
    {
        ["size"] = new[] { r.Width, r.Height },
        ["radius"] = Math.Min(radius, minSide / 2),
        ["edge"] = Math.Clamp(minSide * 0.30f, 6f, 26f),
        ["strength"] = Math.Clamp(minSide * 0.16f, 3f, 18f),
        ["grain"] = (float)(frost * 0.035),
        ["spec"] = 0.22f,
    };
    var ch = new SKRuntimeEffectChildren(effect!) { ["backdrop"] = imageShader };
    Console.WriteLine("  toShader"); Console.Out.Flush();
    using var shader = effect!.ToShader(false, u, ch);
    Console.WriteLine("  draw"); Console.Out.Flush();
    using (var p = new SKPaint { Shader = shader, IsAntialias = true, FilterQuality = SKFilterQuality.Medium })
        c.DrawRect(new SKRect(0, 0, r.Width, r.Height), p);
    Console.WriteLine("  overlay"); Console.Out.Flush();
    var rr = new SKRoundRect(new SKRect(0, 0, r.Width, r.Height), radius);
    c.Save();
    c.ClipRoundRect(rr, antialias: true);
    using (var t = new SKPaint { Color = dark ? new SKColor(30, 30, 34, (byte)(tintA * 255)) : new SKColor(246, 246, 250, (byte)(tintA * 255)) })
        c.DrawRect(new SKRect(0, 0, r.Width, r.Height), t);
    using (var sheen = new SKPaint { Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(0, Math.Min(r.Height, 120) * 0.45f),
               new[] { new SKColor(255, 255, 255, (byte)(dark ? 34 : 110)), new SKColor(255, 255, 255, 0) }, SKShaderTileMode.Clamp) })
        c.DrawRect(new SKRect(0, 0, r.Width, r.Height), sheen);
    c.Restore();
    using (var rim = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = 1, IsAntialias = true,
               Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(0, r.Height),
                   new[] { new SKColor(255, 255, 255, (byte)(dark ? 105 : 235)), new SKColor(255, 255, 255, (byte)(dark ? 26 : 120)), new SKColor(255, 255, 255, (byte)(dark ? 46 : 150)) },
                   new[] { 0f, 0.55f, 1f }, SKShaderTileMode.Clamp) })
        c.DrawRoundRect(new SKRect(1, 1, r.Width - 1, r.Height - 1), radius - 1, radius - 1, rim);
    Console.WriteLine("  text"); Console.Out.Flush();
    using (var txt = new SKPaint { Color = dark ? SKColors.White : new SKColor(29, 29, 31), TextSize = 15, IsAntialias = true, Typeface = SKTypeface.Default })
        c.DrawText(label, 18, r.Height / 2 + 5, txt);
    c.Restore();
}

// Тулбары-капсулы: прозрачное, среднее, матовое стекло.
Panel(SKRect.Create(120, 80, 520, 52), 26, 0.0, 0.30, false, "Frost 0% (clear) - refraction at edges");
Panel(SKRect.Create(120, 170, 520, 52), 26, 0.45, 0.45, false, "Frost 45%");
Panel(SKRect.Create(120, 260, 520, 52), 26, 1.0, 0.55, false, "Frost 100% (full frost)");
// Крупная панель (у крупных линзирование сильнее), тёмная.
Panel(SKRect.Create(760, 300, 420, 300), 22, 0.0, 0.25, true, "Large panel, clear, dark");
Panel(SKRect.Create(1260, 300, 420, 300), 22, 0.6, 0.5, true, "Large panel, frost 60%");

using var snap = surface.Snapshot();
using var data = snap.Encode(SKEncodedImageFormat.Png, 95);
File.WriteAllBytes(outPath, data.ToArray());
Console.WriteLine("written " + outPath);
