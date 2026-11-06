using System.Globalization;
using Avalonia;
using Avalonia.Media;
using SnapMark.Core;

namespace SnapMark.Annotations;

// Все координаты аннотаций — в пикселях исходного снимка экрана (не логические точки окна).
// Благодаря этому экспорт пиксель-в-пиксель, а изменение рамки выделения не сдвигает аннотации.

public enum ToolKind
{
    Select, Pen, Pencil, Highlighter, Eraser,
    Line, Arrow, DoubleArrow, Rectangle, Ellipse,
    Text, Marker, MarkerArrow,
    /// <summary>Скрыть: размытие или пикселизация области.</summary>
    Redact,
}

public static class ToolKinds
{
    public static bool IsShape(ToolKind t) =>
        t is ToolKind.Line or ToolKind.Arrow or ToolKind.DoubleArrow or ToolKind.Rectangle or ToolKind.Ellipse;

    public static bool IsFreehand(ToolKind t) => t is ToolKind.Pen or ToolKind.Pencil or ToolKind.Highlighter;
}

/// <summary>Текущий стиль рисования (цвет, толщина, шрифт…).</summary>
public sealed class AnnotationStyle
{
    public Color Color { get; set; } = Color.Parse("#FF3B30");
    public double Thickness { get; set; } = 4;
    public double FontSize { get; set; } = 28;
    public string FontFamily { get; set; } = "Segoe UI";
    /// <summary>Начертание 100…900 (300 — тонкий, 400 — обычный, 600 — полужирный, 700 — жирный, 900 — чёрный).</summary>
    public int FontWeight { get; set; } = 700;
    public bool Italic { get; set; }
    public TextFrame TextFrame { get; set; } = TextFrame.None;
    public bool TextShadow { get; set; } = true;
    /// <summary>Цвет тени текста; null — автоматически.</summary>
    public Color? ShadowColor { get; set; }
    public Color? PlateColor { get; set; }
    public Color? BorderColor { get; set; }
    public MarkerShape MarkerShape { get; set; } = MarkerShape.Circle;
    public bool Dashed { get; set; }
    public bool Filled { get; set; }
    /// <summary>Непрозрачность 0.1…1 (как ползунок в Markup на iOS).</summary>
    public double Opacity { get; set; } = 1.0;
}

/// <summary>Шрифты на выбор (если шрифта нет в системе — подставится запасной).</summary>
public static class FontCatalog
{
    public static readonly string[] Names =
    {
        "Segoe UI", "Segoe UI Variable Display", "Bahnschrift", "Arial", "Calibri", "Verdana", "Tahoma",
        "Trebuchet MS", "Georgia", "Times New Roman", "Cambria", "Consolas", "Courier New",
        "Comic Sans MS", "Impact", "Segoe Print", "Segoe Script",
    };

    static readonly Dictionary<string, FontFamily> Cache = new();

    public static FontFamily Get(string? name)
    {
        name = string.IsNullOrWhiteSpace(name) ? "Segoe UI" : name;
        if (!Cache.TryGetValue(name, out var family))
        {
            family = new FontFamily($"{name}, Segoe UI, SF Pro Text, Helvetica Neue, Ubuntu, Arial");
            Cache[name] = family;
        }
        return family;
    }

    public static Typeface Typeface(string? name, int weight, bool italic) =>
        new(Get(name), italic ? FontStyle.Italic : FontStyle.Normal, (FontWeight)Math.Clamp(weight, 100, 950));
}

/// <summary>Отрисовка текста на подложке / в рамке / без рамки (общая для «Текста» и комментариев).</summary>
public static class TextPlate
{
    public static double Padding(TextFrame frame, double size) => frame == TextFrame.None ? 0 : size * 0.32;

