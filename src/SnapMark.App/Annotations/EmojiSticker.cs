using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace SnapMark.Annotations;

/// <summary>
/// Эмодзи-стикер картинкой (набор Apple): рисуется изображением, а не символом шрифта,
/// поэтому выглядит одинаково на Windows, macOS и Linux. Пока картинка не скачалась —
/// показываем обычный символ, чтобы стикер не мигал пустым квадратом.
/// </summary>
public sealed class EmojiStickerAnnotation : Annotation
{
    public string Text { get; set; } = "";
    public Bitmap? Image { get; set; }
    public Point Position { get; set; }
    public double Size { get; set; } = 96;

    public override Rect Bounds => new(Position.X, Position.Y, Size, Size);

    public override void Render(DrawingContext ctx)
    {
        var frame = Bounds;
        if (Image is not null)
        {
            ctx.DrawImage(Image, frame);
            return;
        }

        var typeface = new Typeface(new FontFamily("Segoe UI Emoji, Apple Color Emoji, Noto Color Emoji"));
        var ft = new FormattedText(Text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            typeface, Size * 0.7, Brushes.Black);
        ctx.DrawText(ft, new Point(frame.X + (frame.Width - ft.Width) / 2, frame.Y + (frame.Height - ft.Height) / 2));
    }

    public override void Offset(double dx, double dy) => Position = new Point(Position.X + dx, Position.Y + dy);

    public override Annotation Clone() => CopyBaseTo(new EmojiStickerAnnotation
    {
        Text = Text,
        Image = Image,
        Position = Position,
        Size = Size,
    });
}
