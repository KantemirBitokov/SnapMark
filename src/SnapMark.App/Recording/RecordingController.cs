using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using SnapMark.Core;
using SnapMark.Platform;
using SnapMark.Platform.Common;
using SnapMark.Services;
using SnapMark.UI;

namespace SnapMark.Recording;

/// <summary>Запуск/остановка записи, рамка вокруг области и панель с таймером.</summary>
public static class RecordingController
{
    static FfmpegRecorder? _recorder;
    static RecordingHud? _hud;
    static readonly List<Window> Frame = new();

    static bool _starting;

    public static bool IsRecording => _recorder is not null;

    public static async void Start(PixelRect rect, double scaling, bool withMic, bool withSystem)
    {
        if (IsRecording)
        {
            Toast.Show(L.T("Запись уже идёт"), L.T("Останови текущую запись кнопкой «Стоп» или горячей клавишей записи."));
            return;
        }
        var s = AppServices.Settings;

        var ffmpeg = FfmpegRecorder.Resolve(s.FfmpegPath);
        if (ffmpeg is null)
        {
            if (FfmpegDownloader.IsSupported)
                Toast.Show(L.T("Для записи видео нужен ffmpeg"),
                    L.T("Это бесплатный видеокодер. SnapMark скачает его сам (~90 МБ, один раз)."),
                    L.T("Скачать"), () => Settings.SettingsWindow.ShowSingle(4, startFfmpegDownload: true));
            else
                Toast.Show(L.T("Для записи видео нужен ffmpeg"),
                    L.T("Установи ffmpeg (brew install ffmpeg / apt install ffmpeg) или укажи путь в настройках → Видео."),
                    L.T("Настройки"), () => Settings.SettingsWindow.ShowSingle(4));
            return;
        }

        // Папка видео недоступна (флешка вынута, сетевой диск отвалился) — пишем в стандартную «Видео».
        var folder = s.VideoFolder;
        try { Directory.CreateDirectory(folder); }
        catch
        {
            folder = DefaultFolders.Videos;
            Directory.CreateDirectory(folder);
        }
        var ext = s.VideoFormat switch
        {
            VideoFileFormat.Mkv => "mkv",
            VideoFileFormat.WebM => "webm",
            VideoFileFormat.Gif => "gif",
            _ => "mp4",
        };
        var output = FileNaming.EnsureUnique(Path.Combine(folder,
            FileNaming.Sanitize($"Video_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.{ext}")));

        var recorder = new FfmpegRecorder();
        // ffmpeg оборвался посреди записи (кончилось место, отключили монитор…) — сохраняем то, что успели.
        recorder.UnexpectedExit += () => Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(_recorder, recorder) || _starting) return;
            Toast.Show(L.T("Запись прервалась"), L.T("ffmpeg неожиданно завершился. Сохраняю то, что успело записаться."));
            _ = StopAsync();
        });
        _recorder = recorder;
        _starting = true;

        // Панель появляется сразу («Запуск…») — видно, что нажатие сработало.
        bool audioAllowed = s.VideoFormat != VideoFileFormat.Gif;
        _hud = new RecordingHud(rect, scaling, withMic && audioAllowed, withSystem && audioAllowed);
        _hud.Show();
        try
        {
            if (s.ShowRecordingFrame) ShowFrame(rect, scaling);
            await recorder.StartAsync(ffmpeg, rect, s, withMic, withSystem, output);
            _hud?.SetRecording();
            if (recorder.SystemAudioProblem == "blackhole")
                Toast.Show(L.T("Звук компьютера: нужен BlackHole"),
                    L.T("На macOS 12 и старше звук компьютера записывается через бесплатный драйвер BlackHole. Установи его и выбери «BlackHole 2ch» как микрофон в настройках записи. Сейчас запись идёт без звука компьютера."),
                    L.T("Скачать"), () => ShellOpen.Open(Platform.Mac.MacSystemAudio.BlackHoleUrl));
            else if (recorder.SystemAudioProblem is { } problem)
                Toast.Show(L.T("Запись без звука компьютера"), problem);
        }
        catch (Exception ex)
        {
            _recorder = null;
            _hud?.Close();
            _hud = null;
            HideFrame();
            Toast.Show(L.T("Запись не запустилась"), ex.Message.Length > 300 ? ex.Message[..300] + "…" : ex.Message);
        }
        finally
        {
            _starting = false;
        }
    }

    public static async Task StopAsync()
    {
        var recorder = _recorder;
        if (recorder is null || _starting) return; // ещё запускается — стоп примем после старта
        _recorder = null;

        _hud?.Close();
        _hud = null;
        HideFrame();

        Toast.Show(L.T("Запись остановлена"), AppServices.Settings.VideoFormat == VideoFileFormat.Gif
            ? L.T("Собираю GIF…")
            : L.T("Сохраняю видео…"));

        try
        {
            var path = await recorder.StopAsync();
            if (File.Exists(path))
                Toast.Show(L.T("Видео сохранено"), Path.GetFileName(path), L.T("Показать"), () => ShellOpen.ShowInFolder(path));
            else
                Toast.Show(L.T("Видео не сохранилось"), recorder.LastLog());
        }
        catch (Exception ex)
        {
            var saved = recorder.OutputPath;
            if (File.Exists(saved))
                Toast.Show(L.T("Видео сохранено, но без звука"), ex.Message.Split('\n')[0], L.T("Показать"), () => ShellOpen.ShowInFolder(saved));
            else
                Toast.Show(L.T("Ошибка при остановке записи"), ex.Message);
        }
    }

    /// <summary>
    /// Рамка — четыре тонких прозрачных окна-полоски СНАРУЖИ области с полупрозрачным пунктиром.
    /// Клики внутри области не перекрывают и в видео не попадают (дополнительно исключены из захвата).
    /// </summary>
    static void ShowFrame(PixelRect r, double scaling)
    {
        int t = Math.Max(2, (int)Math.Round(2 * scaling));   // толщина окна-полоски
        int gap = Math.Max(1, (int)Math.Round(1 * scaling)); // зазор от края области
        var strips = new[]
        {
            new PixelRect(r.X - gap - t, r.Y - gap - t, r.Width + 2 * (gap + t), t),  // верх
            new PixelRect(r.X - gap - t, r.Bottom + gap, r.Width + 2 * (gap + t), t), // низ
            new PixelRect(r.X - gap - t, r.Y - gap, t, r.Height + 2 * gap),           // лево
            new PixelRect(r.Right + gap, r.Y - gap, t, r.Height + 2 * gap),           // право
        };
        foreach (var strip in strips)
        {
            double lw = strip.Width / scaling, lh = strip.Height / scaling;
            bool horizontal = strip.Width >= strip.Height;
            var line = new Avalonia.Controls.Shapes.Line
            {
                StartPoint = horizontal ? new Point(0, lh / 2) : new Point(lw / 2, 0),
                EndPoint = horizontal ? new Point(lw, lh / 2) : new Point(lw / 2, lh),
                Stroke = new SolidColorBrush(Color.Parse("#FF3B30")),
                StrokeThickness = 1.25,
                StrokeDashArray = new Avalonia.Collections.AvaloniaList<double> { 4, 4 },
                Opacity = 0.55,
            };
            var w = new Window
            {
                SystemDecorations = SystemDecorations.None,
                Topmost = true,
                ShowInTaskbar = false,
                CanResize = false,
                ShowActivated = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Background = Brushes.Transparent,
                TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent },
                Position = strip.Position,
                Width = lw,
                Height = lh,
                MinWidth = 0,
                MinHeight = 0,
                Title = L.T("SnapMark — область записи"),
                Content = new Canvas { Children = { line } },
            };
            var bounds = strip;
            w.Opened += (_, _) =>
            {
                PlatformServices.WindowHelper.ExcludeFromCapture(w);
                // Windows может не дать окну быть тоньше минимального размера — задаём точные пиксели напрямую.
                PlatformServices.WindowHelper.SetPixelBounds(w, bounds);
            };
            Frame.Add(w);
            w.Show();
        }
    }

    static void HideFrame()
    {
        foreach (var w in Frame) w.Close();
        Frame.Clear();
    }
}

