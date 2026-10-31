using Avalonia;

namespace SnapMark.Annotations;

/// <summary>
/// Распознавание фигур как в Markup на iOS/macOS: нарисуй от руки и задержи палец/мышь —
/// линия станет ровной, «кривой» круг — эллипсом, «кривой» прямоугольник — прямоугольником.
/// </summary>
public static class ShapeRecognizer
{
    public static Annotation? Recognize(FreehandAnnotation stroke)
    {
        var pts = stroke.Points;
        if (pts.Count < 4) return null;

        double length = 0;
        for (int i = 1; i < pts.Count; i++) length += Annotation.Distance(pts[i - 1], pts[i]);
        if (length < 24) return null;

        var first = pts[0];
        var last = pts[^1];

        // ── Прямая: все точки близко к отрезку «начало — конец».
        double chord = Annotation.Distance(first, last);
        if (chord > length * 0.82)
        {
            double maxDev = pts.Max(p => DistanceToLine(p, first, last));
            if (maxDev < Math.Max(6, chord * 0.07))
            {
                if (stroke.Kind == StrokeKind.Highlighter)
                {
                    var straight = (FreehandAnnotation)stroke.Clone();
                    straight.Points.Clear();
                    straight.Widths.Clear();
                    straight.Points.Add(first);
                    straight.Points.Add(last);
                    straight.Widths.Add(1);
                    straight.Widths.Add(1);
                    return straight;
                }
                return Copy(stroke, new LineAnnotation { Start = first, End = last });
            }
        }

        // Маркер-выделитель в фигуры не превращаем — только в прямую.
        if (stroke.Kind == StrokeKind.Highlighter) return null;

        // ── Замкнутые фигуры: конец рядом с началом.
        double minX = pts.Min(p => p.X), maxX = pts.Max(p => p.X);
        double minY = pts.Min(p => p.Y), maxY = pts.Max(p => p.Y);
        double w = maxX - minX, h = maxY - minY;
        if (w < 12 || h < 12) return null;
        if (chord > Math.Max(w, h) * 0.35) return null;

        double cx = (minX + maxX) / 2, cy = (minY + maxY) / 2, a = w / 2, b = h / 2;

        // Ошибка эллипса: насколько «нормированный радиус» отличается от 1.
        double ellipseErr = pts.Average(p =>
        {
            double nx = (p.X - cx) / a, ny = (p.Y - cy) / b;
            return Math.Abs(Math.Sqrt(nx * nx + ny * ny) - 1);
        });

        // Ошибка прямоугольника: расстояние до ближайшей стороны рамки.
        double rectErr = pts.Average(p =>
        {
            double d = Math.Min(Math.Min(p.X - minX, maxX - p.X), Math.Min(p.Y - minY, maxY - p.Y));
            return d / Math.Min(w, h);
        });

        var tl = new Point(minX, minY);
        var br = new Point(maxX, maxY);
        if (rectErr < 0.075 && rectErr < ellipseErr * 0.8)
            return Copy(stroke, new RectAnnotation { Start = tl, End = br });
        if (ellipseErr < 0.16)
            return Copy(stroke, new EllipseAnnotation { Start = tl, End = br });
        if (rectErr < 0.1)
            return Copy(stroke, new RectAnnotation { Start = tl, End = br });
        return null;
    }

    static Annotation Copy(FreehandAnnotation from, Annotation to)
    {
        to.Color = from.Color;
        to.Thickness = from.Kind == StrokeKind.Pencil ? Math.Max(1.5, from.Thickness * 0.55) : from.Thickness;
        to.Dashed = from.Dashed;
        return to;
    }

    static double DistanceToLine(Point p, Point a, Point b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1e-6) return Annotation.Distance(p, a);
        return Math.Abs(dy * p.X - dx * p.Y + b.X * a.Y - b.Y * a.X) / len;
    }
}
