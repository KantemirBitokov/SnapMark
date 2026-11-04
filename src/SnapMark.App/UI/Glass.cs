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
public static class Glass
{
    static readonly ConditionalWeakTable<TopLevel, GlassBackdrop> Backdrops = new();
    static double _transparency = 0.5;

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
    static Bitmap Blur(Bitmap source, int factor)
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
        return result;
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

/// <summary>Размытый фон окна (картинка на всю клиентскую область окна).</summary>
internal sealed class GlassBackdrop
{
    public Bitmap? Image { get; private set; }
    public event Action? Changed;

    // Старую картинку не освобождаем явно: её ещё может рисовать поток композиции. Её заберёт сборщик мусора.
    public void Set(Bitmap image)
    {
        Image = image;
        Changed?.Invoke();
    }
}

/// <summary>
/// Стеклянная панель — замена Border для «пилюль»: тень, размытый фон под панелью, тонированное стекло,
/// блик сверху и тонкий светлый ободок, ярче у верхнего края, как у iOS / macOS.
/// </summary>
public class GlassPanel : Decorator
{
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
        AffectsRender<GlassPanel>(CornerRadiusProperty, BoxShadowProperty, BoundsProperty);
    }

    public GlassPanel()
    {
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.HighQuality);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _backdrop = Glass.BackdropOf(TopLevel.GetTopLevel(this));
        if (_backdrop is not null) _backdrop.Changed += InvalidateVisual;
        Glass.TransparencyChanged += InvalidateVisual;
        ActualThemeVariantChanged += OnThemeChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_backdrop is not null) _backdrop.Changed -= InvalidateVisual;
        _backdrop = null;
        Glass.TransparencyChanged -= InvalidateVisual;
        ActualThemeVariantChanged -= OnThemeChanged;
    }

    void OnThemeChanged(object? sender, EventArgs e) => InvalidateVisual();

    public override void Render(DrawingContext ctx)
    {
        var r = new Rect(Bounds.Size);
        if (r.Width <= 0 || r.Height <= 0) return;
        var rr = new RoundedRect(r, CornerRadius);
        bool dark = ActualThemeVariant == ThemeVariant.Dark;
        double t = Glass.Transparency;

        // Подхватываем фон окна, если он появился позже (окно уже показано).
        if (_backdrop is null && TopLevel.GetTopLevel(this) is { } tl0 && Glass.BackdropOf(tl0) is { } late)
        {
            _backdrop = late;
            _backdrop.Changed += InvalidateVisual;
        }

        // 1. Тень.
        if (BoxShadow.Count > 0) ctx.DrawRectangle(ShadowCaster, null, rr, BoxShadow);

        // 2. Размытый фон ровно под панелью.
        bool hasBackdrop = false;
        if (_backdrop?.Image is { } img && TopLevel.GetTopLevel(this) is { } top
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
            double a = hasBackdrop ? 0.86 - 0.62 * t : 0.97 - 0.40 * t;
            var tint = dark ? Color.FromRgb(30, 30, 34) : Color.FromRgb(246, 246, 250);
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
        ctx.DrawRectangle(null, new Pen(rim, 1), new RoundedRect(r.Deflate(1), Deflate(CornerRadius, 1)));
    }

    static CornerRadius Deflate(CornerRadius c, double d) =>
        new(Math.Max(0, c.TopLeft - d), Math.Max(0, c.TopRight - d), Math.Max(0, c.BottomRight - d), Math.Max(0, c.BottomLeft - d));
}
