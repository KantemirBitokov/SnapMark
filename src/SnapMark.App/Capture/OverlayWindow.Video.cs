using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SnapMark.Core;
using SnapMark.Services;
using SnapMark.Settings;

namespace SnapMark.Capture;

/// <summary>Панель записи видео: параметры прямо рядом с кнопкой «Начать запись» и надёжный старт.</summary>
public partial class OverlayWindow
{
    bool _recordRequested;
    bool _devicesLoaded;
    bool _syncingVideo;

    void InitVideoOptions()
    {
        // Нажатие на «Начать запись» ловим на самом раннем этапе (туннель, даже если событие уже обработано),
        // чтобы клик не «терялся» из-за поверхности захвата, фокуса или всплывающей подсказки.
        AddHandler(PointerPressedEvent, OnPreviewPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);

        VideoMicCombo.SelectionChanged += (_, _) =>
        {
            if (_syncingVideo || VideoMicCombo.SelectedItem is not ComboBoxItem { Tag: string id }) return;
            AppServices.Settings.MicrophoneDevice = id;
            AppServices.SaveSettings();
        };
        VideoSystemCombo.SelectionChanged += (_, _) =>
        {
            if (_syncingVideo || VideoSystemCombo.SelectedItem is not ComboBoxItem { Tag: string id }) return;
            AppServices.Settings.SystemAudioDevice = id;
            AppServices.SaveSettings();
        };
        // Выбор устройств есть только там, где мы сами пишем звук (Windows/WASAPI).
        VideoDevicesBox.IsVisible = OperatingSystem.IsWindows();

        // Точный размер области: поля показывают текущий размер, Enter — применить.
        Surface.SelectionChanged += SyncVideoSize;
        foreach (var box in new[] { VideoWidthBox, VideoHeightBox })
            box.KeyDown += (_, e) =>
            {
                if (e.Key != Key.Enter) return;
                e.Handled = true;
                ApplyVideoSize();
            };
        SyncVideoOptions();
    }

