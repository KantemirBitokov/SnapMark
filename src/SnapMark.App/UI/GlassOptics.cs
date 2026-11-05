using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;

namespace SnapMark.UI;

/// <summary>
/// Оптика Liquid Glass по описанию Apple (WWDC25 «Meet Liquid Glass»): стекло не просто размывает фон —
/// оно «изгибает и концентрирует свет». Шейдер Skia (SkSL):
///   • матовость (frost) — размытие фона нужной силы плюс лёгкое зерно, от прозрачного до полностью матового;
///   • линзирование — у краёв фон «затягивается» из-за границы и изгибается, сильнее у крупных элементов;
///   • дисперсия — у самой кромки каналы чуть расходятся, как у настоящей линзы;
///   • блики — свет сверху-слева: обращённые к нему края светятся ярче.
/// Если шейдер не собрался (старый Skia, программная отрисовка), стекло рисуется по-старому.
/// </summary>
internal static class GlassOptics
{
    // Тело шейдера. SAMPLE(x) подставляется под синтаксис конкретной версии Skia.
    const string Body = @"
uniform shader backdrop;
uniform float2 size;
uniform float radius;
uniform float edge;
uniform float strength;
uniform float grain;
uniform float spec;

float sdf(float2 p) {
    float2 c = size * 0.5;
    float2 q = abs(p - c) - c + radius;
    return min(max(q.x, q.y), 0.0) + length(max(q, 0.0)) - radius;
}

float hash(float2 p) {
    return fract(sin(dot(p, float2(12.9898, 78.233))) * 43758.5453);
}

half4 shade(float2 p) {
    float d = sdf(p);
    if (d > 0.5) { return half4(0.0); }
    float e = 0.75;
    float2 n = float2(sdf(p + float2(e, 0.0)) - sdf(p - float2(e, 0.0)),
                      sdf(p + float2(0.0, e)) - sdf(p - float2(0.0, e)));
    float nl = length(n);
    if (nl > 0.0001) { n = n / nl; } else { n = float2(0.0); }

    // 0 — глубоко внутри, 1 — у самой кромки.
    float t = clamp(1.0 + d / edge, 0.0, 1.0);
    float bend = t * t * strength;
    // Линзирование: у края видно то, что лежит ЗА границей, — фон изгибается вокруг стекла.
    float2 q = p + n * bend;
    half4 c = SAMPLE(q);
    half r = SAMPLE(q + n * bend * 0.14).r;
    half b = SAMPLE(q - n * bend * 0.14).b;
    c = half4(r, c.g, b, 1.0);

    // Блики от источника света сверху-слева (и слабый отражённый снизу-справа).
    float2 L = normalize(float2(-0.55, -0.85));
    float rim = 1.0 - smoothstep(0.0, edge * 0.6, -d);
    float s = pow(max(dot(n, L), 0.0), 3.0) * rim * spec
            + pow(max(dot(n, -L), 0.0), 3.0) * rim * spec * 0.35;
    float g = (hash(floor(p)) - 0.5) * grain;
    c = half4(c.r + s + g, c.g + s + g, c.b + s + g, 1.0);

    // Мягкая кромка (сглаживание).
    float aa = clamp(0.5 - d, 0.0, 1.0);
    return half4(c.rgb * aa, aa);
}
";

    static readonly string[] Variants =
    {
        Body.Replace("SAMPLE(", "backdrop.eval(") + "half4 main(float2 p) { return shade(p); }",
        Body.Replace("SAMPLE(", "sample(backdrop, ") + "half4 main(float2 p) { return shade(p); }",
        Body.Replace("SAMPLE(", "sample(backdrop, ") + "void main(float2 p, inout half4 color) { color = shade(p); }",
    };

    static SKRuntimeEffect? _effect;
    static bool _tried;

    /// <summary>Собранный шейдер или null, если эта версия Skia его не понимает.</summary>
    public static SKRuntimeEffect? Effect
    {
        get
        {
            if (_tried) return _effect;
            _tried = true;
            foreach (var src in Variants)
            {
                try
                {
                    var fx = SKRuntimeEffect.Create(src, out var errors);
                    if (fx is not null)
                    {
                        _effect = fx;
                        break;
                    }
                    _lastError = errors;
                }
                catch (Exception ex)
                {
                    _lastError = ex.Message;
                }
            }
            if (_effect is null && _lastError is not null)
                App.LogError(new InvalidOperationException("Liquid Glass shader: " + _lastError));
            return _effect;
        }
    }

    static string? _lastError;