    /// <param name="make">Создаёт FormattedText нужной кистью (для тени и для основного текста).</param>
    /// <param name="box">Прямоугольник вместе с отступами.</param>
    /// <param name="accent">Цвет рамки для режима Outline.</param>
    /// <param name="shadow">Тень: у текста без рамки — под буквами, у подложки/рамки — под плашкой.</param>
    /// <param name="shadowColor">Цвет тени; null — автоматически (тёмная под светлым текстом, светлая под тёмным).</param>
    /// <param name="plateColor">Цвет подложки; null — автоматически.</param>
    /// <param name="borderColor">Цвет рамки; null — <paramref name="accent"/>.</param>
    public static void Draw(DrawingContext ctx, Func<IBrush, FormattedText> make, Rect box, Color textColor,
        TextFrame frame, double size, Color accent, bool shadow = true, Color? shadowColor = null,
        Color? plateColor = null, Color? borderColor = null)
    {
        double pad = Padding(frame, size);
        bool lightText = (0.299 * textColor.R + 0.587 * textColor.G + 0.114 * textColor.B) / 255.0 > 0.72;
        double radius = size * 0.24;
        bool hasPlate = frame is TextFrame.Fill or TextFrame.FillOutline;
        bool hasBorder = frame is TextFrame.Outline or TextFrame.FillOutline;

        if (!hasPlate)
        {
            if (shadow)
            {
                // Тень делает текст читаемым на любом фоне: мягкий слой + чёткий сдвиг.
                var sc = shadowColor is Color c
                    ? Color.FromArgb(170, c.R, c.G, c.B)
                    : lightText ? Color.FromArgb(150, 0, 0, 0) : Color.FromArgb(150, 255, 255, 255);
                var o = Math.Max(1, size / 22);
                var soft = Color.FromArgb((byte)(sc.A / 3), sc.R, sc.G, sc.B);
                ctx.DrawText(make(new SolidColorBrush(soft)), new Point(box.X + pad + o * 1.8, box.Y + pad + o * 1.8));
                ctx.DrawText(make(new SolidColorBrush(sc)), new Point(box.X + pad + o, box.Y + pad + o));
            }
        }

        var bc = borderColor ?? accent;
        IPen? border = hasBorder
            ? new Pen(new SolidColorBrush(Color.FromRgb(bc.R, bc.G, bc.B)), Math.Max(1.5, size / 12))
            : hasPlate ? new Pen(new SolidColorBrush(Color.FromArgb(40, 0, 0, 0)), 1) : null;

        if (hasPlate)
        {
            var plate = plateColor ?? (lightText ? Color.FromArgb(235, 28, 28, 30) : Color.FromArgb(242, 255, 255, 255));
            var plateShadow = shadowColor is Color pc ? Color.FromArgb(90, pc.R, pc.G, pc.B) : Color.FromArgb(55, 0, 0, 0);
            if (shadow)
                ctx.DrawRectangle(new SolidColorBrush(plate), border, box, radius, radius,
                    new BoxShadows(new BoxShadow { OffsetY = size * 0.08, Blur = size * 0.45, Color = plateShadow }));
            else
                ctx.DrawRectangle(new SolidColorBrush(plate), border, box, radius, radius);
        }
        else if (border is not null)
        {
            // Только рамка — без подложки, фон скриншота виден насквозь.
            ctx.DrawRectangle(null, border, box, radius, radius);
        }
        ctx.DrawText(make(new SolidColorBrush(textColor)), new Point(box.X + pad, box.Y + pad));
    }
}

public static class UiFonts
{
    // Те же семейства, что Sm.Font* в Styles/Colors.axaml (оптические размеры, как у San Francisco).
    public static readonly FontFamily Family =
        new("Segoe UI Variable Text, Segoe UI, SF Pro Text, .AppleSystemUIFont, Helvetica Neue, Inter, Ubuntu, Cantarell, Arial");
    public static readonly FontFamily Display =
        new("Segoe UI Variable Display, Segoe UI, SF Pro Display, .AppleSystemUIFont, Helvetica Neue, Inter Display, Inter, Ubuntu, Cantarell, Arial");
    /// <summary>Для мелких подписей до 11 pt.</summary>
    public static readonly FontFamily Small =
        new("Segoe UI Variable Small, Segoe UI, SF Pro Text, .AppleSystemUIFont, Helvetica Neue, Inter, Ubuntu, Cantarell, Arial");
}

public abstract class Annotation
{
    public Color Color { get; set; } = Colors.Red;
    public double Thickness { get; set; } = 4;
    public bool Dashed { get; set; }

    public abstract Rect Bounds { get; }
    public abstract void Render(DrawingContext ctx);
    public abstract void Offset(double dx, double dy);
    public abstract Annotation Clone();
    public virtual bool IsDegenerate => false;

    public virtual bool HitTest(Point p, double tolerance) => Bounds.Inflate(tolerance).Contains(p);

    public virtual void ApplyStyle(AnnotationStyle s)
    {
        Color = WithOpacity(s.Color, s.Opacity);
        Thickness = s.Thickness;
        Dashed = s.Dashed;
    }

    /// <summary>Обратное: подтянуть стиль выделенной аннотации в панель.</summary>
    public virtual void ReadStyle(AnnotationStyle s)
    {
        s.Color = Color.FromRgb(Color.R, Color.G, Color.B);
        s.Opacity = Math.Max(0.1, Color.A / 255.0);
        s.Thickness = Thickness;
        s.Dashed = Dashed;
    }

    public static Color WithOpacity(Color c, double opacity) =>
        Color.FromArgb((byte)Math.Round(255 * Math.Clamp(opacity, 0.05, 1)), c.R, c.G, c.B);

    protected T CopyBaseTo<T>(T target) where T : Annotation
    {
        target.Color = Color;
        target.Thickness = Thickness;
        target.Dashed = Dashed;
        return target;
    }

    protected IPen MakePen(double? thickness = null, Color? color = null, bool allowDash = true)
    {
        var t = thickness ?? Thickness;
        IDashStyle? dash = Dashed && allowDash ? new DashStyle(new double[] { 1.6, 2.4 }, 0) : null;
        return new Pen(new SolidColorBrush(color ?? Color), t, dash, PenLineCap.Round, PenLineJoin.Round);
    }

