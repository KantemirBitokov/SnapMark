using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SkiaSharp;
using SnapMark.Platform;

namespace SnapMark.UI;

/// <summary>
/// «Жидкое стекло» в духе iOS / macOS 26: размытый фон под панелью, тонированное стекло, блик сверху
/// и светлый ободок. Прозрачность стекла настраивается (Настройки → Основные).
/// </summary>
/// <summary>
/// Вариант стекла — как у Apple, один на всё приложение:
/// Regular — подстраивается под фон (над светлым становится светлым, над тёмным — тёмным), читается везде;
/// Clear — почти прозрачное с лёгким затемнением и всегда светлым текстом, для ярких фонов и видео.
/// </summary>
public enum GlassVariant { Regular, Clear }

public static class Glass
{
    static readonly ConditionalWeakTable<TopLevel, GlassBackdrop> Backdrops = new();
    static double _transparency = 0.5;
    static GlassVariant _variant = GlassVariant.Regular;

    public static GlassVariant Variant
    {
        get => _variant;
        set
        {
            if (_variant == value) return;
            _variant = value;
            TransparencyChanged?.Invoke();
        }
    }

    /// <summary>В системе выключены эффекты прозрачности — стекло матовое, без размытия.</summary>
    public static bool ReduceTransparency { get; private set; }

    /// <summary>В системе включён высокий контраст — стекло непрозрачное с контрастным краем.</summary>
    public static bool HighContrast { get; private set; }

    /// <summary>В системе выключена анимация («Уменьшить движение») — линза выделения переставляется без перетекания.</summary>
    public static bool ReduceMotion { get; private set; }

