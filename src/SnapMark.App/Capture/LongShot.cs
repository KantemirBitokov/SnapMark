using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SkiaSharp;

namespace SnapMark.Capture;

/// <summary>
/// Склейка кадров длинного скриншота. Каждый новый кадр сравнивается с предыдущим по «отпечаткам» строк:
///   • строки, одинаковые в обоих кадрах сверху и снизу, — закреплённые шапка и подвал (не дублируются);
///   • сдвиг ищется по совпадению содержательных строк (однотонные пустые строки не в счёт);
///   • в результат добавляются только новые строки, подвал переносится в конец.
/// </summary>
public sealed class Stitcher
{
    readonly int _width;
    readonly List<byte[]> _rows = new();      // результат по строкам (BGRA)
    ulong[]? _prevHash;
    bool[]? _prevInfo;

    public Stitcher(int width) => _width = width;

    public int Height => _rows.Count;
    public int Frames { get; private set; }

    /// <summary>Добавить кадр. Возвращает число новых строк: 0 — страница не сдвинулась,
    /// -1 — сдвинулась, но перекрытия с прошлым кадром не нашлось (прокрутили слишком далеко, анимация…).</summary>
    public int Add(SKBitmap frame)
    {
        if (frame.Width != _width) throw new ArgumentException("width");
        int h = frame.Height;
        var rows = ReadRows(frame);
        var (hash, info) = Fingerprints(rows, _width);
        Frames++;
        if (_prevHash is null)
        {
            _rows.AddRange(rows);
            _prevHash = hash;
            _prevInfo = info;
            return h;
        }

        var a = _prevHash;
        var ai = _prevInfo!;
        if (a.Length != h) throw new ArgumentException("height");
        int top = 0;
        while (top < h && a[top] == hash[top]) top++;
        if (top == h) return 0; // ничего не изменилось
        int bottom = 0;
        while (bottom < h - top && a[h - 1 - bottom] == hash[h - 1 - bottom]) bottom++;
        int zh = h - top - bottom;

        // Лучший сдвиг s: строка нового кадра top+i совпадает со строкой прошлого top+s+i.
        int bestS = -1;
        double bestScore = 0;
        int bestOverlap = 0;
        // Шапка, которая «прилипает» при прокрутке (GitHub и многие сайты), появляется сверху нового кадра поверх
        // содержимого — эти строки не совпадут ни при каком сдвиге. Поэтому сдвиг оценивается дважды: по всему
        // перекрытию и без верхней полосы нового кадра (до четверти высоты), — берётся лучшая оценка.
        int band = h / 4;
        for (int s = 1; s < zh; s++)
        {
            int overlap = zh - s;
            int skip = Math.Min(band, overlap / 2);
            int informative = 0, match = 0, infoLow = 0, matchLow = 0;
            for (int i = 0; i < overlap; i++)
            {
                if (!ai[top + s + i] && !info[top + i]) continue;
                bool eq = a[top + s + i] == hash[top + i];
                informative++;
                if (eq) match++;
                if (i >= skip)
                {
                    infoLow++;
                    if (eq) matchLow++;
                }
            }
            if (informative < Math.Min(8, overlap / 4 + 1)) continue;
            double score = match / (double)informative;
            if (infoLow >= Math.Min(8, overlap / 4 + 1)) score = Math.Max(score, matchLow / (double)infoLow);
            if (score > bestScore + 1e-9 || (Math.Abs(score - bestScore) < 1e-9 && informative > bestOverlap))
            {
                bestScore = score;
                bestS = s;
                bestOverlap = informative;
            }
        }

        // Не нашли надёжного перекрытия (анимация, всплывшая подсказка…) — кадр пропускаем и сравниваем
        // следующий с последним удачным: шаг прокрутки меньше высоты области, перекрытие найдётся.
        if (bestS <= 0 || bestScore < 0.8) return -1;
        int newRows = bestS;
        // Результат всегда заканчивается хвостом прошлого кадра, а его нижние bottom строк — тот же подвал,
        // что и у этого кадра: убираем их, добавляем новые строки и подвал заново (подвал остаётся в самом конце).
        if (bottom > 0) _rows.RemoveRange(_rows.Count - bottom, bottom);
        for (int i = top + zh - newRows; i < top + zh; i++) _rows.Add(rows[i]);
        for (int i = h - bottom; i < h; i++) _rows.Add(rows[i]);
        _prevHash = hash;
        _prevInfo = info;
        return newRows;
    }

    public SKBitmap Result()
    {
        var bmp = new SKBitmap(new SKImageInfo(_width, Math.Max(1, _rows.Count), SKColorType.Bgra8888, SKAlphaType.Premul));
        var ptr = bmp.GetPixels();
        int stride = bmp.RowBytes;
        for (int y = 0; y < _rows.Count; y++) Marshal.Copy(_rows[y], 0, ptr + y * stride, _width * 4);
        return bmp;
    }

