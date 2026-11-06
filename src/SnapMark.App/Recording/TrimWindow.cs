using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using SnapMark.Core;
using SnapMark.Services;
using SnapMark.UI;

namespace SnapMark.Recording;

/// <summary>
/// Обрезка видео после записи: кадр, лента миниатюр с ручками начала и конца (как в «Фото» на iPhone),
/// «Сохранить отрезок» (MP4) и «Экспорт в GIF» (частота кадров и ширина на выбор).
/// </summary>
public sealed class TrimWindow : Window
{
    string? _file;
    double _duration;
    int _videoW, _videoH;
    readonly Image _preview = new() { Stretch = Stretch.Uniform };
    readonly TrimTimeline _timeline = new() { Height = 64 };
    readonly TextBlock _range = new TextBlock().Style("body");
    readonly TextBlock _status = UiKit.Caption("");
    readonly Button _save, _gif;
    readonly WrapPanel _fpsChips = new(), _widthChips = new();
    int _fps = 15, _gifWidth = 720;
    CancellationTokenSource? _frameCts;
    CancellationTokenSource? _jobCts;

    /// <summary>Открыть видео (null — сначала выбрать файл).</summary>
    public static void Open(string? file)
    {
        var w = new TrimWindow();
        w.Show();
        if (file is not null) _ = w.LoadAsync(file);
        else w.Opened += async (_, _) => await w.PickAsync();
    }

