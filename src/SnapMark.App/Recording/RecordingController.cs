using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using SnapMark.Annotations;
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

    // Рисование поверх записи: окно-холст создаётся при первом выборе инструмента.
    static LiveDrawWindow? _draw;
    static PixelRect _rect;
    static double _scaling = 1;
    static ToolKind _lastTool = ToolKind.Pen;
    static bool _clickThroughWarned;

    public static bool IsRecording => _recorder is not null;

    /// <summary>Текущий инструмент рисования на видео; null — курсор (мышь уходит программам под областью).</summary>
    public static ToolKind? DrawTool { get; private set; }

    public static LiveDrawCanvas? DrawBoard => _draw?.Board;

    /// <summary>Выбрать инструмент рисования на видео (null — вернуться к курсору).</summary>
    public static void SetDrawTool(ToolKind? tool)
    {
        if (_recorder is null) return;
        if (tool is null)
        {
            DrawTool = null;
            if (_draw is not null)
            {
                _draw.Board.IsDrawing = false;
                if (!PlatformServices.WindowHelper.SetClickThrough(_draw, true))
                {
                    // Система не умеет пропускать мышь сквозь окно — прячем рисунки, чтобы не мешали работать.
                    _draw.Hide();
                    if (!_clickThroughWarned)
                    {
                        _clickThroughWarned = true;
                        Toast.Show(L.T("Рисунки скрыты"), L.T("На этой системе нельзя кликать сквозь рисунки. Они снова появятся, когда выберешь инструмент."));
                    }
                }
            }
            _hud?.SyncTools();
            return;
        }

        if (!EnsureDrawWindow()) return;
        var w = _draw!;
        if (!w.IsVisible)
        {
            w.Show();
            RaiseHud();
        }
        w.Board.Tool = tool.Value;
        w.Board.IsDrawing = true;
        PlatformServices.WindowHelper.SetClickThrough(w, false);
        DrawTool = tool;
        _lastTool = tool.Value;
        _hud?.SyncTools();
    }

    /// <summary>Последний выбранный инструмент — для кнопки «Рисовать».</summary>
    public static ToolKind LastDrawTool => _lastTool;

    public static void SetDrawColor(Color color)
    {
        if (_draw is null && !EnsureDrawWindow()) return;
        _draw!.Board.DrawStyle.Color = color;
        _hud?.SyncTools();
    }

    public static Color DrawColor => _draw?.Board.DrawStyle.Color
        ?? (Color.TryParse(AppServices.Settings.LastColor, out var c) ? c : Color.Parse("#FF3B30"));

    static bool EnsureDrawWindow()
    {
        if (_draw is not null) return true;
        var w = new LiveDrawWindow(_rect, _scaling);
        w.Board.ContentChanged += () => _hud?.SyncTools();
        w.Board.CursorRequested += () => SetDrawTool(null);
        w.Board.ToolRequested += t => SetDrawTool(t);
        w.Show();
        if (!w.IsTransparent)
        {
            // Без прозрачности (Linux без композитора) окно закрыло бы область записи сплошной заливкой.
            w.Close();
            Toast.Show(L.T("Рисование на видео недоступно"), L.T("Системе не хватает прозрачных окон. В Linux включи композитный менеджер окон (эффекты рабочего стола)."));
            return false;
        }
        _draw = w;
        RaiseHud();
        return true;
    }

    /// <summary>Панель записи — поверх холста (важно, когда записывается весь экран).</summary>
    static void RaiseHud()
    {
        if (_hud is not { } hud) return;
        hud.Hide();
        hud.Show();
    }

    static void CloseDrawWindow()
    {
        _draw?.Close();
        _draw = null;
        DrawTool = null;
    }

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
        _rect = rect;
        _scaling = scaling > 0 ? scaling : 1;
        DrawTool = null;

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
            CloseDrawWindow();
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
        CloseDrawWindow();

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

    // Рисование на видео.
    static readonly string[] DrawPalette = { "#FF3B30", "#FF9500", "#FFCC00", "#34C759", "#0A84FF", "#FFFFFF", "#000000" };
    readonly StackPanel _tools = new() { Orientation = Orientation.Horizontal, Spacing = 2, IsVisible = false };
    readonly Button _drawToggle;
    readonly Button _cursorButton;
    readonly Button _undoButton;
    readonly Button _clearButton;
    readonly Dictionary<ToolKind, Button> _toolButtons = new();
    readonly List<Button> _swatches = new();

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

        // ── Рисование на видео: всё, что рисуешь, сразу видно в записи ──
        stack.Children.Add(UiKit.VSep());
        _drawToggle = UiKit.ToolButton("Icon.InkPen", L.T("Рисовать на видео: стрелки, метки, текст — всё видно в записи"), ToggleTools);
        stack.Children.Add(_drawToggle);

        _cursorButton = UiKit.ToolButton("Icon.Select", L.T("Курсор: работать с программами в области записи (Esc)"), () => RecordingController.SetDrawTool(null));
        _tools.Children.Add(_cursorButton);
        foreach (var (tool, icon, tip) in new[]
        {
            (ToolKind.Pen, "Icon.InkPen", L.T("Перо (P): задержи в конце — станет ровной фигурой")),
            (ToolKind.Highlighter, "Icon.Marker", L.T("Маркер-выделитель (H)")),
            (ToolKind.Arrow, "Icon.Arrow", L.T("Стрелка (A)")),
            (ToolKind.Rectangle, "Icon.Rect", L.T("Прямоугольник (R)")),
            (ToolKind.Ellipse, "Icon.Ellipse", L.T("Эллипс (O)")),
            (ToolKind.Marker, "Icon.MarkerBadge", L.T("Метка A, B, C… (M): нажми или потяни к цели, затем набери комментарий")),
            (ToolKind.Text, "Icon.Text", L.T("Текст (T): Enter — готово, Shift+Enter — новая строка")),
            (ToolKind.Eraser, "Icon.Eraser", L.T("Ластик (E): проведи по объекту, чтобы удалить")),
        })
        {
            var t = tool;
            var b = UiKit.ToolButton(icon, tip, () => RecordingController.SetDrawTool(t));
            if (t == ToolKind.Marker && b.Content is Avalonia.Controls.Shapes.Path path) path.Classes.Add("solid");
            _toolButtons[t] = b;
            _tools.Children.Add(b);
        }
        _tools.Children.Add(UiKit.VSep());
        // Цвета — прямо в панели (без всплывающих окон: они попали бы в видео).
        foreach (var hex in DrawPalette)
        {
            var color = Color.Parse(hex);
            var sw = new Button { Background = new SolidColorBrush(color), Tag = hex, VerticalAlignment = VerticalAlignment.Center };
            sw.Classes.Add("swatch");
            sw.Classes.Add("mini");
            sw.Click += (_, _) => RecordingController.SetDrawColor(color);
            _swatches.Add(sw);
            _tools.Children.Add(sw);
        }
        _tools.Children.Add(UiKit.VSep());
        _undoButton = UiKit.ToolButton("Icon.Undo", L.T("Отменить (Ctrl+Z)"), () => RecordingController.DrawBoard?.Undo());
        _clearButton = UiKit.ToolButton("Icon.Trash", L.T("Стереть всё нарисованное"), () => RecordingController.DrawBoard?.Clear(), "danger");
        _tools.Children.Add(_undoButton);
        _tools.Children.Add(_clearButton);
        stack.Children.Add(_tools);
        SyncTools();

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

    void ToggleTools()
    {
        _tools.IsVisible = !_tools.IsVisible;
        RecordingController.SetDrawTool(_tools.IsVisible ? RecordingController.LastDrawTool : null);
        // Панель стала шире/уже — снова по центру области.
        Dispatcher.UIThread.Post(Reposition, DispatcherPriority.Background);
    }

    /// <summary>Подсветить текущий инструмент и цвет, включить/выключить «Отменить» и «Стереть всё».</summary>
    public void SyncTools()
    {
        var current = RecordingController.DrawTool;
        _drawToggle.Classes.Set("selected", _tools.IsVisible);
        _cursorButton.Classes.Set("selected", current is null);
        foreach (var (tool, b) in _toolButtons) b.Classes.Set("selected", current == tool);
        var board = RecordingController.DrawBoard;
        _undoButton.IsEnabled = board?.Document.CanUndo == true;
        _clearButton.IsEnabled = board?.HasContent == true;
        var color = RecordingController.DrawColor;
        foreach (var sw in _swatches)
            sw.Classes.Set("current", sw.Tag is string hex && Color.Parse(hex).ToUInt32() == Color.FromRgb(color.R, color.G, color.B).ToUInt32());
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
