using Avalonia;
using Avalonia.Media;

namespace SnapMark.Capture;

/// <summary>
/// Курсор поворота — дуга со стрелкой в стиле macOS: белая линия с тёмной обводкой,
/// без фона и без пиксельной «лесенки». Рисуется прямо на холсте, поэтому чёткий на любом DPI.
/// </summary>
internal static class RotateGlyph
{
    const double Radius = 6.2;
    const double Sweep = 275;
    const double Start = -55;

    public static void Draw(DrawingContext ctx, Point at)
    {
        var arc = Arc(at, out var end, out double endAngle);

        var shadow = new SolidColorBrush(Colors.Black, 0.5);
        var shadowPen = new Pen(shadow, 3.5, null, PenLineCap.Round);
        var whitePen = new Pen(Brushes.White, 1.55, null, PenLineCap.Round);

        // Сначала тёмная подложка (тень-обводка), затем белая линия и стрелка.
        ctx.DrawGeometry(null, shadowPen, arc);
        ctx.DrawGeometry(shadow, null, Head(end, endAngle, 1.5));
        ctx.DrawGeometry(null, whitePen, arc);
        ctx.DrawGeometry(Brushes.White, null, Head(end, endAngle, 1.0));
    }

    static StreamGeometry Arc(Point center, out Point end, out double endAngle)
    {
        endAngle = Start + Sweep;
        var start = At(center, Start);
        end = At(center, endAngle);

        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(start, false);
            g.ArcTo(end, new Size(Radius, Radius), 0, Sweep > 180, SweepDirection.Clockwise);
            g.EndFigure(false);
        }

        return geometry;
    }

    static Point At(Point center, double degrees)
    {
        double a = degrees * Math.PI / 180;
        return new Point(center.X + Radius * Math.Cos(a), center.Y + Radius * Math.Sin(a));
    }

    static StreamGeometry Head(Point end, double angleDeg, double scale)
    {
        double a = angleDeg * Math.PI / 180;
        var tangent = new Vector(-Math.Sin(a), Math.Cos(a)) * 5.4 * scale;
        var normal = new Vector(Math.Cos(a), Math.Sin(a)) * 2.3 * scale;
        var back = new Vector(-Math.Sin(a), Math.Cos(a)) * 0.55 * scale;

        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(end + tangent, true);
            g.LineTo(end + back + normal);
            g.LineTo(end + back - normal);
            g.EndFigure(true);
        }

        return geometry;
    }
}
