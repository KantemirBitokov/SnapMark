using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
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

    /// <summary>Перечитать системные настройки доступности (вызывается при запуске и периодически).</summary>
    public static void RefreshSystemAccessibility()
    {
        bool reduce = false, contrast = false;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                reduce = key?.GetValue("EnableTransparency") is int v && v == 0;
                var hc = new HighContrastInfo { cbSize = (uint)Marshal.SizeOf<HighContrastInfo>() };
                contrast = SystemParametersInfo(0x0042 /* SPI_GETHIGHCONTRAST */, hc.cbSize, ref hc, 0) && (hc.dwFlags & 1) != 0;
            }
            else if (OperatingSystem.IsMacOS())
            {
                (reduce, contrast) = Platform.Mac.MacNative.AccessibilityDisplay();
            }
        }
        catch { }
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

    internal static GlassBackdrop? BackdropOf(TopLevel? topLevel) =>
        topLevel is not null && Backdrops.TryGetValue(topLevel, out var b) ? b : null;

    static GlassBackdrop Ensure(TopLevel topLevel) => Backdrops.GetValue(topLevel, _ => new GlassBackdrop());

    /// <summary>Фон окна известен заранее (редактор скриншота: замороженный снимок экрана) — размываем один раз.</summary>
    public static void SetStaticBackdrop(TopLevel topLevel, Bitmap? fullWindowImage)
    {
        if (fullWindowImage is null) return;
        try { Ensure(topLevel).Set(Blur(fullWindowImage, 12)); }
        catch { /* без размытия — просто тонированное стекло */ }
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
                if (shot is not null) backdrop.Set(Blur(shot, 8));
            }
            catch { }
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
    static BlurredImage Blur(Bitmap source, int factor)
    {
        int w = Math.Max(2, source.PixelSize.Width / factor), h = Math.Max(2, source.PixelSize.Height / factor);
        using var small = source.CreateScaledBitmap(new PixelSize(w, h), BitmapInterpolationMode.MediumQuality);
        var format = small.Format ?? PixelFormat.Bgra8888;
        var alpha = small.AlphaFormat ?? AlphaFormat.Premul;
        int stride = w * 4;
        var pixels = new byte[stride * h];
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try { small.CopyPixels(new PixelRect(0, 0, w, h), handle.AddrOfPinnedObject(), pixels.Length, stride); }
        finally { handle.Free(); }

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
}

/// <summary>Размытый фон окна (картинка на всю клиентскую область окна).</summary>
internal sealed class GlassBackdrop
{
    public BlurredImage? Image { get; private set; }
    public event Action? Changed;

    // Старую картинку не освобождаем явно: её ещё может рисовать поток композиции. Её заберёт сборщик мусора.
    public void Set(BlurredImage image)
    {
        Image = image;
        Changed?.Invoke();
    }
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
        Glass.TransparencyChanged += OnGlassChanged;
        ActualThemeVariantChanged += OnThemeChanged;
        ScheduleAdapt();
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
        }
        if (!Equals(RequestedThemeVariant, want)) RequestedThemeVariant = want;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_backdrop is not null) _backdrop.Changed -= OnBackdropChanged;
        _backdrop = null;
        Glass.TransparencyChanged -= OnGlassChanged;
        ActualThemeVariantChanged -= OnThemeChanged;
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

        // 1. Тень.
        if (BoxShadow.Count > 0) ctx.DrawRectangle(ShadowCaster, null, rr, BoxShadow);

        // 2. Размытый фон ровно под панелью.
        bool hasBackdrop = false;
        if (!Glass.ReduceTransparency && _backdrop?.Image?.Bitmap is { } img && TopLevel.GetTopLevel(this) is { } top
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