    TrimWindow()
    {
        Title = L.T("Обрезка видео — SnapMark");
        Width = 820;
        Height = 660;
        MinWidth = 560;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ExtendClientAreaToDecorationsHint = true;
        if (App.Logo is not null) Icon = new WindowIcon(App.Logo);
        Classes.Add("sm");

        var title = new TextBlock { Text = L.T("Обрезка видео") }.Style("pagetitle");
        var header = new StackPanel { Margin = new Thickness(24, 40, 24, 8), Spacing = 2, Children = { title, _status } };

        var previewBox = new Border
        {
            Child = _preview,
            Background = Brushes.Black,
            CornerRadius = new CornerRadius(12),
            ClipToBounds = true,
            Margin = new Thickness(24, 4, 24, 12),
        };

        _timeline.RangeChanged += OnRangeChanged;
        _timeline.ScrubTo += t => ShowFrame(t);

        foreach (var (fps, label) in new[] { (10, "10 fps"), (15, "15 fps"), (24, "24 fps") })
            _fpsChips.Children.Add(Chip(label, fps == _fps, () => { _fps = fps; SyncChips(); }, _fpsChips, fps));
        foreach (var (w, label) in new[] { (0, L.T("Исходная")), (960, "960"), (720, "720"), (480, "480") })
            _widthChips.Children.Add(Chip(label, w == _gifWidth, () => { _gifWidth = w; SyncChips(); }, _widthChips, w));

        _save = UiKit.SoftButton(L.T("Сохранить отрезок"), () => _ = SaveAsync(gif: false), primary: true);
        _gif = UiKit.SoftButton(L.T("Экспорт в GIF"), () => _ = SaveAsync(gif: true));
        var cancel = UiKit.SoftButton(L.T("Закрыть"), Close);

        var gifRow = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = L.T("GIF:"), VerticalAlignment = VerticalAlignment.Center }.Style("caption1"),
                _fpsChips,
                new TextBlock { Text = L.T("ширина"), VerticalAlignment = VerticalAlignment.Center }.Style("caption1"),
                _widthChips,
            },
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, _gif, _save } };
        var footer = new StackPanel
        {
            Margin = new Thickness(24, 4, 24, 20), Spacing = 12,
            Children =
            {
                _timeline,
                _range,
                new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children = { gifRow, buttons } },
            },
        };
        Grid.SetColumn(buttons, 1);

        var root = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(header);
        root.Children.Add(footer);
        root.Children.Add(previewBox);
        Content = root;
        SetBusy(true);
    }

    ToggleButton Chip(string text, bool on, Action pick, WrapPanel owner, int tag)
    {
        var t = new ToggleButton { Content = text, IsChecked = on, Tag = tag };
        t.Classes.Add("chip");
        t.Click += (_, _) => pick();
        return t;
    }

    void SyncChips()
    {
        foreach (var c in _fpsChips.Children.OfType<ToggleButton>()) c.IsChecked = (int)c.Tag! == _fps;
        foreach (var c in _widthChips.Children.OfType<ToggleButton>()) c.IsChecked = (int)c.Tag! == _gifWidth;
    }

    async Task PickAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = L.T("Выбери видео"),
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType(L.T("Видео")) { Patterns = new[] { "*.mp4", "*.mkv", "*.webm", "*.mov", "*.gif" } } },
            SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(AppServices.Settings.VideoFolder),
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (path is null) { Close(); return; }
        await LoadAsync(path);
    }

    async Task LoadAsync(string file)
    {
        _file = file;
        Title = $"{Path.GetFileName(file)} — {L.T("Обрезка видео")}";
        _status.Text = L.T("Загружаю видео…");
        try
        {
            (_duration, _videoW, _videoH) = await VideoTools.ProbeAsync(file);
            if (_duration <= 0) throw new InvalidOperationException(L.T("Не удалось определить длительность видео."));
            if (_videoW > 0 && _videoW < _gifWidth) { _gifWidth = 0; SyncChips(); }
            _timeline.Duration = _duration;
            _timeline.SetRange(0, _duration);
            _status.Text = L.T("Двигай жёлтые ручки — начало и конец; щелчок по ленте — посмотреть кадр.");
            ShowFrame(0);
            SetBusy(false);
            OnRangeChanged();
            await LoadThumbnailsAsync(file);
        }
        catch (Exception ex)
        {
            _status.Text = ex.Message;
        }
    }

    async Task LoadThumbnailsAsync(string file)
    {
        const int count = 12;
        var thumbs = new Bitmap?[count];
        for (int i = 0; i < count; i++)
        {
            double t = _duration * (i + 0.5) / count;
            var png = await VideoTools.FrameAsync(file, t, 160, CancellationToken.None);
            if (png is not null)
            {
                using var ms = new MemoryStream(png);
                thumbs[i] = new Bitmap(ms);
                _timeline.SetThumbnails(thumbs);
            }
            if (!IsVisible) return;
        }
    }

    void OnRangeChanged()
    {
        var (s, e) = (_timeline.Start, _timeline.End);
        _range.Text = $"{Fmt(s)} — {Fmt(e)}   ·   {L.F("длина {0}", Fmt(e - s))}";
    }

    static string Fmt(double t) => TimeSpan.FromSeconds(Math.Max(0, t)).ToString(t >= 3600 ? @"h\:mm\:ss\.f" : @"m\:ss\.f");

    /// <summary>Кадр в момент t (с отменой предыдущего запроса, чтобы ручка ехала плавно).</summary>
    void ShowFrame(double t)
    {
        if (_file is null) return;
        _frameCts?.Cancel();
        var cts = _frameCts = new CancellationTokenSource();
        var file = _file;
        DispatcherTimer.RunOnce(async () =>
        {
            if (cts.IsCancellationRequested) return;
            try
            {
                var png = await VideoTools.FrameAsync(file, t, 960, cts.Token);
                if (png is null || cts.IsCancellationRequested) return;
                using var ms = new MemoryStream(png);
                var old = _preview.Source as Bitmap;
                _preview.Source = new Bitmap(ms);
                old?.Dispose();
            }
            catch (OperationCanceledException) { }
            catch { }
        }, TimeSpan.FromMilliseconds(90));
    }

    void SetBusy(bool busy)
    {
        _save.IsEnabled = _gif.IsEnabled = !busy && _file is not null;
        _timeline.IsEnabled = !busy;
    }

    async Task SaveAsync(bool gif)
    {
        if (_file is null) return;
        double s = _timeline.Start, e = _timeline.End;
        if (e - s < 0.1) return;
        var dir = Path.GetDirectoryName(_file) ?? AppServices.Settings.VideoFolder;
        var name = Path.GetFileNameWithoutExtension(_file);
        var output = FileNaming.EnsureUnique(Path.Combine(dir, $"{name}_{(gif ? "gif" : "cut")}.{(gif ? "gif" : "mp4")}"));
        SetBusy(true);
        _status.Text = gif ? L.T("Собираю GIF…") : L.T("Сохраняю отрезок…");
        _jobCts = new CancellationTokenSource();
        try
        {
            if (gif) await VideoTools.GifAsync(_file, s, e, _fps, _gifWidth, output, _jobCts.Token);
            else await VideoTools.TrimAsync(_file, s, e, output, _jobCts.Token);
            var size = new FileInfo(output).Length / 1024.0 / 1024.0;
            _status.Text = $"{L.T("Готово")}: {Path.GetFileName(output)} · {size:0.0} MB";
            Toast.Show(gif ? L.T("GIF сохранён") : L.T("Отрезок сохранён"), Path.GetFileName(output), L.T("Показать"), () => ShellOpen.ShowInFolder(output));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _status.Text = L.T("Не получилось") + ": " + ex.Message;
        }
        finally
        {
            SetBusy(false);
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape) Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _frameCts?.Cancel();
        _jobCts?.Cancel();
    }
}

/// <summary>Лента миниатюр с жёлтыми ручками начала и конца и указателем текущего кадра.</summary>
internal sealed class TrimTimeline : Control
{
    Bitmap?[] _thumbs = Array.Empty<Bitmap?>();
    double _start, _end, _playhead;
    enum Grab { None, Start, End, Playhead }
    Grab _grab;
    const double Handle = 14;

