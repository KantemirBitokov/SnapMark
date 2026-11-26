using Avalonia;
using Avalonia.Media;

namespace SnapMark.Capture;

/// <summary>
/// Курсор поворота — дуга со стрелкой в стиле macOS. Головка сидит на касательной
/// в верхней точке окружности и продолжает дугу, поэтому не выглядит повёрнутой.
/// Рисуется прямо на холсте: чёткий на любом DPI и без фона.
/// </summary>
internal static class RotateGlyph
{
    const double Radius = 6.2;
    const double Stroke = 1.55;
    const double Start = 15;
    const double End = 270;
    const double HeadLength = 3.6, HeadHalf = 1.9, HeadBack = -0.2;

    public static void Draw(DrawingContext ctx, Point at)
    {
        var arc = Arc(at);
        var head = Head(at);

        var shadowBrush = new SolidColorBrush(Colors.Black, 0.55);
        var shadowPen = new Pen(shadowBrush, Stroke + 2.0, null, PenLineCap.Round, PenLineJoin.Round);
        var shadowHeadPen = new Pen(shadowBrush, Stroke * 0.4 + 2.0, null, PenLineCap.Round, PenLineJoin.Round);
        var whitePen = new Pen(Brushes.White, Stroke, null, PenLineCap.Round, PenLineJoin.Round);
        var whiteHeadPen = new Pen(Brushes.White, Stroke * 0.4, null, PenLineCap.Round, PenLineJoin.Round);

        // Сначала тёмная подложка (тень-обводка), затем белая линия и стрелка.
        ctx.DrawGeometry(null, shadowPen, arc);
        ctx.DrawGeometry(shadowBrush, shadowHeadPen, head);
        ctx.DrawGeometry(null, whitePen, arc);
        ctx.DrawGeometry(Brushes.White, whiteHeadPen, head);
    }

    static Point At(Point center, double degrees, double radius = Radius) =>
        new(center.X + radius * Math.Cos(degrees * Math.PI / 180), center.Y + radius * Math.Sin(degrees * Math.PI / 180));

    static StreamGeometry Arc(Point center)
    {
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(At(center, Start), false);
            g.ArcTo(At(center, End), new Size(Radius, Radius), 0, true, SweepDirection.Clockwise);
            g.EndFigure(false);
        }

        return geometry;
    }

    /// <summary>Треугольная головка: вершина по касательной, основание — поперёк дуги.</summary>
    static StreamGeometry Head(Point center)
    {
        double a = End * Math.PI / 180;
        var tangent = new Vector(-Math.Sin(a), Math.Cos(a));
        var normal = new Vector(Math.Cos(a), Math.Sin(a));
        var end = At(center, End);

        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(end + tangent * HeadLength, true);
            g.LineTo(end + tangent * HeadBack + normal * HeadHalf);
            g.LineTo(end + tangent * HeadBack - normal * HeadHalf);
            g.EndFigure(true);
        }

        return geometry;
    }
}
