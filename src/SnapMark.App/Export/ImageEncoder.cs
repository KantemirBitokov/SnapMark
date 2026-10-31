using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using SkiaSharp;
using SnapMark.Annotations;
using SnapMark.Core;

namespace SnapMark.Export;

/// <summary>Рендер итоговой картинки (вырезанная область + аннотации) пиксель-в-пиксель.</summary>
public static class ImageRenderer
{
    /// <param name="overlay">Дополнительная отрисовка поверх аннотаций (например, комментарии на картинке).</param>
    public static RenderTargetBitmap Render(Bitmap screenshot, PixelRect selection, IEnumerable<Annotation> annotations,
        Action<DrawingContext>? overlay = null)
    {
        var rtb = new RenderTargetBitmap(new PixelSize(selection.Width, selection.Height), new Vector(96, 96));
        using (var ctx = rtb.CreateDrawingContext())
        {
            // Снимок сделан с DPI 96, поэтому единицы DrawImage = физические пиксели.
            ctx.DrawImage(screenshot,
                new Rect(selection.X, selection.Y, selection.Width, selection.Height),
                new Rect(0, 0, selection.Width, selection.Height));

            using (ctx.PushTransform(Matrix.CreateTranslation(-selection.X, -selection.Y)))
            {
                foreach (var a in annotations) a.Render(ctx);
                overlay?.Invoke(ctx);
            }
        }
        return rtb;
    }
}

/// <summary>Кодирование PNG → JPG / WebP / BMP и DIB для буфера обмена (через SkiaSharp, он уже идёт с Avalonia).</summary>
public static class ImageEncoder
{
    public static byte[] ToPng(Bitmap bitmap)
    {
        using var ms = new MemoryStream();
        bitmap.Save(ms);
        return ms.ToArray();
    }

    public static string Extension(ImageFileFormat f) => f switch
    {
        ImageFileFormat.Jpg => "jpg",
        ImageFileFormat.WebP => "webp",
        ImageFileFormat.Bmp => "bmp",
        _ => "png",
    };

    public static ImageFileFormat? FromExtension(string? ext) => ext?.TrimStart('.').ToLowerInvariant() switch
    {
        "png" => ImageFileFormat.Png,
        "jpg" or "jpeg" => ImageFileFormat.Jpg,
        "webp" => ImageFileFormat.WebP,
        "bmp" => ImageFileFormat.Bmp,
        _ => null,
    };

    public static byte[] Encode(byte[] png, ImageFileFormat format, int quality)
    {
        if (format == ImageFileFormat.Png) return png;

        using var bgra = DecodeBgra(png);
        switch (format)
        {
            case ImageFileFormat.Jpg:
            case ImageFileFormat.WebP:
            {
                using var image = SKImage.FromBitmap(bgra);
                using var data = image.Encode(
                    format == ImageFileFormat.Jpg ? SKEncodedImageFormat.Jpeg : SKEncodedImageFormat.Webp,
                    Math.Clamp(quality, 1, 100));
                return data.ToArray();
            }
            case ImageFileFormat.Bmp:
                return WriteBmp(bgra);
            default:
                return png;
        }
    }

    /// <summary>CF_DIB: BITMAPINFOHEADER + 32-битные пиксели снизу вверх.</summary>
    public static byte[] ToDib(byte[] png)
    {
        using var bgra = DecodeBgra(png);
        var header = BuildInfoHeader(bgra.Width, bgra.Height);
        var pixels = BottomUpPixels(bgra);
        var result = new byte[header.Length + pixels.Length];
        header.CopyTo(result, 0);
        pixels.CopyTo(result, header.Length);
        return result;
    }

    static SKBitmap DecodeBgra(byte[] png)
    {
        var decoded = SKBitmap.Decode(png) ?? throw new InvalidOperationException(L.T("Не удалось декодировать изображение."));
        if (decoded.ColorType == SKColorType.Bgra8888) return decoded;
        var converted = decoded.Copy(SKColorType.Bgra8888);
        decoded.Dispose();
        return converted ?? throw new InvalidOperationException(L.T("Не удалось преобразовать изображение."));
    }

    static byte[] WriteBmp(SKBitmap bgra)
    {
        var info = BuildInfoHeader(bgra.Width, bgra.Height);
        var pixels = BottomUpPixels(bgra);
        int offset = 14 + info.Length;
        var file = new byte[offset + pixels.Length];
        file[0] = (byte)'B';
        file[1] = (byte)'M';
        BitConverter.GetBytes(file.Length).CopyTo(file, 2);
        BitConverter.GetBytes(offset).CopyTo(file, 10);
        info.CopyTo(file, 14);
        pixels.CopyTo(file, offset);
        return file;
    }

    static byte[] BuildInfoHeader(int width, int height)
    {
        var h = new byte[40];
        BitConverter.GetBytes(40).CopyTo(h, 0);                 // biSize
        BitConverter.GetBytes(width).CopyTo(h, 4);              // biWidth
        BitConverter.GetBytes(height).CopyTo(h, 8);             // biHeight > 0 → снизу вверх
        BitConverter.GetBytes((short)1).CopyTo(h, 12);          // biPlanes
        BitConverter.GetBytes((short)32).CopyTo(h, 14);         // biBitCount
        BitConverter.GetBytes(0).CopyTo(h, 16);                 // BI_RGB
        BitConverter.GetBytes(width * height * 4).CopyTo(h, 20);// biSizeImage
        BitConverter.GetBytes(3780).CopyTo(h, 24);              // ~96 DPI
        BitConverter.GetBytes(3780).CopyTo(h, 28);
        return h;
    }

    static byte[] BottomUpPixels(SKBitmap bgra)
    {
        int w = bgra.Width, h = bgra.Height, rowBytes = bgra.RowBytes, stride = w * 4;
        var src = bgra.Bytes;
        var dst = new byte[stride * h];
        for (int y = 0; y < h; y++)
            Buffer.BlockCopy(src, y * rowBytes, dst, (h - 1 - y) * stride, stride);
        return dst;
    }
}