    /// <summary>Картинка Avalonia → SKImage (копия пикселей).</summary>
    public static SKImage? ToSkImage(Bitmap bitmap)
    {
        int w = bitmap.PixelSize.Width, h = bitmap.PixelSize.Height;
        if (w <= 0 || h <= 0) return null;
        var format = bitmap.Format ?? PixelFormat.Bgra8888;
        var colorType = format == PixelFormat.Rgba8888 ? SKColorType.Rgba8888 : SKColorType.Bgra8888;
        var alpha = bitmap.AlphaFormat switch
        {
            AlphaFormat.Opaque => SKAlphaType.Opaque,
            AlphaFormat.Unpremul => SKAlphaType.Unpremul,
            _ => SKAlphaType.Premul,
        };
        int stride = w * 4;
        var pixels = new byte[stride * h];
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            bitmap.CopyPixels(new PixelRect(0, 0, w, h), handle.AddrOfPinnedObject(), pixels.Length, stride);
            return SKImage.FromPixelCopy(new SKImageInfo(w, h, colorType, alpha), handle.AddrOfPinnedObject(), stride);
        }
        finally
        {
            handle.Free();
        }
    }

    /// <summary>
    /// Матовая версия фона: уменьшаем (дешевле и мягче) и размываем по Гауссу с силой по «матовости» 0…1.
    /// При 0 — фон как есть (прозрачное стекло).
    /// </summary>
    public static (SKImage Image, float Scale) Frost(SKImage sharp, double frost)
    {
        if (frost < 0.02) return (sharp, 1f);
        float scale = frost < 0.25 ? 0.5f : 0.25f;
        int w = Math.Max(2, (int)(sharp.Width * scale)), h = Math.Max(2, (int)(sharp.Height * scale));
        float sigma = (float)(1.5 + frost * 30) * scale; // в пикселях уменьшенной картинки
        using var surface = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
        if (surface is null) return (sharp, 1f);
        using (var paint = new SKPaint
               {
                   FilterQuality = SKFilterQuality.Medium,
                   ImageFilter = SKImageFilter.CreateBlur(sigma, sigma, SKShaderTileMode.Clamp, null),
               })
            surface.Canvas.DrawImage(sharp, new SKRect(0, 0, w, h), paint);
        return (surface.Snapshot(), scale);
    }
}

/// <summary>Фон окна для оптики стекла: резкая картинка и её матовая версия под текущую «матовость».</summary>
internal sealed class SkiaBackdrop
{
    public SkiaBackdrop(SKImage sharp)
    {
        Sharp = sharp;
        Rebuild();
    }

    public SKImage Sharp { get; }
    public SKImage Frosted { get; private set; } = null!;
    /// <summary>Во сколько раз матовая картинка меньше резкой.</summary>
    public float FrostScale { get; private set; } = 1;
    double _builtFor = -1;

    /// <summary>Пересобрать матовую версию, если «матовость» изменилась.</summary>
    public bool Rebuild()
    {
        double frost = Glass.EffectiveFrost;
        if (Math.Abs(frost - _builtFor) < 0.005) return false;
        _builtFor = frost;
        try
        {
            (Frosted, FrostScale) = GlassOptics.Frost(Sharp, frost);
        }
        catch
        {
            (Frosted, FrostScale) = (Sharp, 1f);
        }
        return true;
    }
}

/// <summary>Отрисовка стекла шейдером прямо на холсте Skia (внутри обычного Render панели).</summary>
internal sealed class GlassDrawOperation : ICustomDrawOperation
{
    readonly SkiaBackdrop _backdrop;
    readonly Rect _bounds;
    readonly Point _originPx;   // левый верхний угол панели в пикселях резкой картинки
    readonly double _pxPerDip;  // пикселей резкой картинки в одной точке
    readonly double _radius;

    public GlassDrawOperation(SkiaBackdrop backdrop, Rect bounds, Point originPx, double pxPerDip, double radius)
    {
        _backdrop = backdrop;
        _bounds = bounds;
        _originPx = originPx;
        _pxPerDip = pxPerDip;
        _radius = radius;
    }

    public Rect Bounds => _bounds;
    public bool HitTest(Point p) => false;
    public bool Equals(ICustomDrawOperation? other) => false;
    public void Dispose() { }

    public void Render(ImmediateDrawingContext context)
    {
        var effect = GlassOptics.Effect;
        var feature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
        if (effect is null || feature is null) return;
        using var lease = feature.Lease();
        var canvas = lease.SkCanvas;

        float w = (float)_bounds.Width, h = (float)_bounds.Height;
        float minSide = Math.Min(w, h);
        double frost = Glass.EffectiveFrost;

        // Картинка фона → координаты панели (точки): (img − origin) / (пикселей на точку).
        var img = _backdrop.Frosted;
        float k = (float)(_pxPerDip * _backdrop.FrostScale);
        float ox = (float)(_originPx.X * _backdrop.FrostScale), oy = (float)(_originPx.Y * _backdrop.FrostScale);
        var local = SKMatrix.CreateScaleTranslation(1 / k, 1 / k, -ox / k, -oy / k);
        using var imageShader = img.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, local);

        var uniforms = new SKRuntimeEffectUniforms(effect)
        {
            ["size"] = new[] { w, h },
            ["radius"] = (float)Math.Min(_radius, minSide / 2),
            // Apple: у крупных элементов линзирование сильнее, у мелких — деликатнее.
            ["edge"] = Math.Clamp(minSide * 0.30f, 6f, 26f),
            ["strength"] = Math.Clamp(minSide * 0.16f, 3f, 18f),
            ["grain"] = (float)(frost * 0.035),
            ["spec"] = 0.22f,
        };
        var children = new SKRuntimeEffectChildren(effect) { ["backdrop"] = imageShader };
        using var shader = effect.ToShader(false, uniforms, children);
        using var paint = new SKPaint
        {
            Shader = shader,
            IsAntialias = true,
            FilterQuality = SKFilterQuality.Medium,
            Color = SKColors.White.WithAlpha((byte)Math.Clamp(lease.CurrentOpacity * 255, 0, 255)),
        };
        canvas.DrawRect(new SKRect(0, 0, w, h), paint);
    }
}
