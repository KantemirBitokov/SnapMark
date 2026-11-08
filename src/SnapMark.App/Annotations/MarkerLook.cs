using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SkiaSharp;
using SnapMark.Core;

namespace SnapMark.Annotations;

/// <summary>
/// Внешний вид меток в духе iOS: формы (круг, «сквиркл», скруглённый ромб, капля-булавка),
/// мягкая размытая тень и два стиля — «Стекло» (градиент, блик, светящийся ободок, цветная тень)
/// и плоский (сплошной цвет, белый ободок, нейтральная тень).
/// </summary>
internal static class MarkerLook
{
    /// <summary>Скруглённый шрифт для букв и цифр (Nunito, начертание 850, только латиница и цифры).</summary>
    public static readonly FontFamily LabelFont =
        new("avares://SnapMark/Assets/Fonts#SnapMark Marker, Segoe UI Variable Display, Segoe UI, SF Pro Rounded, .AppleSystemUIFont, Arial");

    public const double PinLength = 1.68;
    const double DiamondScale = 1.3;
    const double DiamondCorner = 0.2;
    const double Squircle = 5;

    // ── Контур ─────────────────────────────────────────────────────
    interface ISink
    {
        void Move(double x, double y);
        void Line(double x, double y);
        void Quad(double cx, double cy, double x, double y);
        void Cubic(double c1x, double c1y, double c2x, double c2y, double x, double y);
        void Close();
    }

    /// <summary>Контур формы с «радиусом» r вокруг (cx, cy) — те же формулы, что в макете.</summary>
    static void Trace(ISink s, MarkerShape shape, double cx, double cy, double r)
    {
        const double k = 0.5522847498; // четверть окружности кубической кривой
        switch (shape)
        {
            case MarkerShape.RoundedSquare:
            {
                // Суперэллипс n = 5 — «непрерывные» углы, как у иконок iOS.
                const int steps = 128;
                for (int i = 0; i < steps; i++)
                {
                    double t = i / (double)steps * 2 * Math.PI, c = Math.Cos(t), sn = Math.Sin(t);
                    double x = cx + r * Math.Sign(c) * Math.Pow(Math.Abs(c), 2 / Squircle);
                    double y = cy + r * Math.Sign(sn) * Math.Pow(Math.Abs(sn), 2 / Squircle);
                    if (i == 0) s.Move(x, y); else s.Line(x, y);
                }
                s.Close();
                break;
            }
            case MarkerShape.Diamond:
            {
                double d = r * DiamondScale;
                var p = new[] { (cx, cy - d), (cx + d, cy), (cx, cy + d), (cx - d, cy) };
                for (int i = 0; i < 4; i++)
                {
                    var a = p[(i + 3) % 4]; var b = p[i]; var c = p[(i + 1) % 4];
                    double x1 = b.Item1 + (a.Item1 - b.Item1) * DiamondCorner, y1 = b.Item2 + (a.Item2 - b.Item2) * DiamondCorner;
                    double x2 = b.Item1 + (c.Item1 - b.Item1) * DiamondCorner, y2 = b.Item2 + (c.Item2 - b.Item2) * DiamondCorner;
                    if (i == 0) s.Move(x1, y1); else s.Line(x1, y1);
                    s.Quad(b.Item1, b.Item2, x2, y2);
                }
                s.Close();
                break;
            }
            case MarkerShape.Pin:
            {
                // Капля: круглая «голова» плавно переходит в остриё.
                double d = r * PinLength;
                s.Move(cx, cy + d);
                s.Cubic(cx - r * 0.46, cy + d * 0.68, cx - r, cy + d * 0.36, cx - r, cy);
                s.Cubic(cx - r, cy - r * k, cx - r * k, cy - r, cx, cy - r);
                s.Cubic(cx + r * k, cy - r, cx + r, cy - r * k, cx + r, cy);
                s.Cubic(cx + r, cy + d * 0.36, cx + r * 0.46, cy + d * 0.68, cx, cy + d);
                s.Close();
                break;
            }
            default:
                s.Move(cx + r, cy);
                s.Cubic(cx + r, cy + r * k, cx + r * k, cy + r, cx, cy + r);
                s.Cubic(cx - r * k, cy + r, cx - r, cy + r * k, cx - r, cy);
                s.Cubic(cx - r, cy - r * k, cx - r * k, cy - r, cx, cy - r);
                s.Cubic(cx + r * k, cy - r, cx + r, cy - r * k, cx + r, cy);
                s.Close();
                break;
        }
    }