    /// <summary>Перечитать системные настройки доступности (вызывается при запуске и периодически).</summary>
    public static void RefreshSystemAccessibility()
    {
        bool reduce = false, contrast = false, still = false;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                reduce = key?.GetValue("EnableTransparency") is int v && v == 0;
                var hc = new HighContrastInfo { cbSize = (uint)Marshal.SizeOf<HighContrastInfo>() };
                contrast = SystemParametersInfo(0x0042 /* SPI_GETHIGHCONTRAST */, hc.cbSize, ref hc, 0) && (hc.dwFlags & 1) != 0;
                // «Показывать анимацию в Windows» выключено.
                still = SystemParametersInfo(0x1042 /* SPI_GETCLIENTAREAANIMATION */, 0, out int anim, 0) && anim == 0;
            }
            else if (OperatingSystem.IsMacOS())
            {
                (reduce, contrast, still) = Platform.Mac.MacNative.AccessibilityDisplay();
            }
        }
        catch { }
        ReduceMotion = still;
        if (reduce == ReduceTransparency && contrast == HighContrast) return;
        ReduceTransparency = reduce;
        HighContrast = contrast;
        TransparencyChanged?.Invoke();
    }

    [StructLayout(LayoutKind.Sequential)]
    struct HighContrastInfo
    {
        public uint cbSize;
        public uint dwFlags;
        public IntPtr lpszDefaultScheme;
    }

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool SystemParametersInfo(uint action, uint param, ref HighContrastInfo info, uint winIni);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool SystemParametersInfo(uint action, uint param, out int value, uint winIni);

    /// <summary>0 — почти матовое стекло, 1 — почти прозрачное.</summary>
    public static double Transparency
    {
        get => _transparency;
        set
        {
            value = Math.Clamp(value, 0, 1);
            if (Math.Abs(value - _transparency) < 0.001) return;
            _transparency = value;
            TransparencyChanged?.Invoke();
        }
    }

    public static event Action? TransparencyChanged;

    static double _frost = 0.45;

    /// <summary>Матовость стекла: 0 — прозрачное (фон резкий), 1 — полностью матовое (сильное размытие и зерно).</summary>
    public static double Frost
    {
        get => _frost;
        set
        {
            value = Math.Clamp(value, 0, 1);
            if (Math.Abs(value - _frost) < 0.001) return;
            _frost = value;
            RebuildFrost();
        }
    }

    /// <summary>С учётом системного «Уменьшить прозрачность» (тогда стекло становится матовым — как в iOS).</summary>
    public static double EffectiveFrost => ReduceTransparency ? 1 : _frost;

    static bool _frostPending;

    /// <summary>Пересобрать матовые версии фонов у всех окон (после смены матовости), не чаще раза за кадр.</summary>
    static void RebuildFrost()
    {
        if (_frostPending) return;
        _frostPending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _frostPending = false;
            foreach (var pair in Backdrops)
                if (pair.Value.Optics is { } optics && optics.Rebuild()) pair.Value.NotifyChanged();
            TransparencyChanged?.Invoke();
        }, DispatcherPriority.Background);
    }

    internal static GlassBackdrop? BackdropOf(TopLevel? topLevel) =>
        topLevel is not null && Backdrops.TryGetValue(topLevel, out var b) ? b : null;

    static GlassBackdrop Ensure(TopLevel topLevel)
    {
        if (Backdrops.TryGetValue(topLevel, out var existing)) return existing;
        var created = Backdrops.GetValue(topLevel, _ => new GlassBackdrop());
        BackdropCreated?.Invoke(topLevel); // панели, уже показанные в этом окне, подхватят фон
        return created;
    }

    internal static event Action<TopLevel>? BackdropCreated;

    /// <summary>Ошибка стекла: в журнал (один раз на вид ошибки) и в Trace — чтобы не проглатывать молча.</summary>
    static readonly HashSet<string> Reported = new();
    internal static void Report(Exception ex)
    {
        System.Diagnostics.Trace.WriteLine("[Glass] " + ex);
        if (Reported.Add(ex.GetType().Name + ex.Message)) App.LogError(ex);
    }

    /// <summary>Фон окна известен заранее (редактор скриншота: замороженный снимок экрана) — размываем один раз.</summary>
    public static void SetStaticBackdrop(TopLevel topLevel, Bitmap? fullWindowImage)
    {
        if (fullWindowImage is null) return;
        var backdrop = Ensure(topLevel);
        SKImage? sharp = null;
        try
        {
            // Всё через Skia: Avalonia не умеет уменьшать WriteableBitmap (CreateScaledBitmap падает на нём,
            // из-за этого раньше размытый фон не создавался вовсе и стекло было просто тонированным).
            sharp = GlassOptics.ToSkImage(fullWindowImage);
            if (sharp is not null) backdrop.Set(Blur(sharp, 12));
        }
        catch (Exception ex) { Report(ex); /* без размытия — просто тонированное стекло */ }
        if (sharp is null) return;
        // Оптика (матовость и линзирование) — сразу после показа окна, чтобы не задерживать его появление.
        Dispatcher.UIThread.Post(() =>
        {
            try { backdrop.SetOptics(new SkiaBackdrop(sharp)); }
            catch (Exception ex) { Report(ex); }
        }, DispatcherPriority.Background);
    }

    /// <summary>
    /// Плавающее окно (панель записи, уведомления, панель серии…): на Windows раз в ~0,35 с снимаем экран
    /// под окном и размываем. Окно исключено из захвата, поэтому на снимке — то, что ПОД ним.
    /// На macOS и Linux — тонированное стекло без размытия.
    /// </summary>
    public static void EnableLiveBackdrop(Window window)
    {
        if (!OperatingSystem.IsWindows()) return;
        var backdrop = Ensure(window);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        bool pending = false;

        void Grab()
        {
            pending = false;
            try
            {
                if (!window.IsVisible || window.ClientSize.Width < 4) return;
                // Если окно не исключено из захвата, на снимке было бы оно само — тогда без размытия.
                if (!PlatformServices.WindowHelper.IsExcludedFromCapture(window)) return;
                var scaling = window.RenderScaling > 0 ? window.RenderScaling : 1;
                var size = PixelSize.FromSize(window.ClientSize, scaling);
                using var shot = PlatformServices.ScreenCapture.Capture(new PixelRect(window.Position, size));
                if (shot is null) return;
                if (GlassOptics.ToSkImage(shot) is not { } sharp) return;
                backdrop.SetOptics(new SkiaBackdrop(sharp), notify: false);
                backdrop.Set(Blur(sharp, 8));
            }
            catch (Exception ex) { Report(ex); }
        }

        void GrabSoon()
        {
            if (pending) return;
            pending = true;
            Dispatcher.UIThread.Post(Grab, DispatcherPriority.Background);
        }

        timer.Tick += (_, _) => Grab();
        window.Opened += (_, _) =>
        {
            GrabSoon();
            timer.Start();
        };
        window.PositionChanged += (_, _) => GrabSoon();
        window.Closed += (_, _) => timer.Stop();
    }

    /// <summary>Размытие: уменьшаем в factor раз и трижды проходим box-blur — мягко и почти бесплатно.</summary>
    static BlurredImage Blur(SKImage source, int factor)
    {
        int w = Math.Max(2, source.Width / factor), h = Math.Max(2, source.Height / factor);
        var format = PixelFormat.Bgra8888;
        var alpha = AlphaFormat.Premul;
        int stride = w * 4;
        var pixels = new byte[stride * h];
        var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var surface = SKSurface.Create(info))
        {
            using (var paint = new SKPaint { FilterQuality = SKFilterQuality.Medium })
                surface.Canvas.DrawImage(source, new SKRect(0, 0, w, h), paint);
            var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            try { surface.ReadPixels(info, handle.AddrOfPinnedObject(), stride, 0, 0); }
            finally { handle.Free(); }
        }

        for (int pass = 0; pass < 3; pass++) BoxBlur(pixels, w, h, 2);

        var result = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), format, alpha);
        using (var fb = result.Lock())
            for (int y = 0; y < h; y++)
                Marshal.Copy(pixels, y * stride, fb.Address + y * fb.RowBytes, stride);
        return new BlurredImage(result, pixels, w, h, format == PixelFormat.Rgba8888);
    }

    /// <summary>Горизонтальный и вертикальный box-blur радиуса r по всем четырём каналам (края — повтором).</summary>
    static void BoxBlur(byte[] px, int w, int h, int r)
    {
        var tmp = new byte[px.Length];
        int window = r * 2 + 1;
        for (int y = 0; y < h; y++)
        {
            int row = y * w * 4;
            for (int c = 0; c < 4; c++)
            {
                int sum = 0;
                for (int i = -r; i <= r; i++) sum += px[row + Math.Clamp(i, 0, w - 1) * 4 + c];
                for (int x = 0; x < w; x++)
                {
                    tmp[row + x * 4 + c] = (byte)(sum / window);
                    sum += px[row + Math.Min(x + r + 1, w - 1) * 4 + c] - px[row + Math.Max(x - r, 0) * 4 + c];
                }
            }
        }
        for (int x = 0; x < w; x++)
        {
            for (int c = 0; c < 4; c++)
            {
                int sum = 0;
                for (int i = -r; i <= r; i++) sum += tmp[Math.Clamp(i, 0, h - 1) * w * 4 + x * 4 + c];
                for (int y = 0; y < h; y++)
                {
                    px[y * w * 4 + x * 4 + c] = (byte)(sum / window);
                    sum += tmp[Math.Min(y + r + 1, h - 1) * w * 4 + x * 4 + c] - tmp[Math.Max(y - r, 0) * w * 4 + x * 4 + c];
                }
            }
        }
    }
}

