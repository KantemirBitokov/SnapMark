using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SkiaSharp;

namespace SnapMark.Capture;

/// <summary>
/// Склейка кадров длинного скриншота. Кадр делится на ячейки (строка пикселей × 1/64 ширины), у каждой —
/// средняя яркость и разброс. Сдвиг между кадрами ищется по совпадению ячеек с допуском, поэтому склейку
/// не ломают:
///   • анимации (мерцающие звёзды, карусели, видео, мигающий курсор) — меняется малая доля ячеек;
///   • закреплённые шапка и подвал (одинаковые строки сверху и снизу) — не дублируются, подвал уходит в конец;
///   • шапка, которая появляется при прокрутке (GitHub и др.), — сдвиг оценивается и без верхней полосы кадра;
///   • неподвижные колонки (боковое меню, кнопка чата) — в оценке сдвига не участвуют;
///   • однообразное содержимое (звёздное небо, таблица из одинаковых строк) — при равных оценках берётся
///     сдвиг, ближайший к ожидаемому (сколько обычно даёт прокрутка).
/// </summary>
public sealed class Stitcher
{
    const int Blocks = 64;
    readonly int _width;
    readonly List<byte[]> _rows = new();      // результат по строкам (BGRA)
    byte[][]? _prev;                          // ячейки прошлого удачного кадра: [строка][2·блок] = яркость, разброс

    public Stitcher(int width) => _width = width;

    public int Height => _rows.Count;
    public int Frames { get; private set; }

    /// <summary>Отладка: оценки последнего сравнения.</summary>
    public string LastInfo { get; private set; } = "";

    /// <summary>
    /// Добавить кадр. Возвращает число новых строк: 0 — страница не сдвинулась, -1 — сдвинулась, но надёжного
    /// перекрытия с прошлым кадром нет (прокрутили слишком далеко и т. п.). expected — ожидаемый сдвиг (0 — неизвестен).
    /// </summary>
    public int Add(SKBitmap frame, int expected = 0)
    {
        if (frame.Width != _width) throw new ArgumentException("width");
        int h = frame.Height;
        var rows = ReadRows(frame);
        var cur = Cells(rows, _width);
        Frames++;
        if (_prev is null)
        {
            _rows.AddRange(rows);
            _prev = cur;
            return h;
        }
        var a = _prev;
        if (a.Length != h) throw new ArgumentException("height");

        // Неподвижные строки сверху и снизу (закреплённые шапка и подвал).
        var all = Enumerable.Repeat(true, Blocks).ToArray();
        int top = 0;
        while (top < h && RowSame(a[top], cur[top])) top++;
        if (top == h) { LastInfo = "same"; return 0; }
        int bottom = 0;
        while (bottom < h - top && RowSame(a[h - 1 - bottom], cur[h - 1 - bottom])) bottom++;
        int zh = h - top - bottom;

        // Какие колонки ячеек изменились на месте (s = 0). Неподвижные (меню сбоку, фон, плавающие кнопки)
        // в поиске сдвига не участвуют. Если изменилась только малая часть (анимация) — страница стоит.
        var moving = new bool[Blocks];
        int evidence = 0, movingCount = 0;
        for (int j = 0; j < Blocks; j++)
        {
            int counted = 0, matched = 0;
            for (int y = top; y < top + zh; y++)
                Cell(a[y], cur[y], j, ref counted, ref matched);
            if (counted == 0) continue;
            evidence++;
            if (matched < counted * 0.95) { moving[j] = true; movingCount++; }
        }
        if (evidence == 0 || movingCount < Math.Max(2, evidence * 0.15)) { LastInfo = $"anim moving={movingCount}/{evidence}"; return 0; }

        var (score0, _, _, _) = Score(a, cur, top, zh, 0, moving, 0);
        int band = h / 4;
        int bestS = -1, bestCounted = 0;
        double bestScore = 0;
        var scores = new double[zh];
        var counts = new int[zh];
        for (int s = 1; s < zh; s++)
        {
            int overlap = zh - s;
            var (full, cf, low, cl) = Score(a, cur, top, zh, s, moving, Math.Min(band, overlap / 2));
            double sc = cf >= 24 ? full : 0;
            if (cl >= 24 && low > sc) sc = low;
            scores[s] = sc;
            counts[s] = Math.Max(cf, cl);
            if (sc > bestScore) { bestScore = sc; bestS = s; }
        }
        if (bestS > 0)
        {
            // Почти равные оценки (однообразное содержимое) — ближе к ожидаемому сдвигу, иначе — с большим перекрытием.
            for (int s = 1; s < zh; s++)
            {
                if (scores[s] < bestScore - 0.03) continue;
                bool better = expected > 0
                    ? Math.Abs(s - expected) < Math.Abs(bestS - expected)
                    : counts[s] > bestCounted;
                if (better || s == bestS) { bestS = s; bestCounted = counts[s]; }
            }
        }
        LastInfo = $"s={bestS} score={bestScore:0.00} s0={score0:0.00} moving={movingCount}/{evidence} expected={expected}";
        if (bestS <= 0 || bestScore < 0.6 || bestScore < score0 + 0.05)
            return score0 >= 0.6 ? 0 : -1;

        int newRows = bestS;
        // Результат всегда заканчивается хвостом прошлого кадра, а его нижние bottom строк — тот же подвал,
        // что и у этого кадра: убираем их, добавляем новые строки и подвал заново (подвал остаётся в самом конце).
        if (bottom > 0) _rows.RemoveRange(_rows.Count - bottom, bottom);
        for (int i = top + zh - newRows; i < top + zh; i++) _rows.Add(rows[i]);
        for (int i = h - bottom; i < h; i++) _rows.Add(rows[i]);
        _prev = cur;
        return newRows;
    }