    void OnVideoCursorChip(object? sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: string t })
        {
            AppServices.Settings.VideoShowCursor = t == "Show";
            AppServices.SaveSettings();
        }
        SyncVideoOptions();
    }

    void OnVideoEffectsChip(object? sender, RoutedEventArgs e)
    {
        var s = AppServices.Settings;
        s.VideoHighlightClicks = VideoClicksChip.IsChecked == true;
        s.VideoShowKeyCombos = VideoKeysChip.IsChecked == true;
        AppServices.SaveSettings();
        SyncVideoOptions();
    }

    void OnVideoQualityChip(object? sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: string t } && Enum.TryParse<VideoQuality>(t, out var q))
        {
            AppServices.Settings.VideoQuality = q;
            AppServices.SaveSettings();
        }
        SyncVideoOptions();
    }

    void OnVideoSizePreset(object? sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: string t } && t.Split('x') is [var ws, var hs]
            && int.TryParse(ws, out var w) && int.TryParse(hs, out var h))
            SetVideoSize(w, h);
        else SyncVideoSize();
    }

    void OnVideoSizeApply(object? sender, RoutedEventArgs e) => ApplyVideoSize();

    void ApplyVideoSize()
    {
        if (int.TryParse(VideoWidthBox.Text?.Trim(), out var w) && int.TryParse(VideoHeightBox.Text?.Trim(), out var h) && w >= 16 && h >= 16)
            SetVideoSize(w, h);
        else SyncVideoSize(); // ерунда в полях — вернуть текущий размер
    }

    /// <summary>
    /// Область записи ровно w×h пикселей: центр остаётся на месте, область не выходит за экран.
    /// Видео пишется в чётных размерах (так требуют кодеки), поэтому нечётное значение округляется вниз.
    /// </summary>
    void SetVideoSize(int w, int h)
    {
        if (Surface.Screenshot is not { } shot) return;
        w &= ~1;
        h &= ~1;
        int sw = shot.PixelSize.Width & ~1, sh = shot.PixelSize.Height & ~1;
        bool clipped = w > sw || h > sh;
        w = Math.Min(w, sw);
        h = Math.Min(h, sh);
        var c = Surface.Selection is PixelRect cur
            ? new PixelPoint(cur.X + cur.Width / 2, cur.Y + cur.Height / 2)
            : new PixelPoint(sw / 2, sh / 2);
        Surface.SetSelection(new PixelRect(c.X - w / 2, c.Y - h / 2, w, h));
        VideoSizeNote.IsVisible = clipped;
        VideoSizeNote.Text = clipped ? L.F("Экран меньше — область уменьшена до {0} × {1}.", w, h) : "";
    }

    /// <summary>Поля «ширина × высота» и подсветка готового размера — по текущей области.</summary>
    void SyncVideoSize()
    {
        if (_mode != CaptureMode.VideoRegion) return;
        if (Surface.Selection is not PixelRect sel)
        {
            VideoWidthBox.Text = VideoHeightBox.Text = "";
            CheckChips(VideoSizeChips, "");
            return;
        }
        int w = sel.Width & ~1, h = sel.Height & ~1;
        if (!VideoWidthBox.IsFocused) VideoWidthBox.Text = w.ToString();
        if (!VideoHeightBox.IsFocused) VideoHeightBox.Text = h.ToString();
        CheckChips(VideoSizeChips, $"{w}x{h}");
    }

    void OnPreviewPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        CloseFloatingPanelsOnOutsideClick(e);

        if (_mode != CaptureMode.VideoRegion || !VideoBar.IsVisible || !RecordButton.IsVisible) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var p = e.GetPosition(RecordButton);
        if (p.X < 0 || p.Y < 0 || p.X > RecordButton.Bounds.Width || p.Y > RecordButton.Bounds.Height) return;
        e.Handled = true;
        StartRecording();
    }

    /// <summary>
    /// Всплывающие панели (стиль, фигуры, параметры записи) закрываются кликом в любое место вне их —
    /// как поповеры в macOS. Клик при этом не «съедается»: можно сразу рисовать или нажать другую кнопку.
    /// Клик по кнопке, открывающей панель, не трогаем — она сама переключит панель.
    /// </summary>
    void CloseFloatingPanelsOnOutsideClick(PointerPressedEventArgs e)
    {
        bool changed = false;
        foreach (var (panel, toggle) in new (Control Panel, Control Toggle)[]
                 {
                     (StylePanel, StyleButton),
                     (ShapesPanel, ShapesMenuButton),
                     (VideoOptionsPanel, VideoOptionsButton),
                 })
        {
            if (!panel.IsVisible || IsInside(e.Source, panel) || IsInside(e.Source, toggle)) continue;
            panel.IsVisible = false;
            changed = true;
        }
        if (changed) LayoutBars();
    }

    /// <summary>Источник клика лежит внутри control (по визуальному или логическому дереву — для выпадающих списков).</summary>
    static bool IsInside(object? source, Control control)
    {
        for (var v = source as Avalonia.Visual; v is not null; v = v.GetVisualParent() ?? (v as StyledElement)?.Parent as Avalonia.Visual)
            if (ReferenceEquals(v, control)) return true;
        return false;
    }

    void OnVideoOptionsClick(object? sender, RoutedEventArgs e)
    {
        VideoOptionsPanel.IsVisible = !VideoOptionsPanel.IsVisible;
        if (VideoOptionsPanel.IsVisible && !_devicesLoaded) _ = LoadVideoDevicesAsync();
        LayoutBars();
    }

    async Task LoadVideoDevicesAsync()
    {
        _devicesLoaded = true;
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var mics = await Task.Run(() => Platform.Windows.WinAudioDevices.List(render: false));
            var outputs = await Task.Run(() => Platform.Windows.WinAudioDevices.List(render: true));
            _syncingVideo = true;
            SettingsWindow.FillDevices(VideoMicCombo, mics, AppServices.Settings.MicrophoneDevice, L.T("По умолчанию (системный микрофон)"));
            SettingsWindow.FillDevices(VideoSystemCombo, outputs, AppServices.Settings.SystemAudioDevice, L.T("По умолчанию (основные колонки/наушники)"));
        }
        catch
        {
            VideoDevicesBox.IsVisible = false;
        }
        finally
        {
            _syncingVideo = false;
        }
        Dispatcher.UIThread.Post(LayoutBars, DispatcherPriority.Loaded);
    }

    void OnVideoFormatChip(object? sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: string t } && Enum.TryParse<VideoFileFormat>(t, out var f))
        {
            AppServices.Settings.VideoFormat = f;
            AppServices.SaveSettings();
        }
        SyncVideoOptions();
    }

    void OnVideoFpsChip(object? sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: string t } && int.TryParse(t, out var fps))
        {
            AppServices.Settings.VideoFps = fps;
            AppServices.SaveSettings();
        }
        SyncVideoOptions();
    }

    void OnVideoAudioChip(object? sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: string t } && Enum.TryParse<AudioSource>(t, out var a))
        {
            _withMic = a is AudioSource.Microphone or AudioSource.MicrophoneAndSystem;
            _withSystem = a is AudioSource.System or AudioSource.MicrophoneAndSystem;
            UpdateMicButton();
            SaveAudioChoice();
        }
        SyncVideoOptions();
    }

    /// <summary>Чипы, подпись «MP4 · 30 fps» и доступность звука — по текущим настройкам.</summary>
    void SyncVideoOptions()
    {
        var s = AppServices.Settings;
        CheckChips(VideoFormatChips, s.VideoFormat.ToString());
        CheckChips(VideoFpsChips, s.VideoFps.ToString());
        CheckChips(VideoQualityChips, s.VideoQuality.ToString());
        CheckChips(VideoCursorChips, s.VideoShowCursor ? "Show" : "Hide");
        VideoClicksChip.IsChecked = s.VideoHighlightClicks;
        VideoKeysChip.IsChecked = s.VideoShowKeyCombos;
        SyncVideoSize();
        var audio = (_withMic, _withSystem) switch
        {
            (true, true) => AudioSource.MicrophoneAndSystem,
            (true, false) => AudioSource.Microphone,
            (false, true) => AudioSource.System,
            _ => AudioSource.None,
        };
        CheckChips(VideoAudioChips, audio.ToString());

        bool gif = s.VideoFormat == VideoFileFormat.Gif;
        GifAudioNote.IsVisible = gif;
        VideoAudioChips.IsEnabled = !gif;
        MicButton.IsEnabled = SpeakerButton.IsEnabled = !gif;

        var name = s.VideoFormat switch
        {
            VideoFileFormat.Mkv => "MKV",
            VideoFileFormat.WebM => "WebM",
            VideoFileFormat.Gif => "GIF",
            _ => "MP4",
        };
        VideoFormatText.Text = $"{name} · {s.VideoFps} fps";
        Dispatcher.UIThread.Post(LayoutBars, DispatcherPriority.Loaded);
    }

    /// <summary>Рабочая область экрана (без панели задач) в логических координатах окна.</summary>
    Rect WorkArea(double W, double H)
    {
        if (_screen is null) return new Rect(0, 0, W, H);
        var b = _screen.Bounds;
        var wa = _screen.WorkingArea;
        double s = _screen.Scaling;
        var r = new Rect((wa.X - b.X) / s, (wa.Y - b.Y) / s, wa.Width / s, wa.Height / s);
        return r.Width < 100 || r.Height < 100 ? new Rect(0, 0, W, H) : r.Intersect(new Rect(0, 0, W, H));
    }

    /// <summary>Панель записи и её параметры — у выделенной области, но не под панелью задач Windows.</summary>
    void LayoutVideoBars(Rect v, double W, double H)
    {
        var wa = WorkArea(W, H);
        double top = wa.Top + 8, bottom = wa.Bottom - 8, left = wa.Left + 8, right = wa.Right - 8;

        VideoBar.IsVisible = true;
        var vb = MeasureOf(VideoBar);
        double vx = Clamp(v.Center.X - vb.Width / 2, left, right - vb.Width);
        double vy = v.Bottom + 10;
        bool below = true;
        if (vy + vb.Height > bottom) { vy = v.Top - vb.Height - 10; below = false; }
        if (vy < top) { vy = v.Bottom - vb.Height - 16; below = false; }
        vy = Clamp(vy, top, bottom - vb.Height);
        Place(VideoBar, vx, vy);

        if (!VideoOptionsPanel.IsVisible) return;
        var op = MeasureOf(VideoOptionsPanel);
        double anchorX = vx + vb.Width / 2;
        if (VideoOptionsButton.TranslatePoint(new Point(VideoOptionsButton.Bounds.Width / 2, 0), Layer) is Point o) anchorX = o.X;
        double ox = Clamp(anchorX - op.Width / 2, left, right - op.Width);
        double oy = below ? vy + vb.Height + 8 : vy - op.Height - 8;
        if (oy + op.Height > bottom || oy < top) oy = below ? vy - op.Height - 8 : vy + vb.Height + 8;
        oy = Clamp(oy, top, bottom - op.Height);
        Place(VideoOptionsPanel, ox, oy);
    }
}