/// <summary>Размытая картинка и её пиксели (по ним стекло понимает, светлый под ним фон или тёмный).</summary>
internal sealed record BlurredImage(Bitmap Bitmap, byte[] Pixels, int Width, int Height, bool Rgba)
{
    /// <summary>Средняя яркость 0…1 в доле [x0..x1]×[y0..y1] картинки (координаты 0…1).</summary>
    public double Luminance(double x0, double y0, double x1, double y1)
    {
        int ax = Math.Clamp((int)(x0 * Width), 0, Width - 1), bx = Math.Clamp((int)Math.Ceiling(x1 * Width), ax + 1, Width);
        int ay = Math.Clamp((int)(y0 * Height), 0, Height - 1), by = Math.Clamp((int)Math.Ceiling(y1 * Height), ay + 1, Height);
        double sum = 0;
        int n = 0;
        for (int y = ay; y < by; y++)
            for (int x = ax; x < bx; x++)
            {
                int i = (y * Width + x) * 4;
                double r = Pixels[Rgba ? i : i + 2], g = Pixels[i + 1], b = Pixels[Rgba ? i + 2 : i];
                sum += 0.2126 * r + 0.7152 * g + 0.0722 * b;
                n++;
            }
        return n == 0 ? 0.5 : sum / n / 255.0;
    }

