using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SkiaSharp;

namespace SnapMark.Annotations;

public enum RedactMode { Blur, Pixelate }

/// <summary>
/// «Скрыть»: прямоугольник, под которым скриншот размыт или превращён в крупные пиксели —
/// для паролей, почты, номеров, лиц. Эффект считается из самого снимка (Source), поэтому
/// в сохранённой картинке под областью нет исходных пикселей.
/// </summary>
public sealed class RedactAnnotation : BoxAnnotation
{
    public RedactMode Mode { get; set; } = RedactMode.Blur;
    /// <summary>Сила 1…10.</summary>
    public double Strength { get; set; } = 7;
    /// <summary>Снимок экрана, из которого считается эффект (координаты аннотаций = пиксели снимка).</summary>
    public Bitmap? Source { get; set; }

    Bitmap? _cache;
    PixelRect _cacheRect;
    RedactMode _cacheMode;
    double _cacheStrength;

    public override Rect Bounds => Box;
    public override bool IsDegenerate => Math.Abs(Start.X - End.X) < 4 || Math.Abs(Start.Y - End.Y) < 4;
    public override bool HitTest(Point p, double tolerance) => Box.Inflate(tolerance).Contains(p);

    // Цвет и толщина к «скрытию» не относятся.
    public override void ApplyStyle(AnnotationStyle s) { }
    public override void ReadStyle(AnnotationStyle s) { }

    public override void Render(DrawingContext ctx)
    {
        var box = Box;
        if (Source is null)
        {
            ctx.DrawRectangle(new SolidColorBrush(Color.FromRgb(120, 120, 128)), null, box);
            return;
        }
        int sw = Source.PixelSize.Width, sh = Source.PixelSize.Height;
        int x1 = Math.Clamp((int)Math.Floor(box.X), 0, sw), y1 = Math.Clamp((int)Math.Floor(box.Y), 0, sh);
        int x2 = Math.Clamp((int)Math.Ceiling(box.Right), 0, sw), y2 = Math.Clamp((int)Math.Ceiling(box.Bottom), 0, sh);
        if (x2 - x1 < 2 || y2 - y1 < 2) return;
        var rect = new PixelRect(x1, y1, x2 - x1, y2 - y1);

        if (_cache is null || _cacheRect != rect || _cacheMode != Mode || Math.Abs(_cacheStrength - Strength) > 0.01)
        {
            _cache?.Dispose();
            _cache = RedactEffect.Build(Source, rect, Mode, Strength);
            _cacheRect = rect;
            _cacheMode = Mode;
            _cacheStrength = Strength;
        }
        if (_cache is null) return;
        using (ctx.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.None }))
            ctx.DrawImage(_cache, new Rect(0, 0, rect.Width, rect.Height), new Rect(rect.X, rect.Y, rect.Width, rect.Height));
    }

    public override Annotation Clone() =>
        CopyBaseTo(new RedactAnnotation { Start = Start, End = End, Mode = Mode, Strength = Strength, Source = Source });
}

/// <summary>Размытие и пикселизация части снимка через Skia.</summary>
internal static class RedactEffect
{
    static readonly ConditionalWeakTable<Bitmap, SKImage> Images = new();

    public static Bitmap? Build(Bitmap source, PixelRect r, RedactMode mode, double strength)
    {
        try
        {
            if (!Images.TryGetValue(source, out var full))
            {
                full = UI.GlassOptics.ToSkImage(source);
                if (full is null) return null;
                Images.AddOrUpdate(source, full);
            }
            using var sub = full.Subset(new SKRectI(r.X, r.Y, r.Right, r.Bottom));
            if (sub is null) return null;

            var info = new SKImageInfo(r.Width, r.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(info);
            if (surface is null) return null;
            var canvas = surface.Canvas;
            double s = Math.Clamp(strength, 1, 10);

            if (mode == RedactMode.Pixelate)
            {
                // Крупные «квадраты»: уменьшаем с усреднением, затем увеличиваем без сглаживания.
                int block = (int)Math.Round(6 + s * 3.2);
                int w = Math.Max(1, (r.Width + block - 1) / block), h = Math.Max(1, (r.Height + block - 1) / block);
                using var small = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
                using (var p = new SKPaint { FilterQuality = SKFilterQuality.High })
                    small.Canvas.DrawImage(sub, new SKRect(0, 0, w, h), p);
                using var smallImg = small.Snapshot();
                using (var p = new SKPaint { FilterQuality = SKFilterQuality.None })
                    canvas.DrawImage(smallImg, new SKRect(0, 0, w * block, h * block), p);
            }
            else
            {
                // Сильное размытие: текст под ним не читается уже на средней силе, на 10 — почти ровный цвет.
                // Радиус растёт квадратично (1 → ~10 px, 5 → ~45, 7 → ~80, 10 → ~150) и не меньше трети высоты области,
                // иначе крупный шрифт на мониторах с масштабом 150–200 % «просвечивает».
                double sigma = Math.Max(8 + s * s * 1.45, Math.Min(r.Width, r.Height) * (0.12 + s * 0.025));
                // Три отдельных шага (фильтр размытия вместе с масштабированием в Skia 2.88 даёт прозрачные края —
                // исходный текст просвечивал): 1) уменьшить, 2) размыть 1:1 с «прижатыми» краями, 3) увеличить.
                double down = Math.Max(1, sigma / 6);
                int w = Math.Max(2, (int)Math.Ceiling(r.Width / down)), h = Math.Max(2, (int)Math.Ceiling(r.Height / down));
                var smallInfo = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
                using var reduced = SKSurface.Create(smallInfo);
                using (var p = new SKPaint { FilterQuality = SKFilterQuality.High })
                    reduced.Canvas.DrawImage(sub, new SKRect(0, 0, w, h), p);
                using var reducedImg = reduced.Snapshot();
                using var blurred = SKSurface.Create(smallInfo);
                float ss = (float)(sigma / down);
                using (var p = new SKPaint { ImageFilter = SKImageFilter.CreateBlur(ss, ss, SKShaderTileMode.Clamp, null) })
                    blurred.Canvas.DrawImage(reducedImg, 0, 0, p);
                using var blurredImg = blurred.Snapshot();
                // Увеличиваем и гарантируем непрозрачность: альфа = 1 (цветовая матрица работает с непредумноженным цветом).
                using var opaque = SKColorFilter.CreateColorMatrix(new float[]
                {
                    1, 0, 0, 0, 0,
                    0, 1, 0, 0, 0,
                    0, 0, 1, 0, 0,
                    0, 0, 0, 0, 1,
                });
                using (var p = new SKPaint { FilterQuality = SKFilterQuality.High, ColorFilter = opaque })
                    canvas.DrawImage(blurredImg, new SKRect(0, 0, r.Width, r.Height), p);
                // Лёгкая «матовая» дымка, как у стекла: размытое не выглядит грязным.
                using var haze = new SKPaint { Color = new SKColor(255, 255, 255, 22) };
                canvas.DrawRect(new SKRect(0, 0, r.Width, r.Height), haze);
            }

            var wb = new WriteableBitmap(new PixelSize(r.Width, r.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
            using (var fb = wb.Lock())
                surface.ReadPixels(info, fb.Address, fb.RowBytes, 0, 0);
            return wb;
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            return null;
        }
    }
}
