using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using SnapMark.Annotations;
using SnapMark.Capture;
using SnapMark.Core;
using SnapMark.Export;
using SnapMark.Platform;
using SnapMark.Recording;
using SnapMark.Services;
using SnapMark.UI;

namespace SnapMark.Steps;

/// <summary>
/// Пошаговая инструкция одной кнопкой: включаешь режим и работаешь как обычно — на каждый клик
/// SnapMark снимает экран под курсором и ставит метку A, B, C… со стрелкой на место нажатия.
/// Шаги складываются в серию; «Готово» — вся инструкция (картинки + «Screenshot_1_A — Клик: …»)
/// сразу в буфере обмена, вставляется одним Ctrl+V.
///
/// На Windows кадр снимается прямо в хуке мыши — до того, как программа получила клик,
/// поэтому на картинке видно именно то, на что нажали (меню ещё не открылось и т. п.).
/// </summary>
public static class StepRecorder
{
    sealed record ScreenInfo(PixelRect Bounds, double Scaling);

    static IDisposable? _input;
    static StepHud? _hud;
    static List<ScreenInfo> _screens = new();
    static int _steps;
    static int _pending;
    static volatile bool _paused;
    static volatile bool _overHud;
    static long _lastClickMs;
    static readonly Stopwatch Clock = Stopwatch.StartNew();

    public static bool IsActive => _input is not null;

    // Журнал для автотеста в CI (SNAPMARK_TEST_INPUT=1): %TEMP%\SnapMark\steps.log.
    static readonly bool Diag = Environment.GetEnvironmentVariable("SNAPMARK_TEST_INPUT") == "1";
    static void Log(string line)
    {
        if (!Diag) return;
        try
        {
            Directory.CreateDirectory(TempFiles.Root);
            File.AppendAllText(Path.Combine(TempFiles.Root, "steps.log"), $"{DateTime.Now:HH:mm:ss.fff} {line}{Environment.NewLine}");
        }
        catch { }
    }
    public static int Count => _steps;

    public static void Toggle()
    {
        if (IsActive) Finish();
        else Start();
    }

    public static void Start()
    {
        if (IsActive) return;
        if (CaptureController.IsActive) CaptureController.CloseAll();
        var monitor = InputMonitor.Instance;
        Log($"start supported={monitor.IsSupported} problem={monitor.Problem}");
        if (!monitor.IsSupported)
        {
            Toast.Show(L.T("Пошаговая инструкция недоступна"), L.T(monitor.Problem ?? ""));
            return;
        }

        _screens = App.GetScreens().Select(s => new ScreenInfo(s.Bounds, s.Scaling > 0 ? s.Scaling : 1)).ToList();
        _steps = 0;
        _paused = false;
        _overHud = false;
        monitor.Click += OnClick;
        _input = monitor.Acquire();
        if (monitor.Problem is { } problem)
        {
            Stop();
            Toast.Show(L.T("Пошаговая инструкция недоступна"), L.T(problem),
                OperatingSystem.IsMacOS() ? L.T("Открыть настройки") : null,
                OperatingSystem.IsMacOS() ? () => ShellOpen.Open("x-apple.systempreferences:com.apple.preference.security?Privacy_ListenEvent") : null);
            return;
        }

        _hud = new StepHud();
        _hud.Show();
        Log($"started screens={_screens.Count} problem={monitor.Problem}");
        Toast.Show(L.T("Пошаговая инструкция"),
            L.F("Работай как обычно: каждый клик — шаг с меткой. «Готово» на панели или {0} — вся инструкция в буфере обмена.",
                AppServices.Settings.StepGuideHotkey));
    }

