using System.Globalization;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace SnapMark.Capture;

/// <summary>
/// Курсор поворота — круговая стрелка, нарисованная при первом обращении.
/// В системном наборе Avalonia такого курсора нет, а без него режим поворота не видно.
/// </summary>
internal static class RotateCursor
{
    static Cursor? _cursor;
    static RenderTargetBitmap? _bitmap; // держим живым, пока жив курсор

    public static Cursor Create()
    {
        if (_cursor is not null) return _cursor;

        try
        {
            var size = new PixelSize(40, 40);
            _bitmap = new RenderTargetBitmap(size, new Vector(96, 96));

            using (var ctx = _bitmap.CreateDrawingContext())
            {
                var typeface = new Typeface(new FontFamily("Segoe UI Symbol, Segoe UI, DejaVu Sans, Arial"));
                var white = new FormattedText("↻", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 26, Brushes.White);
                var black = new FormattedText("↻", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 26, Brushes.Black);

                double x = (size.Width - white.Width) / 2;
                double y = (size.Height - white.Height) / 2;

                // Чёрная обводка: стрелка должна читаться и на светлом, и на тёмном снимке.
                Point[] outline =
                {
                    new(x - 1.2, y), new(x + 1.2, y), new(x, y - 1.2), new(x, y + 1.2),
                };
                foreach (var p in outline) ctx.DrawText(black, p);
                ctx.DrawText(white, new Point(x, y));
            }

            _cursor = new Cursor(_bitmap, new PixelPoint(20, 20));
        }
        catch
        {
            // Если платформа не умеет курсор из картинки — хотя бы «рука».
            _cursor = new Cursor(StandardCursorType.Hand);
        }

        return _cursor;
    }
}