    /// <summary>Насколько «пёстрый» фон (разброс яркости 0…1): над текстом и деталями — больше.</summary>
    public double Busyness(double x0, double y0, double x1, double y1)
    {
        int ax = Math.Clamp((int)(x0 * Width), 0, Width - 1), bx = Math.Clamp((int)Math.Ceiling(x1 * Width), ax + 1, Width);
        int ay = Math.Clamp((int)(y0 * Height), 0, Height - 1), by = Math.Clamp((int)Math.Ceiling(y1 * Height), ay + 1, Height);
        double sum = 0, sum2 = 0;
        int n = 0;
        for (int y = ay; y < by; y++)
            for (int x = ax; x < bx; x++)
            {
                int i = (y * Width + x) * 4;
                double l = (0.2126 * Pixels[Rgba ? i : i + 2] + 0.7152 * Pixels[i + 1] + 0.0722 * Pixels[Rgba ? i + 2 : i]) / 255.0;
                sum += l;
                sum2 += l * l;
                n++;
            }
        if (n < 2) return 0;
        double mean = sum / n;
        return Math.Sqrt(Math.Max(0, sum2 / n - mean * mean));
    }
}

/// <summary>Размытый фон окна (картинка на всю клиентскую область окна).</summary>
internal sealed class GlassBackdrop
{
    public BlurredImage? Image { get; private set; }
    /// <summary>Резкий фон и его матовая версия для шейдера (если Skia его поддерживает).</summary>
    public SkiaBackdrop? Optics { get; private set; }
    public event Action? Changed;

    // Старые картинки не освобождаем явно: их ещё может рисовать поток композиции. Их заберёт сборщик мусора.
    public void Set(BlurredImage image)
    {
        Image = image;
        Changed?.Invoke();
    }

    public void SetOptics(SkiaBackdrop optics, bool notify = true)
    {
        Optics = optics;
        if (notify) Changed?.Invoke();
    }

    public void NotifyChanged() => Changed?.Invoke();
}

/// <summary>
/// Стеклянная панель — замена Border для «пилюль»: тень, размытый фон под панелью, тонированное стекло,
/// блик сверху и тонкий светлый ободок, ярче у верхнего края, как у iOS / macOS.
/// </summary>
public class GlassPanel : ThemeVariantScope
{
    /// <summary>Капсула: скругление — половина высоты (тулбары, панель записи), как у Apple.</summary>
    public static readonly StyledProperty<bool> CapsuleProperty =
        AvaloniaProperty.Register<GlassPanel, bool>(nameof(Capsule));

    public bool Capsule
    {
        get => GetValue(CapsuleProperty);
        set => SetValue(CapsuleProperty, value);
    }

    public static readonly StyledProperty<CornerRadius> CornerRadiusProperty =
        AvaloniaProperty.Register<GlassPanel, CornerRadius>(nameof(CornerRadius), new CornerRadius(18));

    public static readonly StyledProperty<BoxShadows> BoxShadowProperty =
        AvaloniaProperty.Register<GlassPanel, BoxShadows>(nameof(BoxShadow));

