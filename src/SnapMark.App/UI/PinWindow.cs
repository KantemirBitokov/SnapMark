using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using SnapMark.Core;
using SnapMark.Export;
using SnapMark.Platform;
using SnapMark.Services;

namespace SnapMark.UI;

/// <summary>
/// Скриншот-«наклейка» поверх всех окон — удобно сверяться, пока работаешь.
/// Перетаскивание — мышью, колесо — масштаб, Ctrl+колесо — прозрачность, двойной щелчок или Esc — закрыть,
/// Ctrl+C — скопировать; правый клик — меню. Наклеек может быть сколько угодно.
/// </summary>
public sealed class PinWindow : Window
{
    static readonly List<PinWindow> Open = new();

    readonly byte[] _png;
    readonly Bitmap _bitmap;
    readonly Image _image;
    readonly Border _frame;
    readonly Border _close;
    readonly TextBlock _badge = new TextBlock { IsHitTestVisible = false }.Style("caption1");
    readonly Border _badgeBox;
    readonly DispatcherTimer _badgeTimer;
    readonly double _baseW, _baseH;
    double _zoom = 1;
    const double Pad = 14; // место под тень

    public static void Pin(byte[] png, PixelRect? screenRect = null)
    {
        var w = new PinWindow(png, screenRect);
        Open.Add(w);
        w.Closed += (_, _) => Open.Remove(w);
        w.Show();
    }

    public static int Count => Open.Count;