    /// <summary>
    /// Доля совпавших ячеек: строка нового кадра top+i против строки прошлого top+s+i — по всему перекрытию
    /// и без верхней полосы нового кадра (i ≥ skip, там могла появиться прилипающая шапка).
    /// </summary>
    static (double Full, int CountedFull, double Low, int CountedLow) Score(byte[][] a, byte[][] b, int top, int zh, int s, bool[] moving, int skip)
    {
        int cHigh = 0, mHigh = 0, cLow = 0, mLow = 0;
        for (int i = 0; i < zh - s; i++)
        {
            var ra = a[top + s + i];
            var rb = b[top + i];
            if (i < skip)
                for (int j = 0; j < Blocks; j++) { if (moving[j]) Cell(ra, rb, j, ref cHigh, ref mHigh); }
            else
                for (int j = 0; j < Blocks; j++) { if (moving[j]) Cell(ra, rb, j, ref cLow, ref mLow); }
        }
        int c = cHigh + cLow, m = mHigh + mLow;
        return (c == 0 ? 0 : m / (double)c, c, cLow == 0 ? 0 : mLow / (double)cLow, cLow);
    }

    static void Cell(byte[] ra, byte[] rb, int j, ref int counted, ref int matched)
    {
        int ma = ra[2 * j], va = ra[2 * j + 1], mb = rb[2 * j], vb = rb[2 * j + 1];
        int dm = Math.Abs(ma - mb);
        if (va <= 8 && vb <= 8 && dm <= 4) return; // обе ячейки ровные и одинаковые — ни за, ни против
        counted++;
        if (dm <= 5 && Math.Abs(va - vb) <= 16) matched++;
    }

    static bool RowSame(byte[] a, byte[] b)
    {
        int counted = 0, matched = 0;
        for (int j = 0; j < Blocks; j++) Cell(a, b, j, ref counted, ref matched);
        return counted - matched <= 1;
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

    /// <summary>Ячейки строки: средняя яркость и разброс (max − min) по 64 полосам ширины.</summary>
    /// <remarks>
    /// Правый край (полоса прокрутки: ползунок двигается и появляется/гаснет) и узкая левая кромка не входят —
    /// иначе строки двух кадров никогда не совпадут.
    /// </remarks>
    static byte[][] Cells(List<byte[]> rows, int width)
    {
        int from = Math.Min(width / 50, 8), to = Math.Max(from + Blocks, width - Math.Max(24, width / 20));
        to = Math.Min(to, width);
        var result = new byte[rows.Count][];
        for (int y = 0; y < rows.Count; y++)
        {
            var r = rows[y];
            var c = new byte[2 * Blocks];
            for (int j = 0; j < Blocks; j++)
            {
                int x0 = from + (to - from) * j / Blocks, x1 = Math.Max(x0 + 1, from + (to - from) * (j + 1) / Blocks);
                int sum = 0, min = 255, max = 0;
                for (int x = x0; x < x1; x++)
                {
                    int p = x * 4;
                    int l = (r[p] + r[p + 1] * 2 + r[p + 2]) >> 2;
                    sum += l;
                    if (l < min) min = l;
                    if (l > max) max = l;
                }
                c[2 * j] = (byte)(sum / (x1 - x0));
                c[2 * j + 1] = (byte)(max - min);
            }
            result[y] = c;
        }
        return result;
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