    sealed class GeoSink : ISink
    {
        readonly StreamGeometryContext _g;
        bool _open;
        public GeoSink(StreamGeometryContext g) => _g = g;
        public void Move(double x, double y)
        {
            if (_open) _g.EndFigure(true);
            _g.BeginFigure(new Point(x, y), true);
            _open = true;
        }
        public void Line(double x, double y) => _g.LineTo(new Point(x, y));
        public void Quad(double cx, double cy, double x, double y) => _g.QuadraticBezierTo(new Point(cx, cy), new Point(x, y));
        public void Cubic(double a, double b, double c, double d, double x, double y) =>
            _g.CubicBezierTo(new Point(a, b), new Point(c, d), new Point(x, y));
        public void Close()
        {
            if (_open) _g.EndFigure(true);
            _open = false;
        }
    }

    sealed class SkSink : ISink
    {
        public readonly SKPath Path = new();
        public void Move(double x, double y) => Path.MoveTo((float)x, (float)y);
        public void Line(double x, double y) => Path.LineTo((float)x, (float)y);
        public void Quad(double cx, double cy, double x, double y) => Path.QuadTo((float)cx, (float)cy, (float)x, (float)y);
        public void Cubic(double a, double b, double c, double d, double x, double y) =>
            Path.CubicTo((float)a, (float)b, (float)c, (float)d, (float)x, (float)y);
        public void Close() => Path.Close();
    }

    public static Geometry Geometry(MarkerShape shape, Point center, double r)
    {
        var geo = new StreamGeometry();
        using (var g = geo.Open())
            Trace(new GeoSink(g), shape, center.X, center.Y, r);
        return geo;
    }

    /// <summary>Габариты формы (без тени) относительно центра.</summary>
    public static Rect Extent(MarkerShape shape, Point c, double r) => shape switch
    {
        MarkerShape.Diamond => new Rect(c.X - r * DiamondScale, c.Y - r * DiamondScale, r * DiamondScale * 2, r * DiamondScale * 2),
        MarkerShape.Pin => new Rect(c.X - r, c.Y - r, r * 2, r * (1 + PinLength)),
        _ => new Rect(c.X - r, c.Y - r, r * 2, r * 2),
    };

    // ── Цвета ──────────────────────────────────────────────────────
    static Color Lighten(Color c, double f) => Color.FromRgb(
        (byte)Math.Round(c.R + (255 - c.R) * f), (byte)Math.Round(c.G + (255 - c.G) * f), (byte)Math.Round(c.B + (255 - c.B) * f));

    static Color Darken(Color c, double f) => Color.FromRgb((byte)(c.R * (1 - f)), (byte)(c.G * (1 - f)), (byte)(c.B * (1 - f)));