    public double Duration { get; set; } = 1;
    public double Start => _start;
    public double End => _end;
    public event Action? RangeChanged;
    public event Action<double>? ScrubTo;

    public TrimTimeline()
    {
        Cursor = new Cursor(StandardCursorType.Hand);
        ClipToBounds = false;
    }

    public void SetRange(double s, double e)
    {
        _start = s;
        _end = e;
        _playhead = s;
        InvalidateVisual();
    }

    public void SetThumbnails(Bitmap?[] thumbs)
    {
        _thumbs = thumbs;
        InvalidateVisual();
    }

    Rect Strip => new(Handle, 0, Math.Max(1, Bounds.Width - Handle * 2), Bounds.Height);
    double X(double t) => Strip.X + Strip.Width * (Duration <= 0 ? 0 : t / Duration);
    double TimeAt(double x) => Math.Clamp((x - Strip.X) / Strip.Width, 0, 1) * Duration;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var p = e.GetPosition(this);
        double xs = X(_start) - Handle / 2, xe = X(_end) + Handle / 2;
        _grab = Math.Abs(p.X - xs) <= Handle ? Grab.Start : Math.Abs(p.X - xe) <= Handle ? Grab.End : Grab.Playhead;
        e.Pointer.Capture(this);
        Move(p.X);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_grab != Grab.None) Move(e.GetPosition(this).X);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _grab = Grab.None;
        e.Pointer.Capture(null);
    }

    void Move(double x)
    {
        double minLen = Math.Min(0.2, Duration);
        switch (_grab)
        {
            case Grab.Start:
                _start = Math.Clamp(TimeAt(x + Handle / 2), 0, _end - minLen);
                _playhead = _start;
                RangeChanged?.Invoke();
                break;
            case Grab.End:
                _end = Math.Clamp(TimeAt(x - Handle / 2), _start + minLen, Duration);
                _playhead = _end;
                RangeChanged?.Invoke();
                break;
            case Grab.Playhead:
                _playhead = Math.Clamp(TimeAt(x), _start, _end);
                break;
        }
        ScrubTo?.Invoke(_playhead);
        InvalidateVisual();
    }

    public override void Render(DrawingContext ctx)
    {
        var strip = Strip;
        using (ctx.PushClip(new RoundedRect(strip, 8)))
        {
            ctx.FillRectangle(new SolidColorBrush(Color.FromRgb(40, 40, 44)), strip);
            if (_thumbs.Length > 0)
            {
                double w = strip.Width / _thumbs.Length;
                for (int i = 0; i < _thumbs.Length; i++)
                {
                    var b = _thumbs[i];
                    if (b is null) continue;
                    var dest = new Rect(strip.X + i * w, strip.Y, w, strip.Height);
                    // Заполнить ячейку с обрезкой по центру.
                    double scale = Math.Max(dest.Width / b.Size.Width, dest.Height / b.Size.Height);
                    var src = new Rect((b.Size.Width - dest.Width / scale) / 2, (b.Size.Height - dest.Height / scale) / 2, dest.Width / scale, dest.Height / scale);
                    ctx.DrawImage(b, src, dest);
                }
            }
            // Вне отрезка — затемнение.
            var dim = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0));
            ctx.FillRectangle(dim, new Rect(strip.X, strip.Y, X(_start) - strip.X, strip.Height));
            ctx.FillRectangle(dim, new Rect(X(_end), strip.Y, strip.Right - X(_end), strip.Height));
        }

        // Жёлтая рамка отрезка с ручками.
        var yellow = new SolidColorBrush(Color.FromRgb(255, 204, 0));
        double xs = X(_start), xe = X(_end);
        ctx.DrawRectangle(null, new Pen(yellow, 3), new Rect(xs, 1.5, Math.Max(0, xe - xs), Bounds.Height - 3), 4, 4);
        foreach (var (hx, left) in new[] { (xs - Handle, true), (xe, false) })
        {
            var r = new Rect(hx, 0, Handle, Bounds.Height);
            ctx.DrawRectangle(yellow, null, r, left ? 6 : 6, 6);
            var c = r.Center;
            ctx.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(90, 70, 0)), 2, lineCap: PenLineCap.Round),
                new Point(c.X + (left ? 1.5 : -1.5), c.Y - 7), new Point(c.X + (left ? -1.5 : 1.5), c.Y));
            ctx.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(90, 70, 0)), 2, lineCap: PenLineCap.Round),
                new Point(c.X + (left ? -1.5 : 1.5), c.Y), new Point(c.X + (left ? 1.5 : -1.5), c.Y + 7));
        }
        // Текущий кадр.
        double px = X(_playhead);
        ctx.DrawRectangle(Brushes.White, new Pen(new SolidColorBrush(Color.FromArgb(90, 0, 0, 0)), 1),
            new Rect(px - 2, -4, 4, Bounds.Height + 8), 2, 2);
    }
}