    /// <summary>Закончить: вся серия — в буфер обмена (картинки + текст шагов).</summary>
    public static async void Finish()
    {
        if (!IsActive) return;
        Stop();
        // Дожидаемся кадров, которые ещё обрабатываются.
        for (int i = 0; i < 40 && Volatile.Read(ref _pending) > 0; i++) await Task.Delay(50);
        Log($"finish steps={_steps} series={AppServices.Series.Shots.Count}");
        if (_steps == 0)
        {
            Toast.Show(L.T("Пошаговая инструкция"), L.T("Ни одного шага: кликни по программе, пока режим включён."));
            return;
        }
        try
        {
            await AppServices.Series.CopyAllAsync();
            Toast.Show(L.F("Инструкция готова · шагов: {0}", _steps),
                SmartPaste.IsArmed
                    ? L.T("Ctrl+V вставит шаги вместе с подписями — в чат, документ или Word.")
                    : L.T("Вставь в чат или документ. Шаги — на панели серии: там можно подписать и удалить лишние."));
        }
        catch (Exception ex)
        {
            Toast.Show(L.T("Не удалось скопировать"), ex.Message);
        }
    }

    /// <summary>Выйти без копирования (шаги остаются в серии).</summary>
    public static void Cancel()
    {
        if (!IsActive) return;
        Stop();
        if (_steps > 0)
            Toast.Show(L.T("Пошаговая инструкция остановлена"), L.F("Шагов в серии: {0}. Их можно подписать на панели серии.", _steps));
    }

    static void Stop()
    {
        InputMonitor.Instance.Click -= OnClick;
        _input?.Dispose();
        _input = null;
        _hud?.Close();
        _hud = null;
    }

    public static bool Paused
    {
        get => _paused;
        set
        {
            _paused = value;
            _hud?.Sync();
        }
    }

    internal static void SetOverHud(bool over) => _overHud = over;

    // ── Клик (фоновый поток; на Windows — прямо из хука мыши) ─────────────────────────────────
    static void OnClick(InputClick click)
    {
        Log($"click {click} paused={_paused} overHud={_overHud} active={IsActive}");
        if (_paused || _overHud || !IsActive) return;
        if (CaptureController.IsActive || RecordingController.IsRecording) return;
        long now = Clock.ElapsedMilliseconds;
        if (now - Interlocked.Read(ref _lastClickMs) < 350) return; // двойной щелчок — один шаг
        Interlocked.Exchange(ref _lastClickMs, now);

        var monitor = InputMonitor.Instance;
        var screen = FindScreen(click, monitor.LogicalCoordinates, out var local);
        if (screen is null) { Log("no screen for click"); return; }
        string? title = AppServices.Settings.StepsWindowTitles ? WindowTitles.At(click) : null;

        Interlocked.Increment(ref _pending);
        if (OperatingSystem.IsWindows())
        {
            // Быстрый снимок GDI — ещё до того, как программа обработает клик.
            Bitmap? shot = null;
            try { shot = PlatformServices.ScreenCapture.Capture(screen.Bounds); } catch { }
            Dispatcher.UIThread.Post(() => AddStep(shot, screen, local, click.Button, title));
        }
        else
        {
            // macOS / Linux: снимок через системные средства медленный — не держим поток событий.
            Task.Run(() =>
            {
                Bitmap? shot = null;
                try { shot = PlatformServices.ScreenCapture.Capture(screen.Bounds); } catch { }
                Dispatcher.UIThread.Post(() => AddStep(shot, screen, local, click.Button, title));
            });
        }
    }

    /// <summary>Монитор под кликом и позиция клика внутри него (доли ширины и высоты 0…1).</summary>
    static ScreenInfo? FindScreen(InputClick c, bool logical, out Point local)
    {
        foreach (var s in _screens)
        {
            var b = s.Bounds;
            Rect r = logical
                ? new Rect(b.X / s.Scaling, b.Y / s.Scaling, b.Width / s.Scaling, b.Height / s.Scaling)
                : new Rect(b.X, b.Y, b.Width, b.Height);
            if (r.Contains(new Point(c.X, c.Y)))
            {
                local = new Point((c.X - r.X) / r.Width, (c.Y - r.Y) / r.Height);
                return s;
            }
        }
        local = default;
        return null;
    }