    PinWindow(byte[] png, PixelRect? at)
    {
        _png = png;
        using (var ms = new MemoryStream(png)) _bitmap = new Bitmap(ms);
        SystemDecorations = SystemDecorations.None;
        Topmost = true;
        ShowInTaskbar = false;
        CanResize = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        Background = Brushes.Transparent;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        WindowStartupLocation = WindowStartupLocation.Manual;
        Title = L.T("SnapMark — закреплённый скриншот");
        FontFamily = Annotations.UiFonts.Family;

        // Размер «как на экране»: снимок в физических пикселях → точки окна.
        double scaling = 1;
        var screens = App.GetScreens();
        var screen = at is PixelRect r0 ? screens.FirstOrDefault(s => s.Bounds.Contains(r0.Center)) : screens.FirstOrDefault(s => s.IsPrimary);
        if (screen is not null) scaling = screen.Scaling > 0 ? screen.Scaling : 1;
        _baseW = _bitmap.PixelSize.Width / scaling;
        _baseH = _bitmap.PixelSize.Height / scaling;
        // Огромные снимки — сразу поменьше, чтобы не закрывали весь экран.
        if (screen is not null)
        {
            double maxW = screen.WorkingArea.Width / scaling * 0.6, maxH = screen.WorkingArea.Height / scaling * 0.6;
            _zoom = Math.Min(1, Math.Min(maxW / _baseW, maxH / _baseH));
        }

        _image = new Image { Source = _bitmap, Stretch = Stretch.Fill };
        RenderOptions.SetBitmapInterpolationMode(_image, BitmapInterpolationMode.HighQuality);
        _frame = new Border
        {
            Child = _image,
            CornerRadius = new CornerRadius(10),
            ClipToBounds = true,
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)),
        };
        var shadow = new Border
        {
            Margin = new Thickness(Pad),
            CornerRadius = new CornerRadius(10),
            BoxShadow = BoxShadows.Parse("0 10 28 0 #59000000, 0 1 4 0 #33000000"),
            Background = Brushes.Black,
            Child = _frame,
        };

        // Кнопка «закрыть» в углу — появляется при наведении.
        var x = UiKit.ToolButton("Icon.Close", L.T("Открепить (Esc или двойной щелчок)"), Close);
        x.Width = x.Height = 26;
        var closeGlass = new GlassPanel { Child = x, Padding = new Thickness(2), Capsule = true };
        _close = new Border
        {
            Child = closeGlass,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, Pad + 6, Pad + 6, 0),
            Opacity = 0,
        };
        _badgeBox = new Border
        {
            Child = _badge,
            Background = new SolidColorBrush(Color.FromArgb(200, 28, 28, 30)),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8, 3),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(Pad + 8, 0, 0, Pad + 8),
            Opacity = 0,
            IsHitTestVisible = false,
        };
        _badge.Foreground = Brushes.White;
        Content = new Grid { Children = { shadow, _close, _badgeBox } };

        _badgeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1100) };
        _badgeTimer.Tick += (_, _) => { _badgeTimer.Stop(); _badgeBox.Opacity = 0; };

        PointerEntered += (_, _) => _close.Opacity = 1;
        PointerExited += (_, _) => _close.Opacity = 0;
        ContextMenu = BuildMenu();
        ApplyZoom();

        if (at is PixelRect r)
            Position = new PixelPoint(r.X - (int)(Pad * scaling), r.Y - (int)(Pad * scaling));
        else if (screen is not null)
            Position = new PixelPoint(screen.WorkingArea.Center.X - (int)(_baseW * _zoom * scaling / 2), screen.WorkingArea.Center.Y - (int)(_baseH * _zoom * scaling / 2));
    }

    ContextMenu BuildMenu()
    {
        MenuItem Item(string header, Action act)
        {
            var m = new MenuItem { Header = header };
            m.Click += (_, _) => act();
            return m;
        }
        var opacity = new MenuItem { Header = L.T("Прозрачность") };
        foreach (var v in new[] { 100, 85, 70, 50, 30 })
            opacity.Items.Add(Item($"{v} %", () => { Opacity = v / 100.0; Badge($"{v} %"); }));
        return new ContextMenu
        {
            Items =
            {
                Item(L.T("Копировать (Ctrl+C)"), () => _ = CopyAsync()),
                Item(L.T("Сохранить как…"), () => _ = SaveAsync()),
                new Separator(),
                Item(L.T("Масштаб 100 %"), () => { _zoom = 1; ApplyZoom(); }),
                opacity,
                new Separator(),
                Item(L.T("Открепить"), Close),
                Item(L.T("Открепить все"), () => { foreach (var w in Open.ToList()) w.Close(); }),
            },
        };
    }

    void ApplyZoom()
    {
        _frame.Width = Math.Max(40, _baseW * _zoom);
        _frame.Height = Math.Max(30, _baseH * _zoom);
    }

    void Badge(string text)
    {
        _badge.Text = text;
        _badgeBox.Opacity = 1;
        _badgeTimer.Stop();
        _badgeTimer.Start();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (e.Handled || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.ClickCount >= 2)
        {
            Close();
            return;
        }
        BeginMoveDrag(e);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if ((e.KeyModifiers & KeyModifiers.Control) != 0)
        {
            Opacity = Math.Clamp(Opacity + (e.Delta.Y > 0 ? 0.1 : -0.1), 0.2, 1);
            Badge($"{Math.Round(Opacity * 100)} %");
        }
        else
        {
            // Масштаб вокруг курсора: точка под мышью остаётся на месте.
            var p = e.GetPosition(_frame);
            double old = _zoom;
            _zoom = Math.Clamp(_zoom * (e.Delta.Y > 0 ? 1.12 : 1 / 1.12), 0.15, 5);
            ApplyZoom();
            double k = _zoom / old, s = RenderScaling > 0 ? RenderScaling : 1;
            Position = new PixelPoint(Position.X - (int)Math.Round(p.X * (k - 1) * s), Position.Y - (int)Math.Round(p.Y * (k - 1) * s));
            Badge($"{Math.Round(_zoom * 100)} %");
        }
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape) Close();
        else if (e.Key == Key.C && (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0) _ = CopyAsync();
        else if (e.Key == Key.D0 && (e.KeyModifiers & KeyModifiers.Control) != 0) { _zoom = 1; ApplyZoom(); Badge("100 %"); }
    }

    async Task CopyAsync()
    {
        try
        {
            await PlatformServices.Clipboard.CopyAsync(new ClipboardPayload { Png = _png, Dib = ImageEncoder.ToDib(_png) });
            Badge(L.T("Скопировано"));
        }
        catch (Exception ex)
        {
            Toast.Show(L.T("Не удалось скопировать"), ex.Message);
        }
    }

    async Task SaveAsync()
    {
        var s = AppServices.Settings;
        var ext = ImageEncoder.Extension(s.ImageFormat);
        Topmost = false;
        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = L.T("Сохранить скриншот"),
                SuggestedFileName = FileNaming.Build(s.FileNameTemplate, DateTime.Now, AppServices.PeekShotNumber()) + "." + ext,
                DefaultExtension = ext,
            });
            if (file is null) return;
            var path = file.TryGetLocalPath();
            var format = ImageEncoder.FromExtension(Path.GetExtension(path ?? file.Name)) ?? s.ImageFormat;
            var bytes = ImageEncoder.Encode(_png, format, s.ImageQuality);
            await using (var stream = await file.OpenWriteAsync())
            {
                stream.SetLength(0);
                await stream.WriteAsync(bytes);
            }
            AppServices.NextShotNumber();
            if (path is not null) Toast.Show(L.T("Сохранено"), Path.GetFileName(path), L.T("Показать"), () => ShellOpen.ShowInFolder(path));
        }
        catch (Exception ex)
        {
            Toast.Show(L.T("Не удалось сохранить"), ex.Message);
        }
        finally
        {
            Topmost = true;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _badgeTimer.Stop();
        _bitmap.Dispose();
    }
}
