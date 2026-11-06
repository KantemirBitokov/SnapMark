using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SkiaSharp;

namespace SnapMark.Capture;

/// <summary>
/// Склейка кадров длинного скриншота. Кадр делится на ячейки (строка пикселей × 1/64 ширины). Сдвиг между
/// кадрами ищется в три ступени:
///   1. точное совпадение ячеек (отпечаток пикселей) — надёжно для обычных страниц, различает даже похожие строки
///      списка; анимации (карусели, видео, курсор) портят лишь свои ячейки, остальные совпадают;
///   2. мягкое совпадение — та же яркость и разброс или то же положение самой светлой и тёмной точки:
///      выдерживает мерцание (звёздное небо, переливы), когда точных совпадений мало;
///   3. если содержимое совсем не за что зацепить, а прокрутка стабильно даёт одно и то же число пикселей —
///      берётся этот ожидаемый сдвиг.
/// Кроме того: закреплённые шапка и подвал не дублируются (подвал уходит в конец); шапка, появляющаяся при
/// прокрутке (GitHub), не мешает — оценка идёт и без верхней полосы кадра; неподвижные колонки (меню сбоку,
/// плавающие кнопки, всплывшее уведомление) в поиске сдвига не участвуют.
/// </summary>
public sealed class Stitcher
{
    const int Blocks = 64;
    readonly int _width;
    readonly List<byte[]> _rows = new();      // результат по строкам (BGRA)
    Row[]? _prev;                             // ячейки прошлого удачного кадра
    int _lastBand;                            // сколько нижних строк результата — «подвал» прошлого кадра

    /// <summary>Ячейки одной строки: средняя яркость, разброс, положение самой светлой и тёмной точки, отпечаток.</summary>
    sealed class Row
    {
        public readonly byte[] M = new byte[Blocks], V = new byte[Blocks], Hi = new byte[Blocks], Lo = new byte[Blocks];
        public readonly ushort[] H = new ushort[Blocks];
    }

    public Stitcher(int width) => _width = width;

    public int Height => _rows.Count;
    public int Frames { get; private set; }

    /// <summary>Отладка: оценки последнего сравнения.</summary>
    public string LastInfo { get; private set; } = "";

    /// <summary>
    /// Добавить кадр. Возвращает число новых строк: 0 — страница не сдвинулась, -1 — сдвинулась, но надёжного
    /// перекрытия с прошлым кадром нет. expected — ожидаемый сдвиг (0 — неизвестен); trustExpected — прокрутка
    /// стабильна, и если сопоставить нечего (однородная или сплошь анимированная область), можно взять его.
    /// </summary>
    public int Add(SKBitmap frame, int expected = 0, bool trustExpected = false)
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
        int top = 0;
        while (top < h && RowSame(a[top], cur[top])) top++;
        if (top == h) { LastInfo = "same"; return 0; }
        int bottom = 0;
        while (bottom < h - top && RowSame(a[h - 1 - bottom], cur[h - 1 - bottom])) bottom++;
        int zh = h - top - bottom;
        // Изменилась лишь узкая полоса (всплыло уведомление, мигнула кнопка) — страница не сдвинулась.
        if (zh < h / 10) { LastInfo = $"small change {zh} rows"; return 0; }

        // Какие колонки ячеек изменились на месте. Неподвижные (меню сбоку, фон, плавающие кнопки, уведомления)
        // в поиске сдвига не участвуют. Изменилась только малая часть (анимация на месте) — страница стоит.
        var moving = new bool[Blocks];
        int evidence = 0, movingCount = 0;
        for (int j = 0; j < Blocks; j++)
        {
            int counted = 0, matched = 0;
            for (int y = top; y < top + zh; y++)
                if (Evidence(a[y], cur[y], j)) { counted++; if (Soft(a[y], cur[y], j)) matched++; }
            if (counted == 0) continue;
            evidence++;
            if (matched < counted * 0.95) { moving[j] = true; movingCount++; }
        }
        if (evidence == 0 || movingCount < Math.Max(2, evidence * 0.15)) { LastInfo = $"still moving={movingCount}/{evidence}"; return 0; }

