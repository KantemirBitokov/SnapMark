using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using SkiaSharp;
using SnapMark.Core;
using SnapMark.Export;
using SnapMark.Platform;
using SnapMark.Services;
using SnapMark.UI;

namespace SnapMark.Capture;

/// <summary>
/// Длинный скриншот: SnapMark сам прокручивает страницу или документ под выделенной областью
/// (колесом мыши в её центре), снимает кадры и склеивает их. Остановится сам в конце страницы;
/// «Готово» или Esc — раньше. Если автопрокрутка недоступна — крути сам, кадры снимаются автоматически.
/// </summary>
public static class LongShotController
{
    const int MaxHeight = 30000, MaxFrames = 120;
    static CancellationTokenSource? _cts;
    static LongShotHud? _hud;
    static IDisposable? _keys;

    public static bool IsActive => _cts is not null;

    public static void Start(PixelRect rect, double scaling)
    {
        if (IsActive) return;
        _cts = new CancellationTokenSource();
        var cts = _cts;
        bool auto = AutoScroll.IsSupported;
        _hud = new LongShotHud(auto);
        _hud.Done += () => cts.Cancel();
        _hud.Show();
        // Esc где угодно — закончить (клавиши видит слежение за вводом, если оно доступно).
        var mon = InputMonitor.Instance;
        if (mon.IsSupported)
        {
            mon.KeyCombo += OnKey;
            _keys = mon.Acquire();
        }
        _ = RunAsync(rect, scaling, auto, cts.Token);
    }

    static void OnKey(string combo)
    {
        if (combo == "Esc") Dispatcher.UIThread.Post(() => _cts?.Cancel());
    }

    /// <summary>Снять и склеить (доступно и для автотестов). progress: (кадров, высота).</summary>
    public static async Task<SKBitmap?> CaptureAsync(PixelRect rect, double scaling, bool auto, CancellationToken ct, Action<int, int>? progress = null)
    {
        var first = await Task.Run(() => Grab(rect));
        if (first is null) return null;
        var stitcher = new Stitcher(first.Width);
        stitcher.Add(first);
        first.Dispose();
        progress?.Invoke(stitcher.Frames, stitcher.Height);
        int still = 0;
        int cx = rect.X + rect.Width / 2, cy = rect.Y + rect.Height / 2;
        while (!ct.IsCancellationRequested && stitcher.Height < MaxHeight && stitcher.Frames < MaxFrames)
        {
            if (auto)
            {
                // Прокрутка примерно на половину высоты области (по 3 строки за «щелчок»), затем ждём, пока страница успокоится.
                AutoScroll.Scroll(cx, cy, Math.Clamp(rect.Height / 150, 1, 8), scaling);
                try { await Task.Delay(260, ct); } catch (OperationCanceledException) { break; }
            }
            else
            {
                try { await Task.Delay(220, ct); } catch (OperationCanceledException) { break; }
            }
            var frame = await Task.Run(() => Grab(rect));
            if (frame is null) break;
            int added;
            using (frame) added = stitcher.Add(frame);
            progress?.Invoke(stitcher.Frames, stitcher.Height);
            if (added == 0)
            {
                // Конец страницы: дважды подряд ничего не сдвинулось (в ручном режиме — ждём, пока прокрутят).
                if (auto && ++still >= 3) break;
            }
            else still = 0;
        }
        return stitcher.Result();
    }

    static SKBitmap? Grab(PixelRect rect)
    {
        using var bmp = PlatformServices.ScreenCapture.Capture(rect);
        return bmp is null ? null : Stitcher.ToSk(bmp);
    }

    static async Task RunAsync(PixelRect rect, double scaling, bool auto, CancellationToken ct)
    {
        await Task.Delay(250); // редактор успел закрыться
        SKBitmap? result = null;
        try
        {
            result = await CaptureAsync(rect, scaling, auto, ct, (f, h) => Dispatcher.UIThread.Post(() => _hud?.Progress(f, h)));
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            Toast.Show(L.T("Длинный скриншот не удался"), ex.Message);
        }
        finally
        {
            InputMonitor.Instance.KeyCombo -= OnKey;
            _keys?.Dispose();
            _keys = null;
            _hud?.Close();
            _hud = null;
            _cts = null;
        }
        if (result is null) return;
        byte[] png;
        using (result)
        using (var img = SKImage.FromBitmap(result))
        using (var data = img.Encode(SKEncodedImageFormat.Png, 100))
            png = data.ToArray();
        LongShotWindow.Open(png, scaling);
    }
}