    static List<byte[]> ReadRows(SKBitmap b)
    {
        using var converted = b.ColorType == SKColorType.Bgra8888 ? null : b.Copy(SKColorType.Bgra8888);
        var src = converted ?? b;
        var ptr = src.GetPixels();
        var rows = new List<byte[]>(src.Height);
        for (int y = 0; y < src.Height; y++)
        {
            var row = new byte[src.Width * 4];
            Marshal.Copy(ptr + y * src.RowBytes, row, 0, row.Length);
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>Отпечаток строки (FNV по слегка огрублённым пикселям) и «содержательность» (не однотонная).</summary>
    /// <remarks>
    /// Правый край (полоса прокрутки: ползунок двигается и появляется/гаснет) и узкая левая кромка в отпечаток
    /// не входят — иначе строки двух кадров никогда не совпадут.
    /// </remarks>
    static (ulong[] Hash, bool[] Informative) Fingerprints(List<byte[]> rows, int width)
    {
        int from = Math.Min(width / 50, 8) * 4, to = Math.Max(from + 4, (width - Math.Max(24, width / 20)) * 4);
        var hash = new ulong[rows.Count];
        var info = new bool[rows.Count];
        for (int y = 0; y < rows.Count; y++)
        {
            var r = rows[y];
            ulong hv = 1469598103934665603UL;
            int minL = 255, maxL = 0;
            for (int x = from; x < to && x < r.Length; x += 4)
            {
                byte b = (byte)(r[x] >> 3), g = (byte)(r[x + 1] >> 3), rr = (byte)(r[x + 2] >> 3);
                hv = (hv ^ b) * 1099511628211UL;
                hv = (hv ^ g) * 1099511628211UL;
                hv = (hv ^ rr) * 1099511628211UL;
                int l = (r[x] + r[x + 1] * 2 + r[x + 2]) >> 2;
                if (l < minL) minL = l;
                if (l > maxL) maxL = l;
            }
            hash[y] = hv;
            info[y] = maxL - minL > 24;
        }
        return (hash, info);
    }

    /// <summary>Avalonia Bitmap → SKBitmap (копия пикселей).</summary>
    public static SKBitmap ToSk(Bitmap bitmap)
    {
        int w = bitmap.PixelSize.Width, h = bitmap.PixelSize.Height;
        var sk = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
        bitmap.CopyPixels(new PixelRect(0, 0, w, h), sk.GetPixels(), sk.ByteCount, sk.RowBytes);
        return sk;
    }
}

/// <summary>Автопрокрутка колесом мыши в точке экрана (физические пиксели).</summary>
public static class AutoScroll
{
    public static bool IsSupported => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() || (OperatingSystem.IsLinux() && X11Supported());

    /// <summary>Прокрутить на notches «щелчков» колеса в точке (x, y): больше нуля — вниз, меньше — вверх.</summary>
    public static bool Scroll(int x, int y, int notches, double scaling)
    {
        try
        {
            if (OperatingSystem.IsWindows()) return Win(x, y, notches);
            if (OperatingSystem.IsMacOS()) return Mac(x / scaling, y / scaling, notches);
            if (OperatingSystem.IsLinux()) return X11(x, y, notches);
        }
        catch { }
        return false;
    }

    // ── Windows ──
    // Размер должен быть ровно sizeof(INPUT) (40 байт на x64), иначе SendInput молча ничего не делает.
    [StructLayout(LayoutKind.Sequential)] struct INPUT { public uint type; public MOUSEINPUT mi; }
    [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx, dy; public int mouseData; public uint dwFlags, time; public IntPtr extra; }
    [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);

    [SupportedOSPlatform("windows")]
    static bool Win(int x, int y, int notches)
    {
        SetCursorPos(x, y);
        var input = new INPUT { type = 0, mi = new MOUSEINPUT { dwFlags = 0x0800 /*WHEEL*/, mouseData = -120 * notches } };
        return SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>()) == 1;
    }

    // ── macOS ──
    const string CG = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    [StructLayout(LayoutKind.Sequential)] struct CGPoint { public double X, Y; }
    [DllImport(CG)] static extern int CGWarpMouseCursorPosition(CGPoint p);
    [DllImport(CG)] static extern IntPtr CGEventCreateScrollWheelEvent(IntPtr source, uint units, uint count, int wheel1);
    [DllImport(CG)] static extern void CGEventPost(uint tap, IntPtr evt);
    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")] static extern void CFRelease(IntPtr o);

    [SupportedOSPlatform("macos")]
    static bool Mac(double x, double y, int notches)
    {
        CGWarpMouseCursorPosition(new CGPoint { X = x, Y = y });
        var ev = CGEventCreateScrollWheelEvent(IntPtr.Zero, 1 /*line*/, 1, -3 * notches);
        if (ev == IntPtr.Zero) return false;
        CGEventPost(0 /*HID*/, ev);
        CFRelease(ev);
        return true;
    }

    // ── Linux (XTest) ──
    [DllImport("libX11.so.6")] static extern IntPtr XOpenDisplay(IntPtr name);
    [DllImport("libX11.so.6")] static extern int XFlush(IntPtr d);
    [DllImport("libXtst.so.6")] static extern int XTestFakeMotionEvent(IntPtr d, int screen, int x, int y, ulong delay);
    [DllImport("libXtst.so.6")] static extern int XTestFakeButtonEvent(IntPtr d, uint button, bool press, ulong delay);
    static IntPtr _display;

    static bool X11Supported()
    {
        try { return Platform.Linux.Xlib.IsX11Session && NativeLibrary.TryLoad("libXtst.so.6", out _); }
        catch { return false; }
    }

    static bool X11(int x, int y, int notches)
    {
        if (_display == IntPtr.Zero) _display = XOpenDisplay(IntPtr.Zero);
        if (_display == IntPtr.Zero) return false;
        XTestFakeMotionEvent(_display, -1, x, y, 0);
        uint button = notches >= 0 ? 5u : 4u; // 5 — вниз, 4 — вверх
        for (int i = 0; i < Math.Abs(notches); i++)
        {
            XTestFakeButtonEvent(_display, button, true, 0);
            XTestFakeButtonEvent(_display, button, false, 0);
        }
        XFlush(_display);
        return true;
    }
}