        var e = new double[zh];
        var t = new double[zh];
        var cnt = new int[zh];
        int band = h / 4;
        for (int sh = 0; sh < zh; sh++)
        {
            int overlap = zh - sh;
            var r = Score(a, cur, top, zh, sh, moving, sh == 0 ? 0 : Math.Min(band, overlap / 2));
            e[sh] = r.Exact;
            t[sh] = r.Soft;
            cnt[sh] = r.Counted;
        }

        int Pick(double[] sc, double tie, bool confident)
        {
            int best = -1;
            for (int sh = 1; sh < zh; sh++)
                if (cnt[sh] >= 24 && (best < 0 || sc[sh] > sc[best])) best = sh;
            if (best < 0) return -1;
            // Почти равные оценки (повторяющееся содержимое) — ближе к ожидаемому сдвигу, иначе — с большим перекрытием.
            double bestScore = sc[best];
            // Уверенное совпадение (почти всё сошлось) — ожидаемый сдвиг не навязываем: в конце страницы прокрутка короче.
            if (confident && bestScore >= 0.85) tie = Math.Min(tie, 0.005);
            int top1 = best;
            for (int sh = 1; sh < zh; sh++)
            {
                if (cnt[sh] < 24 || sc[sh] < bestScore - tie || sh == top1) continue;
                bool better = expected > 0 ? Math.Abs(sh - expected) < Math.Abs(best - expected) : cnt[sh] > cnt[best];
                if (better) best = sh;
            }
            return best;
        }

        // Прокрутка стабильна — ожидаемому сдвигу доверяем сильнее: среди близких оценок (большие анимации вроде
        // карусели во весь экран портят и верный сдвиг) берём ближайший к нему. Сдвиг ещё не известен — строже порог.
        // Сдвиг совпал с ожидаемым — достаточно и более слабого подтверждения (дробный масштаб, сглаживание шрифтов).
        bool near(int sh) => expected > 0 && Math.Abs(sh - expected) <= Math.Max(3, expected / 50);
        double tie = trustExpected ? 0.15 : 0.005;
        int s = Pick(e, tie, true);
        string how = "exact";
        if (s < 0 || e[s] < (near(s) ? 0.35 : 0.5) || e[s] < e[0] + 0.1)
        {
            s = Pick(t, trustExpected ? 0.15 : 0.01, false);
            how = "soft";
            if (s < 0 || t[s] < (near(s) ? 0.55 : expected > 0 ? 0.7 : 0.8) || t[s] < t[0] + 0.1)
            {
                s = -1;
                how = "none";
            }
        }
        if (how == "none")
        {
            int be = 1, bt = 1;
            for (int sh = 1; sh < zh; sh++) { if (e[sh] > e[be]) be = sh; if (t[sh] > t[bt]) bt = sh; }
            how = $"none(bestE {be}:{e[be]:0.00}/{cnt[be]} bestT {bt}:{t[bt]:0.00}/{cnt[bt]} top={top} bottom={bottom})";
        }
        LastInfo = $"{how} s={s} e={(s > 0 ? e[s] : 0):0.00} t={(s > 0 ? t[s] : 0):0.00} e0={e[0]:0.00} t0={t[0]:0.00} moving={movingCount}/{evidence} expected={expected}";
        if (s < 0)
        {
            if (t[0] >= 0.7 || e[0] >= 0.5) return 0; // на месте — значит, не сдвинулось
            if (trustExpected && expected > 0 && expected < zh)
            {
                s = expected;
                LastInfo += " → blind";
            }
            else return -1;
        }