/// <summary>Панель во время съёмки: кадры, высота, «Готово». В снимки не попадает.</summary>
public sealed class LongShotHud : Window
{
    readonly TextBlock _text = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 4, 0) }.Style("headline");
    public event Action? Done;

    public LongShotHud(bool auto)
    {
        UiKit.MakeFloating(this);
        ShowActivated = false;
        Title = L.T("SnapMark — длинный скриншот");
        var done = UiKit.SoftButton(L.T("Готово"), () => Done?.Invoke(), primary: true);
        var hint = UiKit.Caption(auto
            ? L.T("Прокручиваю сам · Esc — закончить")
            : L.T("Прокручивай страницу — кадры снимаются сами"));
        hint.VerticalAlignment = VerticalAlignment.Center;
        hint.TextWrapping = TextWrapping.NoWrap;
        var stack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { _text, hint, done } };
        var card = new GlassPanel { Margin = new Thickness(16), Padding = new Thickness(6), Child = stack };
        card.Classes.Add("pill");
        card.Classes.Add("bar");
        Content = card;
        Progress(0, 0);
        Opened += (_, _) =>
        {
            PlatformServices.WindowHelper.ExcludeFromCapture(this);
            var screen = Screens.Primary;
            if (screen is null) return;
            double sc = screen.Scaling > 0 ? screen.Scaling : 1;
            Position = new PixelPoint(screen.WorkingArea.X + (screen.WorkingArea.Width - (int)(ClientSize.Width * sc)) / 2, screen.WorkingArea.Y + (int)(4 * sc));
        };
    }

    public void Progress(int frames, int height) =>
        _text.Text = frames == 0 ? L.T("Длинный скриншот…") : L.F("Кадров: {0} · {1} px", frames, height);
}

/// <summary>Результат: длинная картинка с прокруткой и действия — копировать, сохранить, закрепить.</summary>
public sealed class LongShotWindow : Window
{
    readonly byte[] _png;

    public static void Open(byte[] png, double scaling) => new LongShotWindow(png, scaling).Show();

    LongShotWindow(byte[] png, double scaling)
    {
        _png = png;
        Bitmap bmp;
        using (var ms = new MemoryStream(png)) bmp = new Bitmap(ms);
        Title = L.T("Длинный скриншот — SnapMark");
        if (App.Logo is not null) Icon = new WindowIcon(App.Logo);
        Classes.Add("sm");
        ExtendClientAreaToDecorationsHint = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        double w = bmp.PixelSize.Width / scaling, h = bmp.PixelSize.Height / scaling;
        Width = Math.Clamp(w + 48, 480, 1100);
        Height = 760;

        var title = new TextBlock { Text = L.T("Длинный скриншот") }.Style("pagetitle");
        var sub = UiKit.Caption($"{bmp.PixelSize.Width} × {bmp.PixelSize.Height} px");
        var header = new StackPanel { Margin = new Thickness(24, 40, 24, 10), Spacing = 2, Children = { title, sub } };

        var image = new Image { Source = bmp, Width = Math.Min(w, 1052), Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center };
        RenderOptions.SetBitmapInterpolationMode(image, BitmapInterpolationMode.HighQuality);
        var scroll = new ScrollViewer
        {
            Content = new Border
            {
                Child = image, CornerRadius = new CornerRadius(10), ClipToBounds = true, HorizontalAlignment = HorizontalAlignment.Center,
                BoxShadow = BoxShadows.Parse("0 6 20 0 #33000000"), Margin = new Thickness(0, 4, 0, 16),
            },
            Padding = new Thickness(24, 0),
        };

        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(24, 12),
            Children =
            {
                UiKit.SoftButton(L.T("Закрепить поверх окон"), () => { PinWindow.Pin(_png); }),
                UiKit.SoftButton(L.T("Сохранить как…"), () => _ = SaveAsync()),
                UiKit.SoftButton(L.T("Копировать"), () => _ = CopyAsync(), primary: true),
            },
        };
        var root = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(header);
        root.Children.Add(footer);
        root.Children.Add(scroll);
        Content = root;
    }

    async Task CopyAsync()
    {
        await SmartPaste.CopyPlainAsync(new ClipboardPayload { Png = _png, Dib = ImageEncoder.ToDib(_png) });
        Toast.Show(L.T("Скопировано"), L.T("Длинный скриншот в буфере обмена."));
    }

    async Task SaveAsync()
    {
        var s = AppServices.Settings;
        var ext = ImageEncoder.Extension(s.ImageFormat);
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = L.T("Сохранить скриншот"),
            SuggestedFileName = FileNaming.Build(s.FileNameTemplate, DateTime.Now, AppServices.PeekShotNumber()) + "_long." + ext,
            DefaultExtension = ext,
        });
        if (file is null) return;
        var path = file.TryGetLocalPath();
        var format = ImageEncoder.FromExtension(Path.GetExtension(path ?? file.Name)) ?? s.ImageFormat;
        await using (var stream = await file.OpenWriteAsync())
        {
            stream.SetLength(0);
            await stream.WriteAsync(ImageEncoder.Encode(_png, format, s.ImageQuality));
        }
        AppServices.NextShotNumber();
        if (path is not null) Toast.Show(L.T("Сохранено"), Path.GetFileName(path), L.T("Показать"), () => ShellOpen.ShowInFolder(path));
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape) Close();
        else if (e.Key == Key.C && (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0) _ = CopyAsync();
    }
}
