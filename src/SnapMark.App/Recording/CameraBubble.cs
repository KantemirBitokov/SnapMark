using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using SnapMark.Core;
using SnapMark.Platform;
using SnapMark.Services;
using SnapMark.UI;

namespace SnapMark.Recording;

/// <summary>
/// Камера в углу видео: круглое «окошко» с веб-камерой поверх области записи — оно попадает в видео,
/// как и рисунки. Кадры даёт тот же ffmpeg (dshow / avfoundation / v4l2) — без лишних библиотек.
/// Перетаскивается мышью, колесо — размер, правый клик — форма и размер.
/// </summary>
public sealed class CameraBubble : Window
{
    const int CamSize = 360; // кадр с камеры — квадрат 360×360 (обрезка по центру)
    static CameraBubble? _current;

    readonly PixelRect _region;
    readonly Image _image = new() { Stretch = Stretch.UniformToFill };
    readonly Border _frame;
    readonly WriteableBitmap _bitmap = new(new PixelSize(CamSize, CamSize), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
    readonly TextBlock _placeholder;
    Process? _ffmpeg;
    volatile bool _running = true;
    double _size;
    bool _round;

    public static void Start(PixelRect region, double scaling)
    {
        Stop();
        var s = AppServices.Settings;
        var ffmpeg = VideoTools.Ffmpeg;
        if (ffmpeg is null) return;
        _current = new CameraBubble(region, scaling, ffmpeg, s.VideoCameraDevice);
        _current.Show();
    }

    public static void Stop()
    {
        _current?.Close();
        _current = null;
    }

    public static void Raise()
    {
        if (_current is { IsVisible: true } c) PlatformServices.WindowHelper.BringToFront(c);
    }

    CameraBubble(PixelRect region, double scaling, string ffmpeg, string device)
    {
        _region = region;
        var s = AppServices.Settings;
        _size = Math.Clamp(s.VideoCameraSize, 100, 360);
        _round = s.VideoCameraRound;
        SystemDecorations = SystemDecorations.None;
        Topmost = true;
        ShowInTaskbar = false;
        CanResize = false;
        ShowActivated = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        Background = Brushes.Transparent;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        WindowStartupLocation = WindowStartupLocation.Manual;
        Title = L.T("SnapMark — камера");
        Cursor = new Cursor(StandardCursorType.SizeAll);

        _placeholder = new TextBlock
        {
            Text = L.T("Камера…"), Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(12),
        }.Style("callout");
        _image.Source = _bitmap;
        _frame = new Border
        {
            Child = new Grid { Children = { _placeholder, _image } },
            Background = new SolidColorBrush(Color.FromRgb(28, 28, 30)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(235, 255, 255, 255)),
            BorderThickness = new Thickness(3),
            ClipToBounds = true,
            BoxShadow = BoxShadows.Parse("0 8 24 0 #66000000"),
            Margin = new Thickness(14),
        };
        Content = _frame;
        ApplySize();
        ContextMenu = BuildMenu();

        Opened += (_, _) =>
        {
            // По умолчанию — правый нижний угол области записи.
            double sc = RenderScaling > 0 ? RenderScaling : scaling;
            int px = (int)((_size + 28) * sc);
            Position = new PixelPoint(region.Right - px - (int)(10 * sc), region.Bottom - px - (int)(10 * sc));
        };
        StartCapture(ffmpeg, device);
    }

    void ApplySize()
    {
        _frame.Width = _frame.Height = _size;
        _frame.CornerRadius = new CornerRadius(_round ? _size / 2 : _size * 0.18);
    }

    ContextMenu BuildMenu()
    {
        MenuItem Item(string header, Action act)
        {
            var m = new MenuItem { Header = header };
            m.Click += (_, _) => act();
            return m;
        }
        return new ContextMenu
        {
            Items =
            {
                Item(L.T("Маленькая"), () => SetSize(140)),
                Item(L.T("Средняя"), () => SetSize(200)),
                Item(L.T("Большая"), () => SetSize(280)),
                new Separator(),
                Item(L.T("Круг"), () => SetShape(true)),
                Item(L.T("Скруглённый квадрат"), () => SetShape(false)),
                new Separator(),
                Item(L.T("Скрыть камеру"), () => { Stop(); }),
            },
        };
    }

    void SetSize(double size)
    {
        _size = Math.Clamp(size, 100, 360);
        ApplySize();
        AppServices.Settings.VideoCameraSize = _size;
        AppServices.SaveSettings();
    }

    void SetShape(bool round)
    {
        _round = round;
        ApplySize();
        AppServices.Settings.VideoCameraRound = round;
        AppServices.SaveSettings();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        SetSize(_size * (e.Delta.Y > 0 ? 1.1 : 1 / 1.1));
        e.Handled = true;
    }

    // ── Кадры с камеры через ffmpeg (raw BGRA в stdout) ─────────────────────────
    void StartCapture(string ffmpeg, string device)
    {
        var args = new List<string> { "-hide_banner", "-loglevel", "error" };
        if (OperatingSystem.IsWindows())
        {
            if (string.IsNullOrEmpty(device)) device = ListCameras(ffmpeg).FirstOrDefault() ?? "";
            if (device.Length == 0) { ShowProblem(L.T("Камера не найдена")); return; }
            args.AddRange(new[] { "-f", "dshow", "-rtbufsize", "64M", "-i", "video=" + device });
        }
        else if (OperatingSystem.IsMacOS())
        {
            var cams = ListCameras(ffmpeg);
            int index = Math.Max(0, string.IsNullOrEmpty(device) ? 0 : cams.IndexOf(device));
            args.AddRange(new[] { "-f", "avfoundation", "-framerate", "30", "-i", index + ":none" });
        }
        else
        {
            args.AddRange(new[] { "-f", "v4l2", "-i", string.IsNullOrEmpty(device) ? "/dev/video0" : device });
        }
        args.AddRange(new[]
        {
            // Квадрат по центру, зеркально (как в зеркале — привычнее), 30 кадров в секунду.
            "-vf", $"crop='min(iw,ih)':'min(iw,ih)',scale={CamSize}:{CamSize},hflip,fps=30",
            "-pix_fmt", "bgra", "-f", "rawvideo", "pipe:1",
        });

        var psi = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            _ffmpeg = Process.Start(psi);
        }
        catch (Exception ex)
        {
            ShowProblem(ex.Message);
            return;
        }
        if (_ffmpeg is null) return;
        var errTask = _ffmpeg.StandardError.ReadToEndAsync();
        var stream = _ffmpeg.StandardOutput.BaseStream;
        new Thread(() =>
        {
            var frame = new byte[CamSize * CamSize * 4];
            bool any = false;
            try
            {
                while (_running)
                {
                    int read = 0;
                    while (read < frame.Length)
                    {
                        int n = stream.Read(frame, read, frame.Length - read);
                        if (n <= 0) { read = -1; break; }
                        read += n;
                    }
                    if (read < 0) break;
                    using (var fb = _bitmap.Lock())
                        for (int y = 0; y < CamSize; y++)
                            Marshal.Copy(frame, y * CamSize * 4, fb.Address + y * fb.RowBytes, CamSize * 4);
                    if (!any)
                    {
                        any = true;
                        Dispatcher.UIThread.Post(() => _placeholder.IsVisible = false);
                    }
                    Dispatcher.UIThread.Post(() => _image.InvalidateVisual(), DispatcherPriority.Render);
                }
            }
            catch { }
            if (_running)
            {
                // ffmpeg завершился сам — камера занята другой программой, нет разрешения и т. п.
                var err = errTask.IsCompleted ? errTask.Result : "";
                var line = err.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0);
                Dispatcher.UIThread.Post(() => ShowProblem(any ? L.T("Камера отключилась") : L.T("Камера недоступна") + (line is null ? "" : "\n" + line)));
            }
        }) { IsBackground = true, Name = "SnapMark camera" }.Start();
    }

    void ShowProblem(string text)
    {
        _placeholder.Text = text;
        _placeholder.IsVisible = true;
        _image.IsVisible = false;
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _running = false;
        try { _ffmpeg?.Kill(true); } catch { }
        _ffmpeg?.Dispose();
        if (ReferenceEquals(_current, this)) _current = null;
    }

    /// <summary>Камеры, которые видит ffmpeg.</summary>
    public static List<string> ListCameras(string ffmpeg)
    {
        var result = new List<string>();
        try
        {
            var psi = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            psi.ArgumentList.Add("-hide_banner");
            if (OperatingSystem.IsWindows())
                foreach (var a in new[] { "-list_devices", "true", "-f", "dshow", "-i", "dummy" }) psi.ArgumentList.Add(a);
            else if (OperatingSystem.IsMacOS())
                foreach (var a in new[] { "-f", "avfoundation", "-list_devices", "true", "-i", "" }) psi.ArgumentList.Add(a);
            else
            {
                result.AddRange(Directory.GetFiles("/dev", "video*").OrderBy(x => x));
                return result;
            }
            using var p = Process.Start(psi)!;
            var stderr = p.StandardError.ReadToEnd();
            p.WaitForExit(5000);
            if (OperatingSystem.IsWindows())
            {
                foreach (Match m in Regex.Matches(stderr, "\"([^\"]+)\"\\s+\\(video\\)"))
                    result.Add(m.Groups[1].Value);
            }
            else
            {
                bool video = false;
                foreach (var line in stderr.Split('\n'))
                {
                    if (line.Contains("video devices", StringComparison.OrdinalIgnoreCase)) { video = true; continue; }
                    if (line.Contains("audio devices", StringComparison.OrdinalIgnoreCase)) break;
                    if (!video) continue;
                    var m = Regex.Match(line, @"\[(\d+)\]\s+(.+)$");
                    if (m.Success && !m.Groups[2].Value.StartsWith("Capture screen", StringComparison.OrdinalIgnoreCase))
                        result.Add(m.Groups[2].Value.Trim());
                }
            }
        }
        catch { }
        return result;
    }
}
