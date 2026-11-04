using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia.Controls;
using SnapMark.Platform.Unix;

namespace SnapMark.Platform.Linux;

/// <summary>
/// Linux (X11): окно, сквозь которое проходит мышь, — пустая «форма ввода» через расширение XShape.
/// Пустая форма — события мыши уходят окнам под ним; сброс формы (None) — окно снова принимает мышь.
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed class X11WindowHelper : NoWindowHelper
{
    const string LibXext = "libXext.so.6";
    const int ShapeInput = 2, ShapeSet = 0, Unsorted = 0;

    [DllImport(LibXext)]
    static extern void XShapeCombineRectangles(IntPtr display, IntPtr window, int destKind, int xOff, int yOff,
        IntPtr rectangles, int count, int op, int ordering);

    [DllImport(LibXext)]
    static extern void XShapeCombineMask(IntPtr display, IntPtr window, int destKind, int xOff, int yOff, IntPtr pixmap, int op);

    [DllImport("libX11.so.6")] static extern IntPtr XOpenDisplay(IntPtr name);
    [DllImport("libX11.so.6")] static extern int XCloseDisplay(IntPtr display);
    [DllImport("libX11.so.6")] static extern int XFlush(IntPtr display);

    public override bool SetClickThrough(Window window, bool enabled)
    {
        try
        {
            var handle = window.TryGetPlatformHandle();
            if (handle is null || handle.Handle == IntPtr.Zero) return false;
            // Отдельное подключение к тому же X-серверу: менять форму чужого (Avalonia) окна X11 разрешает.
            var display = XOpenDisplay(IntPtr.Zero);
            if (display == IntPtr.Zero) return false;
            try
            {
                if (enabled) XShapeCombineRectangles(display, handle.Handle, ShapeInput, 0, 0, IntPtr.Zero, 0, ShapeSet, Unsorted);
                else XShapeCombineMask(display, handle.Handle, ShapeInput, 0, 0, IntPtr.Zero, ShapeSet);
                XFlush(display);
                return true;
            }
            finally
            {
                XCloseDisplay(display);
            }
        }
        catch
        {
            return false; // нет libXext или Wayland без XWayland
        }
    }
}