    // ── Геометрические помощники ──────────────────────────────────────
    public static double Distance(Point a, Point b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    protected static double DistanceToSegment(Point p, Point a, Point b)
    {
        double vx = b.X - a.X, vy = b.Y - a.Y;
        double len2 = vx * vx + vy * vy;
        if (len2 < 1e-9) return Distance(p, a);
        double t = Math.Clamp(((p.X - a.X) * vx + (p.Y - a.Y) * vy) / len2, 0, 1);
        return Distance(p, new Point(a.X + t * vx, a.Y + t * vy));
    }

    protected static Rect RectFrom(Point a, Point b) =>
        new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

    protected static Point Shift(Point p, double dx, double dy) => new(p.X + dx, p.Y + dy);

    /// <summary>Залитый наконечник стрелки с остриём в tip, направленный от from.</summary>
    protected static void DrawArrowHead(DrawingContext ctx, Point from, Point tip, double length, double width, IBrush brush)
    {
        double dx = tip.X - from.X, dy = tip.Y - from.Y;
        double len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1e-6) return;
        dx /= len; dy /= len;
        var baseCenter = new Point(tip.X - dx * length, tip.Y - dy * length);
        double px = -dy * width / 2, py = dx * width / 2;

        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(tip, true);
            g.LineTo(new Point(baseCenter.X + px, baseCenter.Y + py));
            g.LineTo(new Point(baseCenter.X - px, baseCenter.Y - py));
            g.EndFigure(true);
        }
        ctx.DrawGeometry(brush, new Pen(brush, 1, lineJoin: PenLineJoin.Round), geo);
    }

    protected static Point MoveTowards(Point from, Point to, double distance)
    {
        double dx = to.X - from.X, dy = to.Y - from.Y;
        double len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1e-6) return from;
        return new Point(from.X + dx / len * distance, from.Y + dy / len * distance);
    }

    protected static bool IsLight(Color c) => (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0 > 0.72;
}

// ═════════════════════════════ Перо / карандаш / маркер-выделитель ═════════════════════════════
public enum StrokeKind { Ink, Pencil, Highlighter }

/// <summary>
/// Рисование от руки в духе Markup на iOS/macOS:
///  • Ink — перо с переменной толщиной (быстрее ведёшь — тоньше; со стилусом — по нажиму) и заострёнными концами;
///  • Pencil — тонкая полупрозрачная линия постоянной толщины;
///  • Highlighter — широкий полупрозрачный маркер с плоским концом.
/// </summary>
public sealed class FreehandAnnotation : Annotation
{
    public List<Point> Points { get; } = new();
    /// <summary>Множитель толщины в каждой точке (для Ink), 1 = базовая толщина.</summary>
    public List<double> Widths { get; } = new();
    public StrokeKind Kind { get; set; } = StrokeKind.Ink;

    public bool Highlighter => Kind == StrokeKind.Highlighter;

    double StrokeWidth => Kind switch
    {
        StrokeKind.Highlighter => Math.Max(12, Thickness * 3.5),
        StrokeKind.Pencil => Math.Max(1.2, Thickness * 0.55),
        _ => Thickness,
    };

    public override Rect Bounds
    {
        get
        {
            if (Points.Count == 0) return default;
            double minX = Points.Min(p => p.X), minY = Points.Min(p => p.Y);
            double maxX = Points.Max(p => p.X), maxY = Points.Max(p => p.Y);
            return new Rect(minX, minY, maxX - minX, maxY - minY).Inflate(StrokeWidth * 0.75 + 1);
        }
    }

    public void AddPoint(Point p, double width = 1)
    {
        if (Points.Count > 0 && Distance(Points[^1], p) < 1.5) return;
        Points.Add(p);
        Widths.Add(width);
    }

    public override void Render(DrawingContext ctx)
    {
        if (Points.Count == 0) return;
        switch (Kind)
        {
            case StrokeKind.Ink:
                RenderInk(ctx);
                break;
            case StrokeKind.Pencil:
                RenderPolyline(ctx, Color.FromArgb((byte)(Color.A * 0.85), Color.R, Color.G, Color.B),
                    StrokeWidth, PenLineCap.Round, allowDash: true);
                break;
            default:
                RenderPolyline(ctx, Color.FromArgb((byte)(Color.A * 0.42), Color.R, Color.G, Color.B),
                    StrokeWidth, PenLineCap.Square, allowDash: false);
                break;
        }
    }

    void RenderPolyline(DrawingContext ctx, Color color, double width, PenLineCap cap, bool allowDash)
    {
        var brush = new SolidColorBrush(color);
        if (Points.Count == 1)
        {
            ctx.DrawEllipse(brush, null, Points[0], width / 2, width / 2);
            return;
        }
        IDashStyle? dash = Dashed && allowDash ? new DashStyle(new double[] { 1.6, 2.4 }, 0) : null;
        var pen = new Pen(brush, width, dash, cap, PenLineJoin.Round);
        ctx.DrawGeometry(null, pen, SmoothPath(Points));
    }