    public CornerRadius CornerRadius
    {
        get => GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    public BoxShadows BoxShadow
    {
        get => GetValue(BoxShadowProperty);
        set => SetValue(BoxShadowProperty, value);
    }

    GlassBackdrop? _backdrop;
    static readonly IBrush ShadowCaster = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));

    // ── «Перетекающая» линза выделения ──────────────────────────────
    // Кнопки с классом «seg» — одна группа инструментов: выбранную подсвечивает линза, которую рисует сама панель
    // (под иконками). При выборе другой кнопки линза плавно переезжает к ней и по пути чуть растягивается — как капля.
    static readonly TimeSpan LensDuration = TimeSpan.FromMilliseconds(300);
    readonly HashSet<Button> _watched = new();
    Rect? _lensRect;          // где линза сейчас (в координатах панели)
    Rect _lensFrom, _lensTo;
    DateTime _lensStart;
    bool _lensAnimating;
    Button? _lensTarget;

    /// <summary>Найти выбранную кнопку группы и, если она сменилась или сдвинулась, повести к ней линзу.</summary>
    void UpdateLens()
    {
        Button? target = null;
        foreach (var b in this.GetVisualDescendants().OfType<Button>())
        {
            if (!b.Classes.Contains("seg")) continue;
            if (_watched.Add(b)) b.Classes.CollectionChanged += (_, _) => Dispatcher.UIThread.Post(UpdateLens, DispatcherPriority.Render);
            if (target is null && b.IsEffectivelyVisible && b.Classes.Contains("selected")) target = b;
        }

        if (target is null || target.TranslatePoint(default, this) is not Point p)
        {
            if (_lensRect is not null) { _lensRect = null; _lensAnimating = false; InvalidateVisual(); }
            _lensTarget = null;
            return;
        }
        var to = new Rect(p, target.Bounds.Size);
        bool moved = _lensRect is not Rect cur || !Near(cur, to);
        if (!moved && !_lensAnimating) return;

        if (_lensRect is null || ReferenceEquals(target, _lensTarget) && !_lensAnimating || Glass.ReduceMotion)
        {
            // Первое появление, сдвиг раскладки той же кнопки или «Уменьшить движение» — сразу на место.
            _lensRect = to;
            _lensAnimating = false;
        }
        else if (!Near(_lensTo, to) || !_lensAnimating)
        {
            _lensFrom = _lensRect.Value;
            _lensTo = to;
            _lensStart = DateTime.UtcNow;
            if (!_lensAnimating)
            {
                _lensAnimating = true;
                TopLevel.GetTopLevel(this)?.RequestAnimationFrame(OnLensFrame);
            }
        }
        _lensTarget = target;
        InvalidateVisual();
    }

    void OnLensFrame(TimeSpan _)
    {
        if (!_lensAnimating) return;
        InvalidateVisual();
        if ((DateTime.UtcNow - _lensStart) < LensDuration) TopLevel.GetTopLevel(this)?.RequestAnimationFrame(OnLensFrame);
        else
        {
            _lensAnimating = false;
            _lensRect = _lensTo;
        }
    }

    /// <summary>Текущее положение линзы: плавный «пружинный» переезд с растяжением в середине пути.</summary>
    Rect? CurrentLens()
    {
        if (!_lensAnimating) return _lensRect;
        double k = Math.Clamp((DateTime.UtcNow - _lensStart).TotalMilliseconds / LensDuration.TotalMilliseconds, 0, 1);
        // ease-out-back: быстро стартует, чуть проскакивает цель и мягко возвращается — как пружина.
        const double c1 = 1.25, c3 = c1 + 1;
        double e = 1 + c3 * Math.Pow(k - 1, 3) + c1 * Math.Pow(k - 1, 2);
        double x = _lensFrom.X + (_lensTo.X - _lensFrom.X) * e;
        double y = _lensFrom.Y + (_lensTo.Y - _lensFrom.Y) * e;
        double w = _lensFrom.Width + (_lensTo.Width - _lensFrom.Width) * e;
        double h = _lensFrom.Height + (_lensTo.Height - _lensFrom.Height) * e;
        // Растяжение по направлению движения — «жидкая» капля.
        double stretch = Math.Sin(k * Math.PI) * 10;
        bool horizontal = Math.Abs(_lensTo.X - _lensFrom.X) >= Math.Abs(_lensTo.Y - _lensFrom.Y);
        _lensRect = horizontal ? new Rect(x - stretch / 2, y + stretch * 0.08, w + stretch, h - stretch * 0.16)
                               : new Rect(x + stretch * 0.08, y - stretch / 2, w - stretch * 0.16, h + stretch);
        return _lensRect;
    }

    static bool Near(Rect a, Rect b) =>
        Math.Abs(a.X - b.X) < 0.5 && Math.Abs(a.Y - b.Y) < 0.5 && Math.Abs(a.Width - b.Width) < 0.5 && Math.Abs(a.Height - b.Height) < 0.5;

    void DrawLens(DrawingContext ctx)
    {
        if (CurrentLens() is not Rect lens) return;
        var radius = new CornerRadius(Math.Min(lens.Width, lens.Height) / 2);
        var rr = new RoundedRect(lens, radius);
        var theme = ActualThemeVariant;
        IBrush fill = this.TryFindResource("Sm.LensBg", theme, out var f) && f is IBrush fb ? fb : new SolidColorBrush(Color.FromArgb(70, 255, 255, 255));
        IBrush edge = this.TryFindResource("Sm.LensBorder", theme, out var b) && b is IBrush bb ? bb : Brushes.White;
        ctx.DrawRectangle(fill, new Pen(edge, 1), rr, new BoxShadows(new BoxShadow { OffsetY = 3, Blur = 10, Color = Color.FromArgb(46, 0, 0, 0) }));
    }

    static GlassPanel()
    {
        AffectsRender<GlassPanel>(CornerRadiusProperty, BoxShadowProperty, BoundsProperty, CapsuleProperty);
    }

    public GlassPanel()
    {
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.HighQuality);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _backdrop = Glass.BackdropOf(TopLevel.GetTopLevel(this));
        if (_backdrop is not null) _backdrop.Changed += OnBackdropChanged;
        else Glass.BackdropCreated += OnBackdropCreated;
        Glass.TransparencyChanged += OnGlassChanged;
        ActualThemeVariantChanged += OnThemeChanged;
        LayoutUpdated += OnLayoutUpdated;
        ScheduleAdapt();
        Dispatcher.UIThread.Post(UpdateLens, DispatcherPriority.Loaded);
    }

    void OnLayoutUpdated(object? sender, EventArgs e) => UpdateLens();

    void OnBackdropCreated(TopLevel topLevel)
    {
        if (!ReferenceEquals(TopLevel.GetTopLevel(this), topLevel) || _backdrop is not null) return;
        Glass.BackdropCreated -= OnBackdropCreated;
        _backdrop = Glass.BackdropOf(topLevel);
        if (_backdrop is not null) _backdrop.Changed += OnBackdropChanged;
        OnBackdropChanged();
    }

    void OnBackdropChanged()
    {
        InvalidateVisual();
        ScheduleAdapt();
    }

    void OnGlassChanged()
    {
        InvalidateVisual();
        ScheduleAdapt();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BoundsProperty) ScheduleAdapt(); // панель переехала — под ней другой фон
    }

    bool _adaptPending;
    bool? _lightGlass;
    double _shadowBoost = 1;

    BoxShadows ScaledShadows()
    {
        if (Math.Abs(_shadowBoost - 1) < 0.01 || BoxShadow.Count == 0) return BoxShadow;
        var list = new BoxShadow[BoxShadow.Count];
        for (int i = 0; i < BoxShadow.Count; i++)
        {
            var sh = BoxShadow[i];
            sh.Color = Color.FromArgb((byte)Math.Clamp(sh.Color.A * _shadowBoost, 0, 255), sh.Color.R, sh.Color.G, sh.Color.B);
            list[i] = sh;
        }
        return list.Length == 1 ? new BoxShadows(list[0]) : new BoxShadows(list[0], list[1..]);
    }

    void ScheduleAdapt()
    {
        if (_adaptPending) return;
        _adaptPending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _adaptPending = false;
            Adapt();
        }, DispatcherPriority.Background);
    }

    /// <summary>
    /// Адаптивная читаемость (вариант Regular): над светлым фоном стекло светлое с тёмным текстом, над тёмным —
    /// тёмное со светлым, как «адаптивные тени» в iOS. Clear — всегда тёмная подложка и светлый текст.
    /// Без фона (macOS/Linux у плавающих окон) — как выбранная тема программы.
    /// </summary>
    void Adapt()
    {
        ThemeVariant? want = null;
        if (Glass.HighContrast) want = null;
        else if (Glass.Variant == GlassVariant.Clear) want = ThemeVariant.Dark;
        else if (!Glass.ReduceTransparency && _backdrop?.Image is { } img && TopLevel.GetTopLevel(this) is { } top
                 && this.TranslatePoint(default, top) is Point o && top.ClientSize.Width > 0 && top.ClientSize.Height > 0)
        {
            double cw = top.ClientSize.Width, ch = top.ClientSize.Height;
            double lum = img.Luminance(o.X / cw, o.Y / ch, (o.X + Bounds.Width) / cw, (o.Y + Bounds.Height) / ch);
            // Гистерезис: не «мигать» на границе светлого и тёмного.
            _lightGlass = _lightGlass switch
            {
                true => lum > 0.42,
                false => lum > 0.60,
                null => lum > 0.52,
            };
            want = _lightGlass == true ? ThemeVariant.Light : ThemeVariant.Dark;
            // Адаптивная тень (Apple): над текстом и пёстрым фоном — плотнее, над ровным светлым — слабее.
            double busy = img.Busyness(o.X / cw, o.Y / ch, (o.X + Bounds.Width) / cw, (o.Y + Bounds.Height) / ch);
            double boost = Math.Clamp(0.7 + busy * 3.2 - (lum > 0.8 ? 0.15 : 0), 0.6, 1.6);
            if (Math.Abs(boost - _shadowBoost) > 0.05) { _shadowBoost = boost; InvalidateVisual(); }
        }
        if (!Equals(RequestedThemeVariant, want)) RequestedThemeVariant = want;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_backdrop is not null) _backdrop.Changed -= OnBackdropChanged;
        _backdrop = null;
        Glass.BackdropCreated -= OnBackdropCreated;
        Glass.TransparencyChanged -= OnGlassChanged;
        ActualThemeVariantChanged -= OnThemeChanged;
        LayoutUpdated -= OnLayoutUpdated;
    }

    void OnThemeChanged(object? sender, EventArgs e) => InvalidateVisual();

    public override void Render(DrawingContext ctx)
    {
        var r = new Rect(Bounds.Size);
        if (r.Width <= 0 || r.Height <= 0) return;
        var radius = Capsule ? new CornerRadius(Math.Min(r.Width, r.Height) / 2) : CornerRadius;
        var rr = new RoundedRect(r, radius);
        bool dark = ActualThemeVariant == ThemeVariant.Dark;
        bool clear = Glass.Variant == GlassVariant.Clear;
        double t = Glass.Transparency;

        // Подхватываем фон окна, если он появился позже (окно уже показано).
        if (_backdrop is null && TopLevel.GetTopLevel(this) is { } tl0 && Glass.BackdropOf(tl0) is { } late)
        {
            _backdrop = late;
            _backdrop.Changed += OnBackdropChanged;
            ScheduleAdapt();
        }

        // Высокий контраст (системная настройка): сплошная заливка и контрастный край, без стекла.
        if (Glass.HighContrast)
        {
            if (BoxShadow.Count > 0) ctx.DrawRectangle(ShadowCaster, null, rr, BoxShadow);
            ctx.DrawRectangle(dark ? Brushes.Black : Brushes.White, new Pen(dark ? Brushes.White : Brushes.Black, 1.5), rr);
            return;
        }

        // 1. Тень (адаптивная).
        if (BoxShadow.Count > 0) ctx.DrawRectangle(ShadowCaster, null, rr, ScaledShadows());

        // 2. Фон под панелью: шейдер Liquid Glass (матовость + линзирование + блики) или простое размытие.
        bool hasBackdrop = false;
        if (_backdrop?.Optics is { } optics && TopLevel.GetTopLevel(this) is { } topO
            && this.TranslatePoint(default, topO) is Point o2 && topO.ClientSize.Width > 0)
        {
            double pxPerDip = optics.Sharp.Width / topO.ClientSize.Width;
            ctx.Custom(new GlassDrawOperation(optics, r, new Point(o2.X * pxPerDip, o2.Y * pxPerDip), pxPerDip, radius.TopLeft));
            hasBackdrop = true;
        }
        else if (!Glass.ReduceTransparency && _backdrop?.Image?.Bitmap is { } img && TopLevel.GetTopLevel(this) is { } top
            && this.TranslatePoint(default, top) is Point origin && top.ClientSize.Width > 0 && top.ClientSize.Height > 0)
        {
            double sx = img.Size.Width / top.ClientSize.Width, sy = img.Size.Height / top.ClientSize.Height;
            var src = new Rect(origin.X * sx, origin.Y * sy, r.Width * sx, r.Height * sy)
                .Intersect(new Rect(img.Size));
            if (src.Width > 0 && src.Height > 0)
            {
                using (ctx.PushClip(rr))
                    ctx.DrawImage(img, src, r);
                hasBackdrop = true;
            }
        }

        using (ctx.PushClip(rr))
        {
            // 3. Тонированное стекло: чем выше «прозрачность», тем больше видно фон.
            //    Clear — лёгкое затемнение (так светлый текст читается над любым ярким фоном).
            //    «Уменьшить прозрачность» в системе — почти непрозрачная заливка.
            double a = Glass.ReduceTransparency ? 0.97
                : clear ? (hasBackdrop ? 0.46 - 0.30 * t : 0.62 - 0.25 * t)
                : hasBackdrop ? 0.86 - 0.62 * t : 0.97 - 0.40 * t;
            var tint = clear ? Color.FromRgb(12, 12, 16) : dark ? Color.FromRgb(30, 30, 34) : Color.FromRgb(246, 246, 250);
            ctx.FillRectangle(new SolidColorBrush(tint, a), r);

            // 4. Блик сверху — как у выпуклого стекла.
            var sheen = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb((byte)(dark ? 34 : 110), 255, 255, 255), 0),
                    new GradientStop(Color.FromArgb(0, 255, 255, 255), 0.45),
                },
            };
            ctx.FillRectangle(sheen, new Rect(r.X, r.Y, r.Width, Math.Min(r.Height, 120)));

            // 4½. Линза выбранного инструмента — под иконками (дети рисуются поверх панели).
            DrawLens(ctx);
        }

        // 5. Ободок: светлый, ярче сверху, плюс еле заметный тёмный контур для отделения от светлого фона.
        var rim = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromArgb((byte)(dark ? 105 : 235), 255, 255, 255), 0),
                new GradientStop(Color.FromArgb((byte)(dark ? 26 : 120), 255, 255, 255), 0.55),
                new GradientStop(Color.FromArgb((byte)(dark ? 46 : 150), 255, 255, 255), 1),
            },
        };
        ctx.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb((byte)(dark ? 70 : 28), 0, 0, 0)), 1), rr);
        ctx.DrawRectangle(null, new Pen(rim, 1), new RoundedRect(r.Deflate(1), Deflate(radius, 1)));
    }

    static CornerRadius Deflate(CornerRadius c, double d) =>
        new(Math.Max(0, c.TopLeft - d), Math.Max(0, c.TopRight - d), Math.Max(0, c.BottomRight - d), Math.Max(0, c.BottomLeft - d));
}
