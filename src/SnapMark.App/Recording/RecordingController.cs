using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
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

    // Пауза: каждый отрезок пишется в свой файл, в конце склеиваем.
    static readonly List<string> _parts = new();
    static string? _output;
    static bool _paused, _withMic, _withSystem;

    // Рисование поверх записи: окно-холст создаётся при первом выборе инструмента.
    static LiveDrawWindow? _draw;
    static RecordingEffectsWindow? _effects;
    static PixelRect _rect;
    static double _scaling = 1;
    static ToolKind _lastTool = ToolKind.Pen;
    static bool _clickThroughWarned;
    static LiveChromeWindow? _chrome;   // рамка выделения — в видео не попадает
    static LiveStyleWindow? _style;     // панель стиля — в видео не попадает
    static bool _styleOpen;

    /// <summary>Панель стиля открыта кнопкой (иначе появляется сама, когда что-то выделено или печатается).</summary>
    public static bool StyleOpen => _styleOpen;

    public static void ToggleStyle()
    {
        _styleOpen = !_styleOpen;
        UpdateStylePanel();
        _hud?.SyncTools();
    }

    static void UpdateStylePanel()
    {
        var board = _draw?.Board;
        bool want = board is not null && DrawTool is not null && (_styleOpen || board.StyleTarget is not null);
        if (!want)
        {
            _style?.Hide();
            return;
        }
        if (_style is null)
        {
            _style = new LiveStyleWindow(board!);
            _style.Rebuilt += () =>
            {
                _hud?.SyncTools(); // цвет на кнопке «Стиль»
                if (_style is not { } st) return;
                if (!st.HasContent) { st.Hide(); return; }
                // Окно холста — «владелец» панели: система всегда держит панель над ним,
                // иначе холст, забрав фокус при наборе текста, перекрывал бы панель и клики уходили бы в него.
                if (!st.IsVisible)
                {
                    // Показывать с владельцем можно, только пока он виден (иначе Avalonia бросит исключение).
                    if (_draw is not { IsVisible: true } owner || DrawTool is null) return;
                    st.Show(owner);
                }
                PositionStylePanel();
            };
            _style.SizeChanged += (_, _) => PositionStylePanel();
        }
        _style.RequestRebuild();
    }

    /// <summary>Панель стиля — над панелью записи (или под ней, если сверху нет места).</summary>
    static void PositionStylePanel()
    {
        if (_style is not { IsVisible: true } st || _hud is not { } hud) return;
        double hs = hud.RenderScaling > 0 ? hud.RenderScaling : _scaling;
        double ss = st.RenderScaling > 0 ? st.RenderScaling : _scaling;
        int hw = (int)(hud.ClientSize.Width * hs), hh = (int)(hud.ClientSize.Height * hs);
        int sw = (int)(st.ClientSize.Width * ss), sh = (int)(st.ClientSize.Height * ss);
        var screen = hud.Screens.ScreenFromPoint(hud.Position) ?? hud.Screens.Primary;
        var area = screen?.WorkingArea ?? new PixelRect(0, 0, 1920, 1080);
        int gap = (int)(4 * hs);
        int x = hud.Position.X + (hw - sw) / 2;
        int y = hud.Position.Y - sh + gap;
        if (y < area.Y) y = hud.Position.Y + hh - gap;
        x = Math.Clamp(x, area.X, Math.Max(area.X, area.Right - sw));
        y = Math.Clamp(y, area.Y, Math.Max(area.Y, area.Bottom - sh));
        st.Position = new PixelPoint(x, y);
    }

    public static bool IsRecording => _recorder is not null || _paused;

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
            _style?.Hide();
            // В режиме «Мышь» слою интерфейса показывать нечего (выделения нет, свой курсор не нужен),
            // а на Windows он перехватывал бы клики, предназначенные программам под областью.
            _chrome?.Hide();
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
        w.Board.CommitTextInput(); // сменили инструмент посреди набора — текст готов
        if (!w.IsVisible) w.Show();
        if (_chrome is { IsVisible: false } chromeLayer) chromeLayer.Show(w);
        RaiseHud();
        if (tool != ToolKind.Select) w.Board.ClearSelection(); // как в редакторе: новый инструмент — без выделения
        w.Board.Tool = tool.Value;
        w.Board.IsDrawing = true;
        PlatformServices.WindowHelper.SetClickThrough(w, false);
        DrawTool = tool;
        _lastTool = tool.Value;
        UpdateStylePanel();
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
        w.Board.ContentChanged += () =>
        {
            _hud?.SyncTools();
            if (_style is { IsVisible: true } st) st.RequestRebuild();
        };
        w.Board.SelectionChanged += UpdateStylePanel;
        w.Board.CursorRequested += () => SetDrawTool(null);
        w.Board.ToolRequested += t => SetDrawTool(t);
        // Холст получил фокус (клик, набор текста) — система подняла его наверх; панель снова поверх.
        w.Activated += (_, _) => Dispatcher.UIThread.Post(RaiseHud);
        w.Board.ForegroundTaken += () => Dispatcher.UIThread.Post(RaiseHud);
        w.Show();
        if (!w.IsTransparent)
        {
            // Без прозрачности (Linux без композитора) окно закрыло бы область записи сплошной заливкой.
            w.Close();
            Toast.Show(L.T("Рисование на видео недоступно"), L.T("Системе не хватает прозрачных окон. В Linux включи композитный менеджер окон (эффекты рабочего стола)."));
            return false;
        }
        _draw = w;

        // Рамка выделения — на отдельном слое, исключённом из захвата (в Linux исключать окна из записи нельзя).
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
        {
            var chrome = new LiveChromeWindow(_rect, _scaling, w.Board);
            chrome.Show(w);
            if (chrome.ClickThroughOk) _chrome = chrome;
            else chrome.Close();
        }
        w.Board.DrawSelectionInline = _chrome is null;
        // Курсор в видео выключен — системный курсор над холстом прячем, свой рисует слой интерфейса.
        // Без слоя (Linux) оставляем системный: там ffmpeg и так не рисует курсор при -draw_mouse 0.
        w.Board.HideSystemCursor = _chrome is { Excluded: true } && !AppServices.Settings.VideoShowCursor;
        RaiseHud();
        return true;
    }

    /// <summary>Панель записи — поверх холста (важно, когда записывается весь экран).</summary>
    static void RaiseHud()
    {
        var wh = PlatformServices.WindowHelper;
        if (_chrome is { IsVisible: true } chrome) wh.BringToFront(chrome);
        if (_style is { IsVisible: true } style) wh.BringToFront(style);
        if (_hud is { } hud) wh.BringToFront(hud);
    }

    static void CloseDrawWindow()
    {
        _style?.Close();
        _style = null;
        _chrome?.Close();
        _chrome = null;
        _draw?.Close();
        _draw = null;
        DrawTool = null;
        _styleOpen = false;
    }

    /// <summary>Подсветка кликов и показ клавиш — отдельный прозрачный слой, который попадает в видео.</summary>
    static void ShowEffects(PixelRect rect, double scaling)
    {
        var s = AppServices.Settings;
        bool clicks = s.VideoHighlightClicks && !OperatingSystem.IsMacOS(); // на Mac клики рисует ffmpeg
        if (!clicks && !s.VideoShowKeyCombos) return;
        var monitor = InputMonitor.Instance;
        if (!monitor.IsSupported)
        {
            Toast.Show(L.T("Подсветка кликов и клавиш недоступна"), L.T(monitor.Problem ?? ""));
            return;
        }
        var w = new RecordingEffectsWindow(rect, scaling, s.VideoHighlightClicks, s.VideoShowKeyCombos);
        w.Show();
        _effects = w;
        if (monitor.Problem is { } problem)
            Toast.Show(L.T("Показ клавиш недоступен"), L.T(problem));
        RaiseHud();
    }

    static void CloseEffects()
    {
        _effects?.Close();
        _effects = null;
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

        _output = output;
        _parts.Clear();
        _paused = false;
        _withMic = withMic;
        _withSystem = withSystem;

        _rect = rect;
        _scaling = scaling > 0 ? scaling : 1;
        DrawTool = null;
        _starting = true; // пока панель и ffmpeg поднимаются, второй запуск не нужен

        // Панель появляется сразу («Запуск…») — видно, что нажатие сработало.
        bool audioAllowed = s.VideoFormat != VideoFileFormat.Gif;
        _hud = new RecordingHud(rect, scaling, withMic && audioAllowed, withSystem && audioAllowed);
        _hud.PositionChanged += (_, _) => PositionStylePanel();
        _hud.Show();
        try
        {
            if (s.ShowRecordingFrame) ShowFrame(rect, scaling);
            // Камера — заранее: ей нужна секунда на запуск, а в первый кадр она попадёт уже живой.
            if (s.VideoCamera) CameraBubble.Start(rect, scaling);
            await StartRecorderAsync(PartPath(output), first: true);

            if (_recorder?.SystemAudioProblem == "blackhole")
                Toast.Show(L.T("Звук компьютера: нужен BlackHole"),
                    L.T("На macOS 12 и старше звук компьютера записывается через бесплатный драйвер BlackHole. Установи его и выбери «BlackHole 2ch» как микрофон в настройках записи. Сейчас запись идёт без звука компьютера."),
                    L.T("Скачать"), () => ShellOpen.Open(Platform.Mac.MacSystemAudio.BlackHoleUrl));
            else if (_recorder?.SystemAudioProblem is { } problem)
                Toast.Show(L.T("Запись без звука компьютера"), problem);
            if (_recorder?.MicProblem is { } micProblem)
                Toast.Show(L.T("Запись без микрофона"), micProblem);
        }
        catch (Exception ex)
        {
            _recorder = null;
            _hud?.Close();
            _hud = null;
            HideFrame();
            CloseDrawWindow();
            CloseEffects();
            CameraBubble.Stop();
            Toast.Show(L.T("Запись не запустилась"), ex.Message.Length > 300 ? ex.Message[..300] + "…" : ex.Message);
        }
        finally
        {
            _starting = false;
        }
    }

    /// <summary>Запуск ffmpeg на очередной файл: первый отрезок записи или продолжение после паузы.</summary>
    static async Task StartRecorderAsync(string path, bool first)
    {
        var s = AppServices.Settings;
        var ffmpeg = FfmpegRecorder.Resolve(s.FfmpegPath)
            ?? throw new InvalidOperationException(L.T("Для записи видео нужен ffmpeg"));

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
        try
        {
            await recorder.StartAsync(ffmpeg, _rect, s, _withMic, _withSystem, path);
            _hud?.SetRecording(resume: !first);
            if (first) ShowEffects(_rect, _scaling);
        }
        finally
        {
            _starting = false;
        }
    }

    /// <summary>
    /// Файл очередного отрезка — рядом с итоговым видео: если программа упадёт посреди записи,
    /// отрезок легко найти, а не искать в системном временном каталоге.
    /// </summary>
    static string PartPath(string output)
    {
        var dir = Path.GetDirectoryName(output) ?? Path.GetTempPath();
        var name = Path.GetFileNameWithoutExtension(output);
        var ext = Path.GetExtension(output);
        return Path.Combine(dir, $"{name}_part{_parts.Count + 1}{ext}");
    }

    /// <summary>Пауза/продолжение записи (кнопка на панели).</summary>
    public static async Task TogglePauseAsync()
    {
        if (_paused) await ResumeAsync();
        else await PauseAsync();
    }

    static async Task PauseAsync()
    {
        var recorder = _recorder;
        if (recorder is null || _starting) return;

        _recorder = null;
        _paused = true;
        _hud?.SetPaused(true);

        try
        {
            var part = await recorder.StopAsync();
            if (File.Exists(part)) _parts.Add(part);
        }
        catch (Exception ex)
        {
            Toast.Show(L.T("Пауза не сработала"), ex.Message);
        }
    }

    static async Task ResumeAsync()
    {
        _paused = false;
        _hud?.SetPaused(false);
        if (_parts.Count == 0 || _output is not string output) return;

        var part = PartPath(output);

        _starting = true;
        try
        {
            await StartRecorderAsync(part, first: false);
        }
        catch (Exception ex)
        {
            _paused = true;
            _hud?.SetPaused(true);
            Toast.Show(L.T("Запись не возобновилась"), ex.Message);
        }
        finally
        {
            _starting = false;
        }
    }

    /// <summary>Склейка отрезков после паузы — без перекодирования.</summary>
    static async Task ConcatPartsAsync(List<string> parts, string output)
    {
        var ffmpeg = FfmpegRecorder.Resolve(AppServices.Settings.FfmpegPath)
            ?? throw new InvalidOperationException("ffmpeg не найден");

        var list = Path.Combine(Path.GetTempPath(), $"snapmark_concat_{Guid.NewGuid():N}.txt");
        await File.WriteAllLinesAsync(list, parts.Select(p => $"file '{p.Replace("\\", "/")}'"));

        try
        {
            var psi = new ProcessStartInfo(ffmpeg)
            {
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            foreach (var arg in new[] { "-hide_banner", "-y", "-f", "concat", "-safe", "0", "-i", list, "-c", "copy", output })
                psi.ArgumentList.Add(arg);

            using var process = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg не запустился");
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            if (process.ExitCode != 0)
                throw new InvalidOperationException(error.Length > 400 ? error[^400..] : error);
        }
        finally
        {
            try { File.Delete(list); } catch { }
        }

        // Склейка удалась — отрезки больше не нужны. Если нет, они остались рядом с итоговым файлом.
        foreach (var part in parts) { try { File.Delete(part); } catch { } }
    }

    public static async Task StopAsync()
    {
        var recorder = _recorder;
        if (_starting) return;
        if (recorder is null && !_paused) return;

        _recorder = null;
        _paused = false;

        _hud?.Close();
        _hud = null;
        HideFrame();
        CloseDrawWindow();
        CloseEffects();
        CameraBubble.Stop();

        Toast.Show(L.T("Запись остановлена"), AppServices.Settings.VideoFormat == VideoFileFormat.Gif
            ? L.T("Собираю GIF…")
            : L.T("Сохраняю видео…"));

        if (recorder is not null)
        {
            try
            {
                var part = await recorder.StopAsync();
                if (File.Exists(part)) _parts.Add(part);
            }
            catch (Exception ex)
            {
                var saved = recorder.OutputPath;
                if (File.Exists(saved)) _parts.Add(saved);
                else Toast.Show(L.T("Ошибка при остановке записи"), ex.Message);
            }
        }

        var output = _output;
        _output = null;
        if (output is null)
        {
            _parts.Clear();
            return;
        }

        try
        {
            if (_parts.Count == 0)
            {
                Toast.Show(L.T("Видео не сохранилось"), L.T("Запись не успела ничего записать."));
                return;
            }

            if (_parts.Count == 1) File.Move(_parts[0], output, true);
            else await ConcatPartsAsync(_parts.ToList(), output);

            Toast.Show(L.T("Видео сохранено"), Path.GetFileName(output), L.T("Обрезать"), () => TrimWindow.Open(output),
                L.T("Показать"), () => ShellOpen.ShowInFolder(output));
        }
        catch (Exception ex)
        {
            Toast.Show(L.T("Ошибка при сохранении видео"), ex.Message);
        }
        finally
        {
            _parts.Clear();
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
    // Таймер — системным шрифтом (цифры у Segoe UI / SF одинаковой ширины, время не «прыгает»).
    readonly TextBlock _time = new TextBlock { Text = L.T("Запуск…"), VerticalAlignment = VerticalAlignment.Center }.Style("title3");
    readonly Border _dot;
    Button? _stop;
    Button? _pause;
    bool _recording;
    bool _paused;
    bool _moved;
    int _repositionTries;
    TimeSpan _accum;

    // Рисование на видео.
    readonly StackPanel _tools = new() { Orientation = Orientation.Horizontal, Spacing = 2, IsVisible = false };
    readonly Button _drawToggle;
    readonly Button _cursorButton;
    readonly Button _undoButton;
    readonly Button _clearButton;
    readonly Dictionary<ToolKind, Button> _toolButtons = new();
    readonly Button _styleButton;
    readonly Avalonia.Controls.Shapes.Ellipse _styleDot = new() { Width = 14, Height = 14, StrokeThickness = 1, Stroke = new SolidColorBrush(Color.FromArgb(60, 0, 0, 0)) };

    public RecordingHud(PixelRect region, double scaling, bool withMic, bool withSystem)
    {
        _region = region;
        _scaling = scaling;
        UiKit.MakeFloating(this);
        ShowActivated = false;
        Opacity = 0;
        Title = L.T("SnapMark — запись");

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
        stack.Children.Add(DragGrip());
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
        if (AppServices.Settings.VideoFormat != VideoFileFormat.Gif)
        {
            var pause = PauseButton();
            _pause = pause;
            stack.Children.Add(pause);
        }

        // ── Рисование на видео: всё, что рисуешь, сразу видно в записи ──
        stack.Children.Add(UiKit.VSep());
        _drawToggle = UiKit.ToolButton("Icon.InkPen", L.T("Рисовать на видео: стрелки, метки, текст — всё видно в записи"), ToggleTools);
        stack.Children.Add(_drawToggle);

        _cursorButton = UiKit.ToolButton("Icon.Mouse", L.T("Мышь: работать с программами — клики проходят сквозь рисунки (Esc)"), () => RecordingController.SetDrawTool(null), "seg"); // seg — между ними «перетекает» линза
        _tools.Children.Add(_cursorButton);
        foreach (var (tool, icon, tip) in new[]
        {
            (ToolKind.Select, "Icon.Select", L.T("Выбор (V): двигать объекты, менять их стиль; двойной щелчок — править текст или комментарий")),
            (ToolKind.Pen, "Icon.InkPen", L.T("Перо (P): задержи в конце — станет ровной фигурой")),
            (ToolKind.Pencil, "Icon.Pencil", L.T("Карандаш (C)")),
            (ToolKind.Highlighter, "Icon.Marker", L.T("Маркер-выделитель (H)")),
            (ToolKind.Line, "Icon.Line", L.T("Линия (L)")),
            (ToolKind.Arrow, "Icon.Arrow", L.T("Стрелка (A)")),
            (ToolKind.DoubleArrow, "Icon.DoubleArrow", L.T("Двойная стрелка (D)")),
            (ToolKind.Rectangle, "Icon.Rect", L.T("Прямоугольник (R)")),
            (ToolKind.Ellipse, "Icon.Ellipse", L.T("Эллипс (O)")),
            (ToolKind.Marker, "Icon.MarkerBadge", L.T("Метка A, B, C… с комментарием (M)")),
            (ToolKind.MarkerArrow, "", L.T("Метка со стрелкой (N): нажми и тяни к цели")),
            (ToolKind.Text, "Icon.Text", L.T("Текст (T): Enter — готово, Shift+Enter — новая строка")),
            (ToolKind.Eraser, "Icon.Eraser", L.T("Ластик (E): проведи по объекту, чтобы удалить")),
        })
        {
            var t = tool;
            var b = UiKit.ToolButton(t == ToolKind.MarkerArrow ? "Icon.MarkerBadgeSmall" : icon, tip, () => RecordingController.SetDrawTool(t), "seg");
            if (t is ToolKind.Marker or ToolKind.MarkerArrow && b.Content is Avalonia.Controls.Shapes.Path path) path.Classes.Add("solid");
            if (t == ToolKind.MarkerArrow && b.Content is Avalonia.Controls.Shapes.Path badge)
            {
                // Как в редакторе: маленькая метка + стрелка из неё.
                b.Content = null;
                b.Content = new Grid { Width = 20, Height = 20, Children = { badge, UiKit.Icon("Icon.MarkerTail") } };
            }
            _toolButtons[t] = b;
            _tools.Children.Add(b);
        }
        _tools.Children.Add(UiKit.VSep());
        // Стиль: цвет, толщина, шрифт, рамки, тени — как в редакторе. Панель стиля в видео не попадает.
        _styleButton = new Button
        {
            Content = new Grid { Width = 20, Height = 20, Children = { _styleDot } },
        };
        _styleDot.HorizontalAlignment = HorizontalAlignment.Center;
        _styleDot.VerticalAlignment = VerticalAlignment.Center;
        _styleButton.Classes.Add("tool");
        ToolTip.SetTip(_styleButton, L.T("Стиль: цвет, толщина, шрифт, рамка, тень"));
        _styleButton.Click += (_, _) => RecordingController.ToggleStyle();
        _tools.Children.Add(_styleButton);
        _tools.Children.Add(UiKit.VSep());
        _undoButton = UiKit.ToolButton("Icon.Undo", L.T("Отменить (Ctrl+Z)"), () => RecordingController.DrawBoard?.Undo());
        _clearButton = UiKit.ToolButton("Icon.Trash", L.T("Стереть всё нарисованное"), () => RecordingController.DrawBoard?.Clear(), "danger");
        _tools.Children.Add(_undoButton);
        _tools.Children.Add(_clearButton);
        stack.Children.Add(_tools);
        SyncTools();

        var card = new GlassPanel { Margin = new Thickness(16), Padding = new Thickness(6), Child = stack };
        card.Classes.Add("pill");
        card.Classes.Add("bar"); // капсула
        Content = card;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += (_, _) =>
        {
            if (!_recording)
            {
                _dot.Opacity = _dot.Opacity > 0.5 ? 0.3 : 0.8;
                return;
            }
            var e = _accum + _clock.Elapsed;
            _time.Text = e.TotalHours >= 1 ? e.ToString(@"h\:mm\:ss") : e.ToString(@"mm\:ss");
            if (!_paused) _dot.Opacity = _dot.Opacity > 0.5 ? 0.35 : 1;
        };

        Opened += (_, _) =>
        {
            PlatformServices.WindowHelper.ExcludeFromCapture(this);
            Reposition();
            Dispatcher.UIThread.Post(Reposition, DispatcherPriority.Loaded); // размер окна известен только после раскладки
            Opacity = 1;
            _timer.Start();
        };
    }

    /// <summary>Ручка слева: потяни — вся панель поедет за курсором, позиция запомнится.</summary>
    Control DragGrip()
    {
        var dots = new UniformGrid
        {
            Columns = 2, Rows = 3,
            Width = 8, Height = 13,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        for (int i = 0; i < 6; i++)
            dots.Children.Add(new Avalonia.Controls.Shapes.Ellipse
            {
                Width = 2.2, Height = 2.2,
                Fill = new SolidColorBrush(Color.Parse("#8E8E93")),
            });

        var grip = new Border
        {
            Width = 14, Height = 22,
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Color.FromArgb(26, 128, 128, 128)),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.SizeAll),
            Child = dots,
        };
        ToolTip.SetTip(grip, L.T("Потянуть — переставить панель"));

        Point? grab = null;
        grip.PointerPressed += (_, e) =>
        {
            grab = e.GetPosition(this);
            e.Pointer.Capture(grip);
            e.Handled = true;
        };
        grip.PointerMoved += (_, e) =>
        {
            if (grab is not Point start) return;
            var delta = e.GetPosition(this) - start;
            double dx = delta.X * _scaling, dy = delta.Y * _scaling;
            if (Math.Abs(dx) < 0.5 && Math.Abs(dy) < 0.5) return;
            _moved = true;
            Position = new PixelPoint(Position.X + (int)Math.Round(dx), Position.Y + (int)Math.Round(dy));
            e.Handled = true;
        };
        grip.PointerReleased += (_, e) =>
        {
            grab = null;
            e.Pointer.Capture(null);
        };

        return grip;
    }

    Button PauseButton()
    {
        var button = new Button { Content = UiKit.Icon("Icon.Pause"), IsEnabled = false };
        button.Classes.Add("tool");
        ToolTip.SetTip(button, L.T("Пауза"));
        button.Click += (_, _) => _ = RecordingController.TogglePauseAsync();
        return button;
    }

    void UpdatePauseVisual()
    {
        if (_pause?.Content is Avalonia.Controls.Shapes.Path path)
            path.Data = UiKit.Geometry(_paused ? "Icon.Play" : "Icon.Pause");
        if (_pause is not null) ToolTip.SetTip(_pause, _paused ? L.T("Продолжить запись") : L.T("Пауза"));
    }

    void ToggleTools()
    {        _tools.IsVisible = !_tools.IsVisible;
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
        _styleDot.Fill = new SolidColorBrush(RecordingController.DrawColor);
        _styleButton.Classes.Set("selected", RecordingController.StyleOpen);
    }

    /// <summary>Запись реально началась: красная точка, таймер с нуля, кнопки «Стоп» и «Пауза» активны.</summary>
    public void SetRecording(bool resume = false)
    {
        _recording = true;
        if (!resume)
        {
            _accum = TimeSpan.Zero;
            _time.Text = "00:00";
        }
        _clock.Restart();
        _paused = false;
        UpdatePauseVisual();
        _dot.Background = new SolidColorBrush(Color.Parse("#FF3B30"));
        _dot.Opacity = 1;
        if (_stop is not null) _stop.IsEnabled = true;
        if (_pause is not null) _pause.IsEnabled = true;
    }

    /// <summary>Пауза: таймер замирает, точка становится серой, кнопка превращается в «Продолжить».</summary>
    public void SetPaused(bool paused)
    {
        _paused = paused;
        if (paused)
        {
            _accum += _clock.Elapsed;
            _clock.Reset(); // не Stop: иначе Elapsed останется в сумме и время удвоится
        }
        // При возобновлении часы сознательно не запускаем: пока ffmpeg поднимается, таймер стоит,
        // а стартует его SetRecording — иначе секунда запуска отсчиталась бы и откатилась назад.

        _dot.Background = new SolidColorBrush(Color.Parse(paused ? "#8E8E93" : "#FF3B30"));
        _dot.Opacity = 1;
        UpdatePauseVisual();
    }

    void Reposition()
    {
        int w = (int)Math.Ceiling(ClientSize.Width * _scaling);
        int h = (int)Math.Ceiling(ClientSize.Height * _scaling);
        var screen = Screens.ScreenFromPoint(_region.Center) ?? Screens.Primary;
        var area = screen?.WorkingArea ?? new PixelRect(0, 0, 1920, 1080);

        // Раскладка ещё не посчитала размер окна — повторим чуть позже, иначе панель встанет в угол 0,0.
        if (w <= 0 || h <= 0)
        {
            if (_repositionTries++ < 3) Dispatcher.UIThread.Post(Reposition, DispatcherPriority.Loaded);
            return;
        }

        // Панель уже переставляли вручную в этой записи — держим её там, где её оставили.
        if (_moved)
        {
            Position = ClampTo(Position, area, w, h);
            return;
        }

        // Позиция из прошлой записи — только если она всё ещё на экране.
        if (SavedPosition() is PixelPoint saved && area.Contains(saved))
        {
            Position = ClampTo(saved, area, w, h);
            return;
        }

        // По умолчанию — снизу по центру экрана.
        Position = new PixelPoint(
            Math.Clamp(area.X + (area.Width - w) / 2, area.X, Math.Max(area.X, area.Right - w)),
            Math.Clamp(area.Bottom - h - (int)(24 * _scaling), area.Y, Math.Max(area.Y, area.Bottom - h)));
    }

    static PixelPoint ClampTo(PixelPoint point, PixelRect area, int w, int h) => new(
        Math.Clamp(point.X, area.X, Math.Max(area.X, area.Right - w)),
        Math.Clamp(point.Y, area.Y, Math.Max(area.Y, area.Bottom - h)));

    static PixelPoint? SavedPosition()
    {
        var text = AppServices.Settings.VideoHudPos;
        if (string.IsNullOrWhiteSpace(text)) return null;
        var parts = text.Split(',');
        if (parts.Length != 2 || !int.TryParse(parts[0], out var x) || !int.TryParse(parts[1], out var y)) return null;
        return new PixelPoint(x, y);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _timer.Stop();

        if (!_moved) return;
        AppServices.Settings.VideoHudPos = $"{Position.X},{Position.Y}";
        AppServices.SaveSettings();
    }
}