    /// <summary>Сглаживание: квадратичные кривые через середины отрезков.</summary>
    static StreamGeometry SmoothPath(IReadOnlyList<Point> pts)
    {
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(pts[0], false);
            for (int i = 1; i < pts.Count - 1; i++)
            {
                var mid = new Point((pts[i].X + pts[i + 1].X) / 2, (pts[i].Y + pts[i + 1].Y) / 2);
                g.QuadraticBezierTo(pts[i], mid);
            }
            g.LineTo(pts[^1]);
            g.EndFigure(false);
        }
        return geo;
    }

    /// <summary>Перо: контур переменной ширины, залитый одним цветом (без «бусин» на стыках).</summary>
    void RenderInk(DrawingContext ctx)
    {
        var brush = new SolidColorBrush(Color);
        int n = Points.Count;
        if (n == 1)
        {
            var r = Thickness * Widths[0] / 2;
            ctx.DrawEllipse(brush, null, Points[0], r, r);
            return;
        }
        if (Dashed)
        {
            // Пунктир переменной толщины выглядит странно — рисуем обычной линией.
            RenderPolyline(ctx, Color, Thickness, PenLineCap.Round, allowDash: true);
            return;
        }

        // Лёгкое сглаживание точек скользящим средним.
        var pts = new Point[n];
        for (int i = 0; i < n; i++)
        {
            var a = Points[Math.Max(0, i - 1)];
            var b = Points[i];
            var c = Points[Math.Min(n - 1, i + 1)];
            pts[i] = i == 0 || i == n - 1 ? b : new Point((a.X + 2 * b.X + c.X) / 4, (a.Y + 2 * b.Y + c.Y) / 4);
        }

        // Толщина в каждой точке с заострением концов.
        int taper = Math.Clamp(n / 4, 1, 7);
        var w = new double[n];
        for (int i = 0; i < n; i++)
        {
            double f = Widths.Count > i ? Widths[i] : 1;
            double start = i < taper ? 0.35 + 0.65 * i / taper : 1;
            double end = i > n - 1 - taper ? 0.35 + 0.65 * (n - 1 - i) / taper : 1;
            w[i] = Math.Max(0.6, Thickness * f * Math.Min(start, end));
        }

        var left = new Point[n];
        var right = new Point[n];
        for (int i = 0; i < n; i++)
        {
            var p0 = pts[Math.Max(0, i - 1)];
            var p1 = pts[Math.Min(n - 1, i + 1)];
            double dx = p1.X - p0.X, dy = p1.Y - p0.Y;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1e-6) { dx = 1; dy = 0; len = 1; }
            double nx = -dy / len * w[i] / 2, ny = dx / len * w[i] / 2;
            left[i] = new Point(pts[i].X + nx, pts[i].Y + ny);
            right[i] = new Point(pts[i].X - nx, pts[i].Y - ny);
        }

        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(left[0], true);
            for (int i = 1; i < n; i++) g.LineTo(left[i]);
            for (int i = n - 1; i >= 0; i--) g.LineTo(right[i]);
            g.EndFigure(true);
        }
        ctx.DrawGeometry(brush, null, geo);
        // Скруглённые концы.
        ctx.DrawEllipse(brush, null, pts[0], w[0] / 2, w[0] / 2);
        ctx.DrawEllipse(brush, null, pts[n - 1], w[n - 1] / 2, w[n - 1] / 2);
    }

    public override bool HitTest(Point p, double tolerance)
    {
        var limit = tolerance + StrokeWidth / 2;
        if (Points.Count == 1) return Distance(p, Points[0]) <= limit;
        for (int i = 1; i < Points.Count; i++)
            if (DistanceToSegment(p, Points[i - 1], Points[i]) <= limit) return true;
        return false;
    }

    public override void Offset(double dx, double dy)
    {
        for (int i = 0; i < Points.Count; i++) Points[i] = Shift(Points[i], dx, dy);
    }

    public override Annotation Clone()
    {
        var c = CopyBaseTo(new FreehandAnnotation { Kind = Kind });
        c.Points.AddRange(Points);
        c.Widths.AddRange(Widths);
        return c;
    }
}

// ═════════════════════════════ Линия / стрелка / двойная стрелка ═════════════════════════════
public sealed class LineAnnotation : Annotation
{
    public Point Start { get; set; }
    public Point End { get; set; }
    public bool ArrowStart { get; set; }
    public bool ArrowEnd { get; set; }

    public override Rect Bounds => RectFrom(Start, End).Inflate(Math.Max(Thickness, HeadLength) / 2 + 2);
    public override bool IsDegenerate => Distance(Start, End) < 4;

    double HeadLength => Math.Max(12, Thickness * 4.2);

    public override void Render(DrawingContext ctx)
    {
        var s = Start;
        var e = End;
        double len = Distance(s, e);
        double head = Math.Min(HeadLength, len * (ArrowStart && ArrowEnd ? 0.45 : 0.7));
        double width = head * 0.72;
        var brush = new SolidColorBrush(Color);

        if (ArrowEnd)
        {
            DrawArrowHead(ctx, Start, End, head, width, brush);
            e = MoveTowards(End, Start, head * 0.8);
        }
        if (ArrowStart)
        {
            DrawArrowHead(ctx, End, Start, head, width, brush);
            s = MoveTowards(Start, End, head * 0.8);
        }
        ctx.DrawLine(MakePen(), s, e);
    }

    public override bool HitTest(Point p, double tolerance) =>
        DistanceToSegment(p, Start, End) <= tolerance + Thickness / 2 + 2;

    public override void Offset(double dx, double dy)
    {
        Start = Shift(Start, dx, dy);
        End = Shift(End, dx, dy);
    }