    static double Luma(Color c) => (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;

    /// <summary>Рисует саму метку (тень, ободок, заливку, блик) — без подписи.</summary>
    public static void DrawBody(DrawingContext ctx, MarkerShape shape, Point center, double r, Color color, bool glass)
    {
        double rim = r * (glass ? 0.07 : 0.075);
        double outer = r + rim;

        // 1. Мягкая тень: у стекла — цветная, у плоской — нейтральная.
        double sigma = r * (glass ? 0.154 : 0.115);
        double offY = r * (glass ? 0.115 : 0.096);
        var shadowColor = glass ? Color.FromArgb(115, color.R, color.G, color.B) : Color.FromArgb(72, 0, 0, 0);
        if (Shadow(shape, outer, sigma, shadowColor) is { } sh)
            ctx.DrawImage(sh.Image, new Rect(center.X + sh.Origin.X, center.Y + sh.Origin.Y + offY, sh.Size.Width, sh.Size.Height));

        // 2. Ободок. На почти белой метке белый ободок пропал бы — тогда светло-серый.
        bool nearWhite = Luma(color) > 0.9;
        IBrush rimBrush;
        if (nearWhite)
            rimBrush = new SolidColorBrush(Color.FromRgb(209, 209, 214));
        else if (glass)
            rimBrush = VerticalGradient(Color.FromArgb(242, 255, 255, 255), Color.FromArgb(140, 255, 255, 255));
        else
            rimBrush = Brushes.White;
        ctx.DrawGeometry(rimBrush, null, Geometry(shape, center, outer));

        // 3. Заливка.
        var body = Geometry(shape, center, r);
        if (!glass)
        {
            ctx.DrawGeometry(new SolidColorBrush(color), null, body);
            return;
        }
        ctx.DrawGeometry(VerticalGradient(Lighten(color, 0.22), Darken(color, 0.06)), null, body);

        // 4. Стеклянный блик на верхней половине.
        ctx.DrawGeometry(new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(140, 255, 255, 255), 0),
                new GradientStop(Color.FromArgb(0, 255, 255, 255), 0.5),
            },
        }, null, body);

        // 5. Тонкая светлая линия по внутреннему краю — «толщина стекла».
        double lw = Math.Max(1, r * 0.058);
        using (ctx.PushGeometryClip(body))
            ctx.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(89, 255, 255, 255)), lw),
                Geometry(shape, center, r - lw / 2));
    }

    static LinearGradientBrush VerticalGradient(Color top, Color bottom) => new()
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(top, 0), new GradientStop(bottom, 1) },
    };

    /// <summary>Рисует букву/цифру по центру метки.</summary>
    public static void DrawLabel(DrawingContext ctx, string label, Point center, double r, IBrush brush)
    {
        double size = r * (label.Length > 1 ? 0.9 : 1.1);
        var ft = new FormattedText(label, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(LabelFont, FontStyle.Normal, FontWeight.Normal), size, brush);
        // Базовая линия на 0.36 em ниже центра — визуальный центр заглавных и цифр.
        ctx.DrawText(ft, new Point(center.X - ft.Width / 2, center.Y + size * 0.36 - ft.Baseline));
    }

    // ── Размытая тень (Skia), с кэшем: размытие не зависит от масштаба, картинка маленькая ──
    sealed record ShadowImage(Bitmap Image, Point Origin, Size Size);

    static readonly Dictionary<(MarkerShape, int, int, uint), ShadowImage?> Cache = new();

    static ShadowImage? Shadow(MarkerShape shape, double r, double sigma, Color color)
    {
        var key = (shape, (int)Math.Round(r * 4), (int)Math.Round(sigma * 4), color.ToUInt32());
        lock (Cache)
        {
            if (Cache.TryGetValue(key, out var hit)) return hit;
            if (Cache.Count > 256)
            {
                // Уже записанные кадры держат свои ссылки на картинки — освобождать безопасно.
                foreach (var old in Cache.Values) old?.Image.Dispose();
                Cache.Clear();
            }
            var img = BuildShadow(shape, key.Item2 / 4.0, key.Item3 / 4.0, color);
            Cache[key] = img;
            return img;
        }
    }

    static ShadowImage? BuildShadow(MarkerShape shape, double r, double sigma, Color color)
    {
        try
        {
            const double scale = 2; // запас чёткости при увеличении
            var sink = new SkSink();
            Trace(sink, shape, 0, 0, r);
            using var path = sink.Path;
            var b = path.Bounds;
            double pad = sigma * 3 + 1;
            double left = b.Left - pad, top = b.Top - pad;
            double w = b.Width + pad * 2, h = b.Height + pad * 2;
            int pw = Math.Max(1, (int)Math.Ceiling(w * scale)), ph = Math.Max(1, (int)Math.Ceiling(h * scale));

            var info = new SKImageInfo(pw, ph, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(info);
            if (surface is null) return null;
            var canvas = surface.Canvas;
            canvas.Clear(SKColors.Transparent);
            canvas.Scale((float)scale);
            canvas.Translate((float)-left, (float)-top);
            using (var paint = new SKPaint
                   {
                       IsAntialias = true,
                       Color = new SKColor(color.R, color.G, color.B, color.A),
                       MaskFilter = sigma > 0.1 ? SKMaskFilter.CreateBlur(SKBlurStyle.Normal, (float)sigma) : null,
                   })
                canvas.DrawPath(path, paint);
            canvas.Flush();

            var wb = new WriteableBitmap(new PixelSize(pw, ph), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
            using (var fb = wb.Lock())
                surface.ReadPixels(info, fb.Address, fb.RowBytes, 0, 0);
            return new ShadowImage(wb, new Point(left, top), new Size(pw / scale, ph / scale));
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            return null;
        }
    }
}