/// <summary>Панель записи: пульсирующая точка, таймер, кнопка «Стоп».</summary>
public sealed class RecordingHud : Window
{
    readonly PixelRect _region;
    readonly double _scaling;
    readonly Stopwatch _clock = Stopwatch.StartNew();
    readonly DispatcherTimer _timer;
    readonly TextBlock _time = new() { Text = L.T("Запуск…"), FontSize = 14, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    readonly Border _dot;
    Button? _stop;
    bool _recording;

    public RecordingHud(PixelRect region, double scaling, bool withMic, bool withSystem)
    {
        _region = region;
        _scaling = scaling;
        UiKit.MakeFloating(this);
        ShowActivated = false;
        Opacity = 0;
        Title = L.T("SnapMark — запись");

        _time.FontFamily = new FontFamily("Cascadia Mono, Consolas, SF Mono, Menlo, monospace");
        _dot = new Border
        {
            Width = 12, Height = 12, CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Color.Parse("#8E8E93")), // серая, пока ffmpeg запускается
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 2, 0),
        };

        var stop = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children =
                {
                    new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(2), Background = Brushes.White, VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = L.T("Стоп"), VerticalAlignment = VerticalAlignment.Center },
                },
            },
        };
        stop.Classes.Add("record");
        stop.IsEnabled = false; // доступна, когда запись реально пошла
        _stop = stop;
        ToolTip.SetTip(stop, L.T("Остановить запись (повторно нажми горячую клавишу записи)"));
        stop.Click += (_, _) => _ = RecordingController.StopAsync();

        var stack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        stack.Children.Add(_dot);
        stack.Children.Add(_time);
        foreach (var (on, key, tip) in new[] { (withMic, "Icon.Mic", L.T("Пишется микрофон")), (withSystem, "Icon.Speaker", L.T("Пишется звук компьютера")) })
        {
            if (!on) continue;
            var icon = UiKit.Icon(key);
            icon.Width = icon.Height = 20;
            icon.Stretch = Stretch.None;
            icon.StrokeThickness = 1.5;
            icon.Stroke = new SolidColorBrush(Color.Parse("#8E8E93"));
            icon.StrokeLineCap = PenLineCap.Round;
            icon.StrokeJoin = PenLineJoin.Round;
            icon.VerticalAlignment = VerticalAlignment.Center;
            ToolTip.SetTip(icon, tip);
            stack.Children.Add(icon);
        }
        stack.Children.Add(stop);

        var card = new Border { Margin = new Thickness(16), Padding = new Thickness(6), Child = stack };
        card.Classes.Add("pill");
        Content = card;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += (_, _) =>
        {
            if (!_recording)
            {
                _dot.Opacity = _dot.Opacity > 0.5 ? 0.3 : 0.8;
                return;
            }
            var e = _clock.Elapsed;
            _time.Text = e.TotalHours >= 1 ? e.ToString(@"h\:mm\:ss") : e.ToString(@"mm\:ss");
            _dot.Opacity = _dot.Opacity > 0.5 ? 0.35 : 1;
        };

        Opened += (_, _) =>
        {
            PlatformServices.WindowHelper.ExcludeFromCapture(this);
            Reposition();
            Opacity = 1;
            _timer.Start();
        };
    }

    /// <summary>Запись реально началась: красная точка, таймер с нуля, кнопка «Стоп» активна.</summary>
    public void SetRecording()
    {
        _recording = true;
        _clock.Restart();
        _time.Text = "00:00";
        _dot.Background = new SolidColorBrush(Color.Parse("#FF3B30"));
        _dot.Opacity = 1;
        if (_stop is not null) _stop.IsEnabled = true;
    }

    void Reposition()
    {
        int w = (int)Math.Ceiling(ClientSize.Width * _scaling);
        int h = (int)Math.Ceiling(ClientSize.Height * _scaling);
        var screen = Screens.ScreenFromPoint(_region.Center) ?? Screens.Primary;
        var area = screen?.WorkingArea ?? new PixelRect(0, 0, 1920, 1080);

        int x = _region.X + (_region.Width - w) / 2;
        int y = _region.Bottom + (int)(6 * _scaling);               // под областью
        if (y + h > area.Bottom) y = _region.Y - h - (int)(6 * _scaling); // над областью
        if (y < area.Y) y = area.Y;                                  // у верхнего края
        x = Math.Clamp(x, area.X, Math.Max(area.X, area.Right - w));
        Position = new PixelPoint(x, y);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _timer.Stop();
    }
}