    public override Annotation Clone() =>
        CopyBaseTo(new LineAnnotation { Start = Start, End = End, ArrowStart = ArrowStart, ArrowEnd = ArrowEnd });
}

// ═════════════════════════════ Прямоугольник / эллипс ═════════════════════════════
public abstract class BoxAnnotation : Annotation
{
    public Point Start { get; set; }
    public Point End { get; set; }
    public bool Filled { get; set; }

    public Rect Box => RectFrom(Start, End);
    public override Rect Bounds => Box.Inflate(Thickness / 2 + 1);
    public override bool IsDegenerate => Math.Abs(Start.X - End.X) < 3 || Math.Abs(Start.Y - End.Y) < 3;

    public override void Offset(double dx, double dy)
    {
        Start = Shift(Start, dx, dy);
        End = Shift(End, dx, dy);
    }

    public override void ApplyStyle(AnnotationStyle s)
    {
        base.ApplyStyle(s);
        Filled = s.Filled;
    }

    public override void ReadStyle(AnnotationStyle s)
    {
        base.ReadStyle(s);
        s.Filled = Filled;
    }

    public override bool HitTest(Point p, double tolerance)
    {
        if (Filled) return Bounds.Inflate(tolerance).Contains(p);
        // Пустая рамка: попадание только рядом с контуром, чтобы можно было рисовать внутри.
        var outer = Box.Inflate(tolerance + Thickness / 2);
        var inner = Box.Deflate(tolerance + Thickness / 2);
        return outer.Contains(p) && !(inner.Width > 0 && inner.Height > 0 && inner.Contains(p));
    }
}

public sealed class RectAnnotation : BoxAnnotation
{
    public override void Render(DrawingContext ctx)
    {
        var r = Box;
        double radius = Math.Min(Thickness * 0.8, Math.Min(r.Width, r.Height) / 4);
        if (Filled) ctx.DrawRectangle(new SolidColorBrush(Color), null, r, radius, radius);
        else ctx.DrawRectangle(null, MakePen(), r, radius, radius);
    }

    public override Annotation Clone() => CopyBaseTo(new RectAnnotation { Start = Start, End = End, Filled = Filled });
}

public sealed class EllipseAnnotation : BoxAnnotation
{
    public override void Render(DrawingContext ctx)
    {
        var r = Box;
        if (Filled) ctx.DrawEllipse(new SolidColorBrush(Color), null, r.Center, r.Width / 2, r.Height / 2);
        else ctx.DrawEllipse(null, MakePen(), r.Center, r.Width / 2, r.Height / 2);
    }

    public override Annotation Clone() => CopyBaseTo(new EllipseAnnotation { Start = Start, End = End, Filled = Filled });
}

// ═════════════════════════════ Текст ═════════════════════════════
public sealed class TextAnnotation : Annotation
{
    public Point Position { get; set; }
    public string Text { get; set; } = "";
    public double FontSize { get; set; } = 28;
    public string FontFamily { get; set; } = "Segoe UI";
    public int FontWeight { get; set; } = 700;
    public bool Italic { get; set; }
    public TextFrame Frame { get; set; } = TextFrame.None;
    public bool Shadow { get; set; } = true;
    public Color? ShadowColor { get; set; }
    public Color? PlateColor { get; set; }
    public Color? BorderColor { get; set; }

    public override bool IsDegenerate => string.IsNullOrWhiteSpace(Text);

    double Pad => TextPlate.Padding(Frame, FontSize);

    public FormattedText Format(IBrush? brush = null) => new(
        Text.Length == 0 ? " " : Text,
        CultureInfo.CurrentCulture,
        FlowDirection.LeftToRight,
        FontCatalog.Typeface(FontFamily, FontWeight, Italic),
        FontSize,
        brush ?? new SolidColorBrush(Color));

    public override Rect Bounds
    {
        get
        {
            var ft = Format();
            return new Rect(Position.X, Position.Y, ft.Width, ft.Height).Inflate(Pad);
        }
    }

    public override void Render(DrawingContext ctx)
    {
        var ft = Format();
        var box = new Rect(Position.X, Position.Y, ft.Width, ft.Height).Inflate(Pad);
        TextPlate.Draw(ctx, b => Format(b), box, Color, Frame, FontSize, Color, Shadow, ShadowColor, PlateColor, BorderColor);
    }

    public override void Offset(double dx, double dy) => Position = Shift(Position, dx, dy);

    public override void ApplyStyle(AnnotationStyle s)
    {
        base.ApplyStyle(s);
        FontSize = s.FontSize;
        FontFamily = s.FontFamily;
        FontWeight = s.FontWeight;
        Italic = s.Italic;
        Frame = s.TextFrame;
        Shadow = s.TextShadow;
        ShadowColor = s.ShadowColor;
        PlateColor = s.PlateColor;
        BorderColor = s.BorderColor;
    }

    public override void ReadStyle(AnnotationStyle s)
    {
        base.ReadStyle(s);
        s.FontSize = FontSize;
        s.FontFamily = FontFamily;
        s.FontWeight = FontWeight;
        s.Italic = Italic;
        s.TextFrame = Frame;
        s.TextShadow = Shadow;
        s.ShadowColor = ShadowColor;
        s.PlateColor = PlateColor;
        s.BorderColor = BorderColor;
    }