        // «Подвал» этого кадра: закреплённые строки снизу плюс полоса, где поверх страницы висит что-то
        // неподвижное (кнопка чата, «наверх», окно cookies, уведомление Windows). Его содержимое не добавляем
        // сейчас — страница под ним покажется выше в следующем кадре, а сам подвал окажется в результате один раз, в конце.
        int footer = Math.Min(Math.Max(bottom, OverlayBand(a, cur, s, bottom)), h / 3);
        // Подвал не должен «съесть» новые строки: иначе часть страницы между кадрами пропадёт.
        footer = Math.Max(bottom, Math.Min(footer, h - top - s));
        // Результат заканчивается хвостом прошлого кадра (строки прошлого кадра до самого низа). Убираем столько,
        // сколько было подвалом там или здесь, и добавляем страницу с того места, где она в прошлом кадре кончилась.
        int remove = Math.Min(Math.Max(_lastBand, footer), _rows.Count);
        int start = Math.Max(top, h - remove - s);
        _rows.RemoveRange(_rows.Count - remove, remove);
        for (int i = start; i < h; i++) _rows.Add(rows[i]);
        _lastBand = footer;
        _prev = cur;
        return s;
    }

    /// <summary>
    /// Высота полосы снизу, где поверх прокручиваемой страницы висит неподвижный элемент: ячейки на том же месте
    /// совпадают в обоих кадрах, хотя страница под ними сдвинулась на s. Вертикальные линии (рамки таблиц) и
    /// повторяющееся содержимое не в счёт. Ищем в нижних 40 % кадра.
    /// </summary>
    static int OverlayBand(Row[] a, Row[] b, int s, int bottom)
    {
        int h = b.Length, limit = h * 3 / 5, clusterTop = -1, lastMarked = -1;
        for (int y = h - 1 - bottom; y >= limit; y--)
        {
            bool marked = false;
            for (int j = 0; j < Blocks && !marked; j++)
            {
                if (b[y].H[j] != a[y].H[j] || (b[y].V[j] <= 8 && a[y].V[j] <= 8)) continue;
                if (y > 0 && b[y - 1].H[j] == b[y].H[j] && a[y - 1].H[j] == a[y].H[j]) continue; // вертикальная линия
                if (y + s < h && a[y + s].H[j] == b[y].H[j]) continue;                         // то же и после сдвига
                marked = true;
            }
            if (!marked) continue;
            if (lastMarked >= 0 && lastMarked - y > 48) break;
            clusterTop = lastMarked = y;
        }
        return clusterTop < 0 ? 0 : h - clusterTop;
    }

    /// <summary>
    /// Доли совпавших ячеек (точно и мягко): строка нового кадра top+i против строки прошлого top+s+i.
    /// Берётся лучшее из двух — по всему перекрытию и без верхней полосы нового кадра (i ≥ skip),
    /// где могла появиться прилипающая шапка.
    /// </summary>
    static (double Exact, double Soft, int Counted) Score(Row[] a, Row[] b, int top, int zh, int s, bool[] moving, int skip)
    {
        int cAll = 0, eAll = 0, tAll = 0, cLow = 0, eLow = 0, tLow = 0;
        for (int i = 0; i < zh - s; i++)
        {
            var ra = a[top + s + i];
            var rb = b[top + i];
            bool low = i >= skip;
            for (int j = 0; j < Blocks; j++)
            {
                if (!moving[j] || !Evidence(ra, rb, j)) continue;
                bool ex = ra.H[j] == rb.H[j];
                bool so = ex || Soft(ra, rb, j);
                cAll++; if (ex) eAll++; if (so) tAll++;
                if (low) { cLow++; if (ex) eLow++; if (so) tLow++; }
            }
        }
        double ea = cAll == 0 ? 0 : eAll / (double)cAll, ta = cAll == 0 ? 0 : tAll / (double)cAll;
        if (cLow >= 24 && cLow < cAll)
        {
            ea = Math.Max(ea, eLow / (double)cLow);
            ta = Math.Max(ta, tLow / (double)cLow);
        }
        return (ea, ta, cAll);
    }

    /// <summary>Обе ячейки ровные и одинаковые — ни за, ни против.</summary>
    static bool Evidence(Row a, Row b, int j) => !(a.V[j] <= 8 && b.V[j] <= 8 && Math.Abs(a.M[j] - b.M[j]) <= 4);

    /// <summary>Мягкое совпадение: та же яркость и разброс — или те же места самой светлой и тёмной точки (мерцание).</summary>
    static bool Soft(Row a, Row b, int j)
    {
        if (a.H[j] == b.H[j]) return true;
        if (Math.Abs(a.M[j] - b.M[j]) <= 5 && Math.Abs(a.V[j] - b.V[j]) <= 16) return true;
        return a.V[j] > 24 && b.V[j] > 24 && Math.Abs(a.Hi[j] - b.Hi[j]) <= 1 && Math.Abs(a.Lo[j] - b.Lo[j]) <= 1;
    }

    /// <summary>
    /// Строка на месте (для закреплённых шапки и подвала): почти все ячейки совпали. Допуск — на плавающие
    /// поверх них кнопки, уведомления и анимацию, которые портят отдельные ячейки.
    /// </summary>
    static bool RowSame(Row a, Row b)
    {
        int diff = 0, ev = 0;
        for (int j = 0; j < Blocks; j++)
        {
            if (!Evidence(a, b, j)) continue;
            ev++;
            if (a.H[j] != b.H[j] && !Soft(a, b, j)) diff++;
        }
        return diff <= Math.Max(1, ev / 10);
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

    /// <summary>Ячейки строки по 64 полосам ширины.</summary>
    /// <remarks>
    /// Правый край (полоса прокрутки: ползунок двигается и появляется/гаснет) и узкая левая кромка не входят —
    /// иначе строки двух кадров никогда не совпадут.
    /// </remarks>
    static Row[] Cells(List<byte[]> rows, int width)
    {
        int from = Math.Min(width / 50, 8), to = Math.Max(from + Blocks, width - Math.Max(24, width / 20));
        to = Math.Min(to, width);
        var result = new Row[rows.Count];
        for (int y = 0; y < rows.Count; y++)
        {
            var r = rows[y];
            var c = new Row();
            for (int j = 0; j < Blocks; j++)
            {
                int x0 = from + (to - from) * j / Blocks, x1 = Math.Max(x0 + 1, from + (to - from) * (j + 1) / Blocks);
                int sum = 0, min = 256, max = -1, hi = 0, lo = 0;
                uint hv = 2166136261;
                for (int x = x0; x < x1; x++)
                {
                    int p = x * 4;
                    int l = (r[p] + r[p + 1] * 2 + r[p + 2]) >> 2;
                    sum += l;
                    if (l < min) { min = l; lo = x - x0; }
                    if (l > max) { max = l; hi = x - x0; }
                    hv = (hv ^ (uint)(r[p] >> 3)) * 16777619;
                    hv = (hv ^ (uint)(r[p + 1] >> 3)) * 16777619;
                    hv = (hv ^ (uint)(r[p + 2] >> 3)) * 16777619;
                }
                c.M[j] = (byte)(sum / (x1 - x0));
                c.V[j] = (byte)(max - min);
                c.Hi[j] = (byte)Math.Min(hi, 255);
                c.Lo[j] = (byte)Math.Min(lo, 255);
                c.H[j] = (ushort)(hv ^ (hv >> 16));
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
    [StructLayout(LayoutKind.Sequential)] struct WPOINT { public int X, Y; }
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(WPOINT p);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint a, uint b, bool attach);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();

    [SupportedOSPlatform("windows")]
    static bool Win(int x, int y, int notches)
    {
        // Если в Windows выключено «Прокручивать неактивные окна», колесо уходит активному окну, а не тому,
        // что под курсором, — делаем окно под точкой активным (только чужое: свои панели не трогаем).
        var root = GetAncestor(WindowFromPoint(new WPOINT { X = x, Y = y }), 2 /*GA_ROOT*/);
        var fg = GetForegroundWindow();
        if (root != IntPtr.Zero && root != fg)
        {
            GetWindowThreadProcessId(root, out var pid);
            if (pid != (uint)Environment.ProcessId)
            {
                // Windows разрешает менять активное окно не всем: «присоединяемся» к потоку текущего активного окна.
                uint fgThread = fg == IntPtr.Zero ? 0 : GetWindowThreadProcessId(fg, out _), me = GetCurrentThreadId();
                bool attached = fgThread != 0 && fgThread != me && AttachThreadInput(me, fgThread, true);
                SetForegroundWindow(root);
                if (attached) AttachThreadInput(me, fgThread, false);
            }
        }
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
