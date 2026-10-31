using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using static SnapMark.Platform.Windows.Native;

namespace SnapMark.Platform.Windows;

/// <summary>
/// Захват через GDI BitBlt — быстро, без зависимостей, работает на Win10/11.
/// TODO (этап 2): Windows.Graphics.Capture для HDR-мониторов и защищённого контента.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WinScreenCapture : IScreenCapture
{
    public Bitmap? Capture(PixelRect r)
    {
        if (r.Width <= 0 || r.Height <= 0) return null;

        IntPtr screenDc = GetDC(IntPtr.Zero);
        IntPtr memDc = CreateCompatibleDC(screenDc);
        IntPtr hBmp = CreateCompatibleBitmap(screenDc, r.Width, r.Height);
        IntPtr old = SelectObject(memDc, hBmp);
        try
        {
            if (!BitBlt(memDc, 0, 0, r.Width, r.Height, screenDc, r.X, r.Y, SRCCOPY | CAPTUREBLT))
                return null;

            // Битмап нельзя держать выбранным в DC во время GetDIBits.
            SelectObject(memDc, old);

            var bmi = new BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = r.Width,
                biHeight = -r.Height, // отрицательная высота = строки сверху вниз
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0, // BI_RGB
            };
            var pixels = new byte[r.Width * r.Height * 4];
            if (GetDIBits(memDc, hBmp, 0, (uint)r.Height, pixels, ref bmi, DIB_RGB_COLORS) == 0)
                return null;

            // GDI оставляет альфу нулевой — делаем изображение непрозрачным.
            for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;

            var wb = new WriteableBitmap(new PixelSize(r.Width, r.Height), new Vector(96, 96),
                PixelFormat.Bgra8888, AlphaFormat.Opaque);
            using (var fb = wb.Lock())
            {
                int stride = r.Width * 4;
                for (int y = 0; y < r.Height; y++)
                    Marshal.Copy(pixels, y * stride, fb.Address + y * fb.RowBytes, stride);
            }
            return wb;
        }
        finally
        {
            SelectObject(memDc, old);
            DeleteObject(hBmp);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }
}