    public override Annotation Clone() => CopyBaseTo(new TextAnnotation
    {
        Position = Position, Text = Text, FontSize = FontSize, FontFamily = FontFamily,
        FontWeight = FontWeight, Italic = Italic, Frame = Frame, Shadow = Shadow, ShadowColor = ShadowColor,
        PlateColor = PlateColor, BorderColor = BorderColor,
    });
}

// ═════════════════════════════ Метка A, B, C… / 1, 2, 3… (с хвостом-стрелкой или без) ═════════════════════════════
public sealed class MarkerAnnotation : Annotation
{
    /// <summary>Постоянный идентификатор: к нему привязан комментарий, буква может меняться при перенумерации.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Порядок создания — по нему раздаются обозначения.</summary>
    public long Sequence { get; set; }
    public Point Center { get; set; }
    public string Label { get; set; } = "A";
    /// <summary>Куда указывает стрелка. null — метка без стрелки.</summary>
    public Point? Tail { get; set; }
    public double FontSize { get; set; } = 28;
    public MarkerShape Shape { get; set; } = MarkerShape.Circle;
    /// <summary>Цвет буквы/цифры. null — автоматически (белый на тёмном, тёмный на светлом).</summary>
    public Color? LabelColor { get; set; }

    // Собственный стиль подписи-комментария этой метки (когда включено «На картинке»).
    public string CommentFont { get; set; } = "Segoe UI";
    public int CommentWeight { get; set; } = 600;
    public bool CommentItalic { get; set; }
    public double CommentSize { get; set; } = 22;
    public Color CommentColor { get; set; } = Color.Parse("#1C1C1E");
    public TextFrame CommentFrame { get; set; } = TextFrame.Fill;
    public bool CommentShadow { get; set; } = true;
    public Color? CommentShadowColor { get; set; }
    public Color? CommentPlateColor { get; set; }
    /// <summary>null — цвет метки.</summary>
    public Color? CommentBorderColor { get; set; }

    public void ApplyCommentStyle(CommentLabelStyle st)
    {
        CommentPlateColor = st.PlateColor;
        CommentBorderColor = st.BorderColor;
        CommentShadow = st.Shadow;
        CommentShadowColor = st.ShadowColor;
        CommentFont = st.FontFamily;
        CommentWeight = st.FontWeight;
        CommentItalic = st.Italic;
        CommentSize = st.Size;
        CommentColor = st.Color;
        CommentFrame = st.Frame;
    }

    public double Radius => Math.Max(11, FontSize * 0.62) * (Label.Length > 1 ? 1.15 : 1);

    public override Rect Bounds
    {
        get
        {
            double r = Radius * (Shape == MarkerShape.Diamond ? 1.25 : 1.05);
            var rect = new Rect(Center.X - r, Center.Y - r, r * 2, r * 2).Inflate(3);
            if (Shape == MarkerShape.Pin) rect = rect.Union(new Rect(Center.X - 2, Center.Y, 4, Radius * 2.15 + 6));
            if (Tail is Point t) rect = rect.Union(new Rect(t.X - 8, t.Y - 8, 16, 16));
            return rect;
        }
    }

    public override void Render(DrawingContext ctx)
    {
        var brush = new SolidColorBrush(Color);
        var light = IsLight(Color);

        if (Tail is Point tail && Distance(tail, Center) > Radius)
        {
            double t = Math.Max(2.5, Radius / 5.5);
            double head = Math.Max(11, t * 4);
            var start = MoveTowards(Center, tail, Radius * 0.9);
            DrawArrowHead(ctx, start, tail, head, head * 0.75, brush);
            var end = MoveTowards(tail, start, head * 0.8);
            ctx.DrawLine(new Pen(brush, t, lineCap: PenLineCap.Round), start, end);
        }

        // Тень → обводка (фигура чуть больше) → заливка. Так у любой формы ровный контур без швов.
        double outline = Math.Max(2, Radius * 0.13);
        IBrush outlineBrush = light ? new SolidColorBrush(Color.FromRgb(40, 40, 42)) : Brushes.White;
        using (ctx.PushTransform(Matrix.CreateTranslation(0, Radius * 0.08)))
            DrawShape(ctx, new SolidColorBrush(Color.FromArgb(70, 0, 0, 0)), Radius + outline + 1.5);
        DrawShape(ctx, outlineBrush, Radius + outline);
        DrawShape(ctx, brush, Radius);

        var ft = new FormattedText(Label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(UiFonts.Display, FontStyle.Normal, Avalonia.Media.FontWeight.Bold),
            Radius * (Label.Length > 1 ? 0.95 : 1.2),
            LabelColor is Color lc ? new SolidColorBrush(lc)
                : light ? new SolidColorBrush(Color.FromRgb(28, 28, 30)) : (IBrush)Brushes.White);
        ctx.DrawText(ft, new Point(Center.X - ft.Width / 2, Center.Y - ft.Height / 2));
    }

