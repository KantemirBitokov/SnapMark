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
    static string[] Variants => GlassShaderSource.Variants;

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
        var feature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
        if (feature is null) return;
        using var lease = feature.Lease();
        var canvas = lease.SkCanvas;

        float w = (float)_bounds.Width, h = (float)_bounds.Height;
        float minSide = Math.Min(w, h);
        double frost = Glass.EffectiveFrost;
        byte alpha = (byte)Math.Clamp(lease.CurrentOpacity * 255, 0, 255);

        // Картинка фона → координаты панели (точки): (img − origin) / (пикселей на точку).
        var img = _backdrop.Frosted;
        float k = (float)(_pxPerDip * _backdrop.FrostScale);
        float ox = (float)(_originPx.X * _backdrop.FrostScale), oy = (float)(_originPx.Y * _backdrop.FrostScale);
        var local = SKMatrix.CreateScaleTranslation(1 / k, 1 / k, -ox / k, -oy / k);
        using var imageShader = img.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, local);

        // Пользовательские шейдеры в Skia 2.88 падают при программной отрисовке (без видеокарты) — проверено
        // тестом tools/ShaderTest. Там рисуем только матовый фон, без преломления.
        var effect = lease.GrContext is not null ? GlassOptics.Effect : null;
        if (effect is null)
        {
            float rad = (float)Math.Min(_radius, minSide / 2);
            using var plain = new SKPaint { Shader = imageShader, IsAntialias = true, FilterQuality = SKFilterQuality.Medium, Color = SKColors.White.WithAlpha(alpha) };
            canvas.DrawRoundRect(new SKRect(0, 0, w, h), rad, rad, plain);
            return;
        }

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
            Color = SKColors.White.WithAlpha(alpha),
        };
        canvas.DrawRect(new SKRect(0, 0, w, h), paint);
    }
}