    static void AddStep(Bitmap? shot, ScreenInfo screen, Point local, ClickButton button, string? title)
    {
        try
        {
            Log($"add shot={(shot is null ? "null" : shot.PixelSize.ToString())} title={title}");
            if (shot is null) return;
            int w = shot.PixelSize.Width, h = shot.PixelSize.Height;
            var p = new Point(Math.Clamp(local.X * w, 0, w - 1), Math.Clamp(local.Y * h, 0, h - 1));
            // Размер метки — как в редакторе на этом мониторе (снимок в физических пикселях).
            double k = w / (double)Math.Max(1, screen.Bounds.Width) * screen.Scaling;
            var s = AppServices.Settings;
            var color = Color.TryParse(s.LastColor, out var c) ? c : Color.Parse("#FF3B30");

            // Кольцо на месте клика и метка со стрелкой к нему (метка сдвинута, чтобы не закрывать цель).
            var ring = new EllipseAnnotation
            {
                Start = new Point(p.X - 17 * k, p.Y - 17 * k),
                End = new Point(p.X + 17 * k, p.Y + 17 * k),
                Color = color,
                Thickness = 3.5 * k,
            };
            double off = 78 * k;
            double dx = p.X - off < 24 * k ? off : -off;
            double dy = p.Y - off < 24 * k ? off : -off;
            var label = s.MarkerNumbering == MarkerNumbering.Numbers
                ? (_steps + 1).ToString()
                : AnnotationDocument.IndexToLabel(_steps);
            var marker = new MarkerAnnotation
            {
                Label = label,
                Center = new Point(p.X + dx, p.Y + dy),
                Tail = MoveTowards(p, new Point(p.X + dx, p.Y + dy), 19 * k),
                Color = color,
                FontSize = Math.Max(20, s.LastFontSize) * k,
                Shape = s.MarkerShape,
                Glass = s.MarkerGlass,
            };

            byte[] png;
            using (var rtb = ImageRenderer.Render(shot, new PixelRect(0, 0, w, h), new Annotation[] { ring, marker }))
                png = ImageEncoder.ToPng(rtb);

            var what = button switch
            {
                ClickButton.Right => L.T("Правый клик"),
                ClickButton.Middle => L.T("Клик колесом"),
                _ => L.T("Клик"),
            };
            var comment = string.IsNullOrWhiteSpace(title) ? what : $"{what}: {title}";
            _steps++;
            AppServices.Series.Add(png, w, h, new List<SeriesMarker> { new() { Label = label, Comment = comment } });
            _hud?.Sync();
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
        finally
        {
            shot?.Dispose();
            Interlocked.Decrement(ref _pending);
        }
    }

    static Point MoveTowards(Point from, Point to, double distance)
    {
        double vx = to.X - from.X, vy = to.Y - from.Y;
        double len = Math.Sqrt(vx * vx + vy * vy);
        return len < 1e-6 ? from : new Point(from.X + vx / len * distance, from.Y + vy / len * distance);
    }
}

/// <summary>Название окна под точкой клика — подпись шага («Клик: Документ — Word»).</summary>
internal static class WindowTitles
{
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    public static string? At(InputClick c)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            var hwnd = WindowFromPoint(new POINT { X = (int)c.X, Y = (int)c.Y });
            if (hwnd == IntPtr.Zero) return null;
            var root = GetAncestor(hwnd, 2 /*GA_ROOT*/);
            var sb = new StringBuilder(256);
            GetWindowText(root == IntPtr.Zero ? hwnd : root, sb, sb.Capacity);
            var title = sb.ToString().Trim();
            if (title.Length == 0)
            {
                GetWindowThreadProcessId(root, out uint pid);
                using var p = Process.GetProcessById((int)pid);
                title = p.ProcessName;
            }
            if (title is "Program Manager") title = L.T("Рабочий стол");
            return title.Length > 80 ? title[..80] + "…" : title;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>Панель режима: счётчик шагов, пауза, «Готово», выход. В скриншоты не попадает.</summary>
public sealed class StepHud : Window
{
    readonly TextBlock _count = new TextBlock { VerticalAlignment = VerticalAlignment.Center }.Style("headline");
    readonly Border _dot;
    readonly Button _pause;
    readonly DispatcherTimer _blink;