    void DrawShape(DrawingContext ctx, IBrush brush, double r)
    {
        double cx = Center.X, cy = Center.Y;
        switch (Shape)
        {
            case MarkerShape.RoundedSquare:
            {
                double half = r * 0.95;
                ctx.DrawRectangle(brush, null, new Rect(cx - half, cy - half, half * 2, half * 2), r * 0.34, r * 0.34);
                break;
            }
            case MarkerShape.Diamond:
            {
                double d = r * 1.22;
                ctx.DrawGeometry(brush, null, Polygon(new Point(cx, cy - d), new Point(cx + d, cy), new Point(cx, cy + d), new Point(cx - d, cy)));
                break;
            }
            case MarkerShape.Pin:
            {
                // Булавка: ровная симметричная капля — круглая «голова» плавно переходит в остриё.
                double extra = r - Radius;
                double d = Radius * 1.75 + extra * 1.5;           // от центра до острия
                var geo = new StreamGeometry();
                using (var g = geo.Open())
                {
                    g.BeginFigure(new Point(cx, cy + d), true);
                    g.CubicBezierTo(new Point(cx - r * 0.46, cy + d * 0.68), new Point(cx - r, cy + d * 0.36), new Point(cx - r, cy));
                    g.ArcTo(new Point(cx + r, cy), new Size(r, r), 0, false, SweepDirection.Clockwise);
                    g.CubicBezierTo(new Point(cx + r, cy + d * 0.36), new Point(cx + r * 0.46, cy + d * 0.68), new Point(cx, cy + d));
                    g.EndFigure(true);
                }
                ctx.DrawGeometry(brush, null, geo);
                break;
            }
            default:
                ctx.DrawEllipse(brush, null, Center, r, r);
                break;
        }
    }

    static StreamGeometry Polygon(params Point[] pts)
    {
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(pts[0], true);
            for (int i = 1; i < pts.Length; i++) g.LineTo(pts[i]);
            g.EndFigure(true);
        }
        return geo;
    }

    public override bool HitTest(Point p, double tolerance)
    {
        if (Distance(p, Center) <= Radius * 1.2 + tolerance) return true;
        if (Shape == MarkerShape.Pin && Math.Abs(p.X - Center.X) <= Radius * 0.6 + tolerance &&
            p.Y >= Center.Y && p.Y <= Center.Y + Radius * 1.8 + tolerance) return true;
        return Tail is Point t && DistanceToSegment(p, Center, t) <= tolerance + 4;
    }

    public override void Offset(double dx, double dy)
    {
        Center = Shift(Center, dx, dy);
        if (Tail is Point t) Tail = Shift(t, dx, dy);
    }

    // Метки всегда непрозрачные — их должно быть хорошо видно.
    public override void ApplyStyle(AnnotationStyle s)
    {
        Color = Color.FromRgb(s.Color.R, s.Color.G, s.Color.B);
        FontSize = s.FontSize;
        Shape = s.MarkerShape;
    }

    public override void ReadStyle(AnnotationStyle s)
    {
        s.Color = Color;
        s.Opacity = 1;
        s.FontSize = FontSize;
        s.MarkerShape = Shape;
    }

    public override Annotation Clone() => CopyBaseTo(new MarkerAnnotation
    {
        Id = Id, Sequence = Sequence, Center = Center, Label = Label, Tail = Tail, FontSize = FontSize, Shape = Shape,
        LabelColor = LabelColor, CommentFont = CommentFont, CommentWeight = CommentWeight, CommentItalic = CommentItalic,
        CommentSize = CommentSize, CommentColor = CommentColor, CommentFrame = CommentFrame,
        CommentShadow = CommentShadow, CommentShadowColor = CommentShadowColor,
        CommentPlateColor = CommentPlateColor, CommentBorderColor = CommentBorderColor,
    });
}

// ═════════════════════════════ Комментарии прямо на картинке ═════════════════════════════
public sealed class CommentLabelStyle
{
    public bool Enabled { get; set; }
    public string FontFamily { get; set; } = "Segoe UI";
    public int FontWeight { get; set; } = 600;
    public bool Italic { get; set; }
    public double Size { get; set; } = 22;
    public Color Color { get; set; } = Color.Parse("#1C1C1E");
    public TextFrame Frame { get; set; } = TextFrame.Fill;
    public bool Shadow { get; set; } = true;
    public Color? ShadowColor { get; set; }
    public Color? PlateColor { get; set; }
    public Color? BorderColor { get; set; }
}

/// <summary>Подписи-комментарии рядом с метками (опция «Комментарии на картинке»). Стиль — у каждой метки свой.</summary>
public static class CommentLabels
{
    /// <summary>
    /// Где поставить подпись: перебираем места вокруг метки (сначала — со стороны, противоположной стрелке)
    /// и берём первое, где подпись не пересекает стрелку, другие метки и уже поставленные подписи
    /// и помещается в выделенную область. Если идеального места нет — место с наименьшими «штрафами».
    /// </summary>
    static Rect PlaceLabel(MarkerAnnotation m, double w, double h, double gap, Rect area,
        List<Rect> placed, List<MarkerAnnotation> markers)
    {
        var c = m.Center;
        double r = m.Radius;

        // Направление «от стрелки»: подпись лучше ставить с противоположной стороны.
        double ax = 1, ay = 0;
        if (m.Tail is Point tail && Annotation.Distance(tail, c) > r)
        {
            double dx = tail.X - c.X, dy = tail.Y - c.Y, len = Math.Sqrt(dx * dx + dy * dy);
            ax = -dx / len;
            ay = -dy / len;
        }

        // 12 направлений по кругу, начиная с «от стрелки»; ближе к нему — раньше в очереди.
        var candidates = new List<(Rect Rect, int Order)>();
        double baseAngle = Math.Atan2(ay, ax);
        for (int i = 0; i < 12; i++)
        {
            int step = (i + 1) / 2 * (i % 2 == 0 ? 1 : -1); // 0, -1, +1, -2, +2 …
            double a = baseAngle + step * Math.PI / 6;
            double ux = Math.Cos(a), uy = Math.Sin(a);
            // Точка у края метки по направлению, от неё прямоугольник «растёт» в ту же сторону.
            double px = c.X + ux * (r + gap), py = c.Y + uy * (r + gap);
            double x = px + (ux >= 0 ? 0 : -w) + (Math.Abs(ux) < 0.35 ? (ux >= 0 ? -w / 2 : w / 2) : 0);
            double y = py + (uy >= 0 ? 0 : -h) + (Math.Abs(uy) < 0.35 ? (uy >= 0 ? -h / 2 : h / 2) : 0);
            candidates.Add((new Rect(x, y, w, h), i));
        }

        Rect best = candidates[0].Rect;
        double bestScore = double.MaxValue;
        foreach (var (rect, order) in candidates)
        {
            double score = order * 0.5;
            var inflated = rect.Inflate(3);
            if (m.Tail is Point t && SegmentHitsRect(c, t, inflated)) score += 100;
            foreach (var other in markers)
            {
                if (ReferenceEquals(other, m)) continue;
                var otherRect = new Rect(other.Center.X - other.Radius, other.Center.Y - other.Radius, other.Radius * 2, other.Radius * 2);
                if (otherRect.Intersects(inflated)) score += 40;
                if (other.Tail is Point ot && SegmentHitsRect(other.Center, ot, inflated)) score += 25;
            }
            foreach (var p in placed)
                if (p.Intersects(inflated)) score += 60;
            // Вылезание за выделенную область — штраф пропорционально «вылезшей» части.
            var inside = rect.Intersect(area);
            double outside = w * h - Math.Max(0, inside.Width) * Math.Max(0, inside.Height);
            score += outside / Math.Max(1, w * h) * 80;

            if (score < bestScore)
            {
                bestScore = score;
                best = rect;
            }
        }

        // Если всё-таки вылезает — сдвигаем внутрь области.
        double bx = Math.Max(area.Left + 2, Math.Min(best.X, area.Right - w - 2));
        double by = Math.Max(area.Top + 2, Math.Min(best.Y, area.Bottom - h - 2));
        return new Rect(bx, by, w, h);
    }

    /// <summary>Пересекает ли отрезок прямоугольник (концы внутри или пересечение с любой стороной).</summary>
    static bool SegmentHitsRect(Point a, Point b, Rect r)
    {
        if (r.Contains(a) || r.Contains(b)) return true;
        var tl = r.TopLeft; var tr = r.TopRight; var bl = r.BottomLeft; var br = r.BottomRight;
        return SegmentsCross(a, b, tl, tr) || SegmentsCross(a, b, tr, br) ||
               SegmentsCross(a, b, br, bl) || SegmentsCross(a, b, bl, tl);
    }

    static bool SegmentsCross(Point p1, Point p2, Point p3, Point p4)
    {
        static double Cross(Point o, Point a, Point b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
        double d1 = Cross(p3, p4, p1), d2 = Cross(p3, p4, p2), d3 = Cross(p1, p2, p3), d4 = Cross(p1, p2, p4);
        return ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0));
    }

    /// <param name="area">Область выделения в пикселях снимка — подписи стараются не вылезать за неё.</param>
    public static void Render(DrawingContext ctx, AnnotationDocument doc, CommentLabelStyle st, Rect area)
    {
        if (!st.Enabled) return;
        var placed = new List<Rect>();
        var markers = doc.MarkersInOrder().ToList();
        foreach (var m in markers)
        {
            var text = doc.GetComment(m).Trim();
            if (text.Length == 0) continue;

            // Каждая метка хранит собственный стиль подписи.
            var size = m.CommentSize;
            var typeface = FontCatalog.Typeface(m.CommentFont, m.CommentWeight, m.CommentItalic);
            FormattedText Make(IBrush brush) =>
                new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, size, brush)
                {
                    MaxTextWidth = size * 20, // длинные комментарии переносятся
                };
            var ft = Make(Brushes.Black);

            double pad = TextPlate.Padding(m.CommentFrame, size);
            double w = ft.Width + pad * 2, h = ft.Height + pad * 2;
            double gap = m.Radius * 0.5 + 4;

            var rect = PlaceLabel(m, w, h, gap, area, placed, markers);
            placed.Add(rect);

            TextPlate.Draw(ctx, Make, rect, m.CommentColor, m.CommentFrame, size, m.Color,
                m.CommentShadow, m.CommentShadowColor, m.CommentPlateColor, m.CommentBorderColor);
        }
    }
}