    public StepHud()
    {
        UiKit.MakeFloating(this);
        ShowActivated = false;
        Opacity = 0;
        Title = L.T("SnapMark — пошаговая инструкция");

        _dot = new Border
        {
            Width = 10, Height = 10, CornerRadius = new CornerRadius(5),
            Background = new SolidColorBrush(Color.Parse("#FF3B30")),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0),
        };
        _pause = UiKit.ToolButton("Icon.Pause", L.T("Пауза: клики не записываются"), () => StepRecorder.Paused = !StepRecorder.Paused);

        var done = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 6,
                Children =
                {
                    new Avalonia.Controls.Shapes.Path
                    {
                        Data = UiKit.Geometry("Icon.Check"), Stroke = Brushes.White, StrokeThickness = 2,
                        StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round, Width = 20, Height = 20,
                        Stretch = Stretch.None, VerticalAlignment = VerticalAlignment.Center,
                    },
                    new TextBlock { Text = L.T("Готово"), VerticalAlignment = VerticalAlignment.Center },
                },
            },
        };
        done.Classes.Add("primary");
        ToolTip.SetTip(done, L.T("Закончить: вся инструкция — в буфер обмена"));
        done.Click += (_, _) => StepRecorder.Finish();

        var close = UiKit.ToolButton("Icon.Close", L.T("Выйти без копирования (шаги останутся в серии)"), StepRecorder.Cancel, "danger");

        var stack = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            Children = { _dot, _count, UiKit.VSep(), _pause, done, close },
        };
        var card = new GlassPanel { Margin = new Thickness(16), Padding = new Thickness(6), Child = stack };
        card.Classes.Add("pill");
        card.Classes.Add("bar");
        Content = card;

        // Клики по самой панели — не шаги.
        PointerEntered += (_, _) => StepRecorder.SetOverHud(true);
        PointerExited += (_, _) => StepRecorder.SetOverHud(false);

        _blink = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _blink.Tick += (_, _) => _dot.Opacity = StepRecorder.Paused ? 0.35 : _dot.Opacity > 0.5 ? 0.35 : 1;
        Opened += (_, _) =>
        {
            PlatformServices.WindowHelper.ExcludeFromCapture(this);
            Reposition();
            Opacity = 1;
            _blink.Start();
        };
        Sync();
    }

    public void Sync()
    {
        _count.Text = StepRecorder.Paused
            ? L.F("Пауза · шагов: {0}", StepRecorder.Count)
            : L.F("Шаги · {0}", StepRecorder.Count);
        _pause.Classes.Set("selected", StepRecorder.Paused);
        ToolTip.SetTip(_pause, StepRecorder.Paused ? L.T("Продолжить запись шагов") : L.T("Пауза: клики не записываются"));
        Dispatcher.UIThread.Post(Reposition, DispatcherPriority.Background);
    }

    /// <summary>Сверху по центру основного монитора — там панель меньше всего мешает.</summary>
    void Reposition()
    {
        var screen = Screens.Primary;
        if (screen is null) return;
        var area = screen.WorkingArea;
        double sc = screen.Scaling > 0 ? screen.Scaling : 1;
        int w = (int)Math.Ceiling(ClientSize.Width * sc);
        Position = new PixelPoint(area.X + (area.Width - w) / 2, area.Y + (int)(4 * sc));
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _blink.Stop();
        StepRecorder.SetOverHud(false);
    }
}
