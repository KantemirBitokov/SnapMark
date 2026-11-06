using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Platform.Storage;
using SnapMark.Core;
using SnapMark.Platform;
using SnapMark.Platform.Common;
using SnapMark.Services;

namespace SnapMark.Settings;

public partial class SettingsWindow : Window
{
    static SettingsWindow? _instance;

    static readonly string[] Titles =
        { L.T("Основные"), L.T("Горячие клавиши"), L.T("Скриншоты"), L.T("Серия и экспорт"), L.T("Видео"), L.T("О программе") };

    static readonly int[] FpsValues = { 15, 24, 30, 60 };

    bool _loading;

    public static void ShowSingle(int page = 0, bool startFfmpegDownload = false)
    {
        if (_instance is null)
        {
            _instance = new SettingsWindow();
            _instance.Show();
        }
        _instance.Nav.SelectedIndex = page;
        _instance.Activate();
        if (startFfmpegDownload) _ = _instance.DownloadFfmpegAsync();
    }

    public SettingsWindow()
    {
        InitializeComponent();
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Mica, WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.None };
        // Системное стекло (Mica / Acrylic): окно — полупрозрачная тонировка поверх размытого рабочего стола.
        Opened += (_, _) => ApplyGlassBackground();
        ActualThemeVariantChanged += (_, _) => ApplyGlassBackground();
        UI.Glass.TransparencyChanged += ApplyGlassBackground;
        Closed += (_, _) => UI.Glass.TransparencyChanged -= ApplyGlassBackground;

        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.1.0";
        VersionText.Text = L.T("Версия ") + version;
        AboutVersion.Text = L.T("Версия ") + version;
        SettingsPathText.Text = SettingsStore.FilePath;

        Nav.SelectionChanged += (_, _) => ShowPage(Nav.SelectedIndex);

        BuildFrameColors();
        LoadValues();
        WireEvents();
        ShowPage(0);
    }

    /// <summary>Система сама попросила меньше прозрачности или больше контраста — объясняем, почему стекло матовое.</summary>
    void UpdateSystemGlassNote()
    {
        string? note = UI.Glass.HighContrast ? L.T("В системе включён высокий контраст — панели показываются без стекла, с контрастным краем.")
            : UI.Glass.ReduceTransparency ? L.T("В системе выключены эффекты прозрачности — стекло показывается матовым, без размытия.")
            : null;
        SystemGlassNote.Text = note ?? "";
        SystemGlassNote.IsVisible = note is not null;
    }

    void ApplyGlassBackground()
    {
        if (UI.Glass.ReduceTransparency || UI.Glass.HighContrast
            || (ActualTransparencyLevel != WindowTransparencyLevel.Mica && ActualTransparencyLevel != WindowTransparencyLevel.AcrylicBlur))
        {
            ClearValue(BackgroundProperty); // системного стекла нет — обычный фон из стиля Window.sm
            return;
        }
        bool dark = ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark;
        double a = 0.9 - 0.55 * UI.Glass.Transparency;
        var tint = dark ? Avalonia.Media.Color.FromRgb(30, 30, 34) : Avalonia.Media.Color.FromRgb(246, 246, 250);
        Background = new Avalonia.Media.SolidColorBrush(tint, a);
    }

    protected override void OnClosed(EventArgs e)
    {
        StopMicTest();
        base.OnClosed(e);
        if (ReferenceEquals(_instance, this)) _instance = null;
    }

    void ShowPage(int index)
    {
        if (index < 0) return;
        if (index != 4) StopMicTest();
        var pages = new Control[] { PageGeneral, PageHotkeys, PageScreens, PageSeries, PageVideo, PageAbout };
        for (int i = 0; i < pages.Length; i++) pages[i].IsVisible = i == index;
        PageTitle.Text = Titles[index];

        if (index == 1) RefreshHotkeyInfo();
        if (index == 4) _ = RefreshVideoInfo();
    }

    static AppSettings S => AppServices.Settings;

    static void Save() => AppServices.SaveSettings();

    // ═══════════════════════════ Загрузка ═══════════════════════════
    void LoadValues()
    {
        _loading = true;

        LanguageCombo.Items.Clear();
        foreach (var (code, name) in L.Languages)
            LanguageCombo.Items.Add(new ComboBoxItem { Content = name, Tag = code });
        LanguageCombo.SelectedItem = LanguageCombo.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(i => (i.Tag as string) == L.Language) ?? LanguageCombo.Items[0];
        ThemeCombo.SelectedIndex = (int)S.Theme;
        GlassSlider.Value = S.GlassTransparency;
        GlassValue.Text = $"{S.GlassTransparency} %";
        FrostSlider.Value = S.GlassFrost;
        FrostValue.Text = $"{S.GlassFrost} %";
        GlassVariantCombo.SelectedIndex = UI.Glass.Variant == UI.GlassVariant.Clear ? 1 : 0;
        GlassAppearanceCombo.SelectedIndex = (int)UI.Glass.Appearance;
        UpdateSystemGlassNote();
        AutostartToggle.IsChecked = S.LaunchAtStartup;
        AutostartToggle.IsEnabled = PlatformServices.Autostart.IsSupported;
        NotifyToggle.IsChecked = S.ShowNotifications;
        UpdatesToggle.IsChecked = S.CheckForUpdates;

        HkRegion.Value = S.CaptureRegionHotkey.Clone();
        HkFull.Value = S.CaptureFullHotkey.Clone();
        HkVideo.Value = S.RecordVideoHotkey.Clone();
        HkSteps.Value = S.StepGuideHotkey.Clone();

        FormatCombo.SelectedIndex = (int)S.ImageFormat;
        QualitySlider.Value = S.ImageQuality;
        QualityValue.Text = S.ImageQuality.ToString();
        UpdateQualityRow();
        FolderText.Text = S.SaveFolder;
        NameTemplateBox.Text = S.FileNameTemplate;
        AutoSaveToggle.IsChecked = S.AutoSaveCopy;
        SearchCombo.SelectedIndex = (int)S.ImageSearch;

        LineTemplateBox.Text = S.SeriesLineTemplate;
        HeadersToggle.IsChecked = S.SeriesIncludeHeaders;
        HeaderTemplateBox.Text = S.SeriesHeaderTemplate;
        HeaderTemplateBox.IsEnabled = S.SeriesIncludeHeaders;
        ClipModeCombo.SelectedIndex = (int)S.SeriesClipboardMode;
        SmartPasteToggle.IsChecked = S.SmartPaste;
        SmartPasteToggle.IsEnabled = PlatformServices.Paste.IsSupported;
        UpdatePreview();

        VideoFormatCombo.SelectedIndex = (int)S.VideoFormat;
        FpsCombo.SelectedIndex = Math.Max(0, Array.IndexOf(FpsValues, S.VideoFps));
        QualityCombo.SelectedIndex = (int)S.VideoQuality;
        FrameToggle.IsChecked = S.ShowRecordingFrame;
        CursorToggle.IsChecked = S.VideoShowCursor;
        ClicksToggle.IsChecked = S.VideoHighlightClicks;
        KeysToggle.IsChecked = S.VideoShowKeyCombos;
        CameraToggle.IsChecked = S.VideoCamera;
        _ = FillCamerasAsync();
        AudioCombo.SelectedIndex = (int)S.Audio;
        // Звук компьютера: Windows — WASAPI, Linux — PulseAudio, macOS 13+ — ScreenCaptureKit (11–12 — BlackHole).
        SystemDeviceRow.IsVisible = OperatingSystem.IsWindows();
        FfmpegBox.Text = S.FfmpegPath;
        VideoFolderText.Text = S.VideoFolder;

        _loading = false;
    }

    // ═══════════════════════════ Обработчики ═══════════════════════════
    void WireEvents()
    {
        // Основные
        LanguageCombo.SelectionChanged += (_, _) =>
        {
            if (_loading || LanguageCombo.SelectedItem is not ComboBoxItem { Tag: string code } || code == S.Language) return;
            S.Language = code;
            Save();
            // Подсказка уже на новом языке; всё остальное переводится после перезапуска.
            L.Set(code);
            UI.Toast.Show(L.T("Язык изменён"), L.T("Перезапусти SnapMark, чтобы применить новый язык."),
                L.T("Перезапустить"), App.Restart);
        };
        // Прозрачность стекла: вид меняется сразу, на диск пишем, когда ползунок остановился.
        var glassSave = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        glassSave.Tick += (_, _) => { glassSave.Stop(); Save(); };
        GlassSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property != RangeBase.ValueProperty || _loading) return;
            int v = (int)Math.Round(GlassSlider.Value);
            S.GlassTransparency = v;
            GlassValue.Text = $"{v} %";
            UI.Glass.Transparency = v / 100.0;
            glassSave.Stop();
            glassSave.Start();
        };
        FrostSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property != RangeBase.ValueProperty || _loading) return;
            int v = (int)Math.Round(FrostSlider.Value);
            S.GlassFrost = v;
            FrostValue.Text = $"{v} %";
            UI.Glass.Frost = v / 100.0;
            glassSave.Stop();
            glassSave.Start();
        };
        GlassVariantCombo.SelectionChanged += (_, _) => Change(() =>
        {
            var v = GlassVariantCombo.SelectedIndex == 1 ? UI.GlassVariant.Clear : UI.GlassVariant.Regular;
            S.GlassVariant = v.ToString();
            UI.Glass.Variant = v;
        });
        GlassAppearanceCombo.SelectionChanged += (_, _) => Change(() =>
        {
            var a = (UI.GlassAppearance)Math.Clamp(GlassAppearanceCombo.SelectedIndex, 0, 2);
            S.GlassAppearance = a.ToString();
            UI.Glass.Appearance = a;
        });
        UI.Glass.TransparencyChanged += UpdateSystemGlassNote;
        Closed += (_, _) => UI.Glass.TransparencyChanged -= UpdateSystemGlassNote;
        ThemeCombo.SelectionChanged += (_, _) => Change(() =>
        {
            S.Theme = (ThemeMode)Math.Max(0, ThemeCombo.SelectedIndex);
            App.ApplyTheme();
        });
        AutostartToggle.IsCheckedChanged += (_, _) => Change(() =>
        {
            S.LaunchAtStartup = AutostartToggle.IsChecked == true;
            try { PlatformServices.Autostart.Set(S.LaunchAtStartup); } catch { }
        });
        NotifyToggle.IsCheckedChanged += (_, _) => Change(() => S.ShowNotifications = NotifyToggle.IsChecked == true);

        // Горячие клавиши
        HkRegion.ValueChanged += hk => SetHotkey(hk, h => S.CaptureRegionHotkey = h);
        HkFull.ValueChanged += hk => SetHotkey(hk, h => S.CaptureFullHotkey = h);
        HkVideo.ValueChanged += hk => SetHotkey(hk, h => S.RecordVideoHotkey = h);
        HkSteps.ValueChanged += hk => SetHotkey(hk, h => S.StepGuideHotkey = h);

        DisableSnippingButton.Click += (_, _) =>
        {
            var ok = PlatformServices.PrintScreenConflict.TryDisableSystemBinding();
            PrtScStatus.Text = ok
                ? L.T("Готово. Если «Ножницы» всё ещё открываются — выйди из системы и войди снова.")
                : L.T("Не получилось изменить настройку. Открой параметры Windows и выключи переключатель вручную.");
            PlatformServices.Hotkeys.Apply(S);
        };
        OpenWinSettingsButton.Click += (_, _) => PlatformServices.PrintScreenConflict.OpenSystemSettings();

        // Скриншоты
        FormatCombo.SelectionChanged += (_, _) => Change(() =>
        {
            S.ImageFormat = (ImageFileFormat)Math.Max(0, FormatCombo.SelectedIndex);
            UpdateQualityRow();
        });
        QualitySlider.PropertyChanged += (_, e) =>
        {
            if (e.Property != RangeBase.ValueProperty) return;
            QualityValue.Text = ((int)QualitySlider.Value).ToString();
            Change(() => S.ImageQuality = (int)QualitySlider.Value);
        };
        NameTemplateBox.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty) Change(() => S.FileNameTemplate = NameTemplateBox.Text ?? "");
        };
        AutoSaveToggle.IsCheckedChanged += (_, _) => Change(() => S.AutoSaveCopy = AutoSaveToggle.IsChecked == true);
        SearchCombo.SelectionChanged += (_, _) => Change(() => S.ImageSearch = (ImageSearchMethod)Math.Max(0, SearchCombo.SelectedIndex));
        OpenFolderButton.Click += (_, _) => ShellOpen.OpenFolder(S.SaveFolder);
        ChooseFolderButton.Click += async (_, _) =>
        {
            var path = await PickFolder(L.T("Папка для скриншотов"), S.SaveFolder);
            if (path is null) return;
            S.SaveFolder = path;
            FolderText.Text = path;
            Save();
        };

        // Серия
        LineTemplateBox.PropertyChanged += (_, e) =>
        {
            if (e.Property != TextBox.TextProperty) return;
            Change(() => S.SeriesLineTemplate = LineTemplateBox.Text ?? "");
            UpdatePreview();
        };
        HeaderTemplateBox.PropertyChanged += (_, e) =>
        {
            if (e.Property != TextBox.TextProperty) return;
            Change(() => S.SeriesHeaderTemplate = HeaderTemplateBox.Text ?? "");
            UpdatePreview();
        };
        HeadersToggle.IsCheckedChanged += (_, _) =>
        {
            Change(() => S.SeriesIncludeHeaders = HeadersToggle.IsChecked == true);
            HeaderTemplateBox.IsEnabled = HeadersToggle.IsChecked == true;
            UpdatePreview();
        };
        SmartPasteToggle.IsCheckedChanged += (_, _) => Change(() =>
        {
            S.SmartPaste = SmartPasteToggle.IsChecked == true;
            if (!S.SmartPaste) SmartPaste.Disarm();
        });
        ClipModeCombo.SelectionChanged += (_, _) =>
            Change(() => S.SeriesClipboardMode = (SeriesClipboardMode)Math.Max(0, ClipModeCombo.SelectedIndex));

        // Видео
        VideoFormatCombo.SelectionChanged += (_, _) => Change(() => S.VideoFormat = (VideoFileFormat)Math.Max(0, VideoFormatCombo.SelectedIndex));
        FpsCombo.SelectionChanged += (_, _) => Change(() => S.VideoFps = FpsValues[Math.Max(0, FpsCombo.SelectedIndex)]);
        QualityCombo.SelectionChanged += (_, _) => Change(() => S.VideoQuality = (VideoQuality)Math.Max(0, QualityCombo.SelectedIndex));
        FrameToggle.IsCheckedChanged += (_, _) => Change(() => S.ShowRecordingFrame = FrameToggle.IsChecked == true);
        CursorToggle.IsCheckedChanged += (_, _) => Change(() => S.VideoShowCursor = CursorToggle.IsChecked == true);
        ClicksToggle.IsCheckedChanged += (_, _) => Change(() => S.VideoHighlightClicks = ClicksToggle.IsChecked == true);
        KeysToggle.IsCheckedChanged += (_, _) => Change(() => S.VideoShowKeyCombos = KeysToggle.IsChecked == true);
        CameraToggle.IsCheckedChanged += (_, _) => Change(() => S.VideoCamera = CameraToggle.IsChecked == true);
        CameraCombo.SelectionChanged += (_, _) =>
        {
            if (_fillingCameras || CameraCombo.SelectedItem is not ComboBoxItem { Tag: string dev }) return;
            Change(() => S.VideoCameraDevice = dev);
        };
        AudioCombo.SelectionChanged += (_, _) => Change(() => S.Audio = (AudioSource)Math.Max(0, AudioCombo.SelectedIndex));
        MicCombo.SelectionChanged += (_, _) =>
        {
            Change(() =>
            {
                if (MicCombo.SelectedItem is ComboBoxItem { Tag: string id }) S.MicrophoneDevice = id;
            });
            if (_micMeter is not null) StartMicTest(); // слушаем уже новый микрофон
        };
        MicTestRow.IsVisible = OperatingSystem.IsWindows();
        MicTestButton.Click += (_, _) =>
        {
            if (_micMeter is null) StartMicTest();
            else StopMicTest();
        };
        SystemDeviceCombo.SelectionChanged += (_, _) => Change(() =>
        {
            if (SystemDeviceCombo.SelectedItem is ComboBoxItem { Tag: string id }) S.SystemAudioDevice = id;
        });
        RefreshMicsButton.Click += (_, _) => _ = RefreshVideoInfo();
        FfmpegBox.PropertyChanged += (_, e) =>
        {
            if (e.Property != TextBox.TextProperty) return;
            Change(() => S.FfmpegPath = FfmpegBox.Text?.Trim().Trim('"') ?? "");
            UpdateFfmpegStatus();
        };
        BrowseFfmpegButton.Click += async (_, _) =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = L.T("Где лежит ffmpeg"),
                AllowMultiple = false,
                FileTypeFilter = OperatingSystem.IsWindows()
                    ? new[] { new FilePickerFileType("ffmpeg.exe") { Patterns = new[] { "ffmpeg.exe", "*.exe" } } }
                    : null,
            });
            var path = files.FirstOrDefault()?.TryGetLocalPath();
            if (path is not null) FfmpegBox.Text = path;
        };
        OpenVideoFolderButton.Click += (_, _) => ShellOpen.OpenFolder(S.VideoFolder);
        ChooseVideoFolderButton.Click += async (_, _) =>
        {
            var path = await PickFolder(L.T("Папка для видео"), S.VideoFolder);
            if (path is null) return;
            S.VideoFolder = path;
            VideoFolderText.Text = path;
            Save();
        };

        // ffmpeg
        FfmpegInstallCard.IsVisible = FfmpegDownloader.IsSupported;
        DownloadFfmpegButton.Click += (_, _) => _ = DownloadFfmpegAsync();

        // О программе
        OpenSettingsFolderButton.Click += (_, _) => ShellOpen.OpenFolder(SettingsStore.Folder);
        SiteLink.Click += (_, _) => ShellOpen.Open("https://sadlybear.pro");
        UpdatesToggle.IsCheckedChanged += (_, _) => Change(() => S.CheckForUpdates = UpdatesToggle.IsChecked == true);
        CheckUpdatesButton.Click += async (_, _) =>
        {
            CheckUpdatesButton.IsEnabled = false;
            UpdateStatusText.Text = L.T("Проверяю…");
            var r = await UpdateChecker.CheckAsync();
            CheckUpdatesButton.IsEnabled = true;
            if (!r.Ok)
                UpdateStatusText.Text = L.T("Не удалось проверить: нет связи с GitHub.");
            else if (r.IsNewer)
            {
                UpdateStatusText.Text = L.F("Доступна версия {0}", r.Latest);
                UpdateChecker.ShowAvailable(r);
            }
            else
                UpdateStatusText.Text = L.F("У тебя последняя версия ({0}).", UpdateChecker.CurrentVersion);
        };
    }

    static readonly string[] FrameColorChoices =
        { "#0A84FF", "#FFFFFF", "#34C759", "#FFCC00", "#FF9500", "#FF3B30", "#AF52DE", "#FF2D55", "#8E8E93" };

    void BuildFrameColors()
    {
        foreach (var hex in FrameColorChoices)
        {
            var btn = new Button { Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(hex)), Tag = hex };
            btn.Classes.Add("swatch");
            ToolTip.SetTip(btn, hex);
            btn.Click += (_, _) =>
            {
                S.SelectionColor = hex;
                Save();
                UpdateFrameColors();
            };
            FrameColors.Children.Add(btn);
        }
        UpdateFrameColors();
    }

    void UpdateFrameColors()
    {
        foreach (var b in FrameColors.Children.OfType<Button>())
            b.Classes.Set("current", string.Equals(b.Tag as string, S.SelectionColor, StringComparison.OrdinalIgnoreCase));
    }

    void Change(Action apply)
    {
        if (_loading) return;
        apply();
        Save();
    }

    void SetHotkey(Hotkey hk, Action<Hotkey> assign)
    {
        assign(hk);
        Save();
        PlatformServices.Hotkeys.Apply(S);
        // Статус обновляется в потоке хоткеев — перечитываем чуть позже.
        Avalonia.Threading.DispatcherTimer.RunOnce(RefreshHotkeyInfo, TimeSpan.FromMilliseconds(300));
    }

    void RefreshHotkeyInfo()
    {
        var hk = PlatformServices.Hotkeys;
        HotkeyStatus.Text = hk.IsSupported
            ? (string.IsNullOrWhiteSpace(hk.Status) ? L.T("Горячие клавиши не назначены.") : hk.Status)
            : hk.Status;

        var conflict = PlatformServices.PrintScreenConflict;
        bool usesPrtSc = new[] { S.CaptureRegionHotkey, S.CaptureFullHotkey, S.RecordVideoHotkey, S.StepGuideHotkey }
            .Any(h => h.Key is "PrintScreen" or "Snapshot");
        PrtScCard.IsVisible = conflict.IsSupported && usesPrtSc && conflict.IsSystemToolBoundToPrintScreen();
    }

    void UpdateQualityRow() =>
        QualityRow.IsVisible = S.ImageFormat is ImageFileFormat.Jpg or ImageFileFormat.WebP;

    void UpdatePreview()
    {
        var sample = new List<SeriesShot>
        {
            new()
            {
                Number = 1, ImagePath = "Screenshot_1.png",
                Markers = { new SeriesMarker { Label = "A", Comment = L.T("кнопка не нажимается") }, new SeriesMarker { Label = "B", Comment = L.T("опечатка в заголовке") } },
            },
            new()
            {
                Number = 2, ImagePath = "Screenshot_2.png",
                Markers = { new SeriesMarker { Label = "A", Comment = L.T("сдвинут отступ") } },
            },
        };
        PreviewText.Text = SeriesTextFormatter.Format(sample,
            LineTemplateBox.Text ?? S.SeriesLineTemplate,
            HeadersToggle.IsChecked == true,
            HeaderTemplateBox.Text ?? S.SeriesHeaderTemplate);
    }

    void UpdateFfmpegStatus()
    {
        var path = FfmpegRecorder.Resolve(S.FfmpegPath);
        FfmpegStatus.Text = path is null ? L.T("Не найден — запись видео недоступна") : L.T("Найден: ") + path;
        if (!FfmpegDownloader.IsRunning)
            DownloadFfmpegButton.Content = path is null ? L.T("Скачать") : L.T("Обновить");
    }

    /// <summary>Скачать и установить ffmpeg с прогрессом прямо в карточке настроек.</summary>
    async Task DownloadFfmpegAsync()
    {
        if (!FfmpegDownloader.IsSupported || FfmpegDownloader.IsRunning) return;
        DownloadFfmpegButton.IsEnabled = false;
        DownloadFfmpegButton.Content = L.T("Загрузка…");
        FfmpegProgress.IsVisible = FfmpegDownloadStatus.IsVisible = true;
        FfmpegProgress.Value = 0;
        FfmpegProgress.IsIndeterminate = true;

        var progress = new Progress<(double Fraction, string Status)>(p =>
        {
            FfmpegProgress.IsIndeterminate = p.Fraction <= 0;
            FfmpegProgress.Value = p.Fraction;
            FfmpegDownloadStatus.Text = p.Status;
        });

        try
        {
            var path = await FfmpegDownloader.DownloadAsync(progress);
            // Если раньше был указан неверный путь — переключаемся на установленный.
            if (!File.Exists(S.FfmpegPath))
            {
                S.FfmpegPath = "";
                FfmpegBox.Text = "";
                Save();
            }
            FfmpegDownloadStatus.Text = L.T("Готово! ffmpeg установлен: ") + path;
            UI.Toast.Show(L.T("ffmpeg установлен"), L.T("Запись видео готова к работе."));
            _ = RefreshVideoInfo();
        }
        catch (Exception ex)
        {
            FfmpegDownloadStatus.Text = L.T("Ошибка: ") + ex.Message;
        }
        finally
        {
            FfmpegProgress.IsVisible = false;
            DownloadFfmpegButton.IsEnabled = true;
            UpdateFfmpegStatus();
        }
    }

    /// <summary>Список устройств: Windows — из WASAPI (работает и без ffmpeg), иначе — то, что видит ffmpeg.</summary>
    async Task RefreshVideoInfo()
    {
        UpdateFfmpegStatus();

        List<(string Id, string Name)> mics;
        List<(string Id, string Name)> outputs = new();
        if (OperatingSystem.IsWindows())
        {
            mics = await Task.Run(() => Platform.Windows.WinAudioDevices.List(render: false));
            outputs = await Task.Run(() => Platform.Windows.WinAudioDevices.List(render: true));
        }
        else
        {
            var ffmpeg = FfmpegRecorder.Resolve(S.FfmpegPath);
            var names = ffmpeg is null ? new List<string>() : await FfmpegRecorder.ListMicrophonesAsync(ffmpeg);
            mics = names.Select(n => (n, n)).ToList();
        }

        _loading = true;
        FillDevices(MicCombo, mics, S.MicrophoneDevice, L.T("По умолчанию (системный микрофон)"));
        FillDevices(SystemDeviceCombo, outputs, S.SystemAudioDevice, L.T("По умолчанию (основные колонки/наушники)"));
        _loading = false;
    }

    // ── Тест микрофона ────────────────────────────────────────────────
    IDisposable? _micMeter;
    double _micPeak;

    void StartMicTest()
    {
        StopMicTest();
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var meter = new Platform.Windows.WinMicMeter(S.MicrophoneDevice);
            meter.Level += level => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                // Быстрый подъём, плавный спад; корень — чтобы тихая речь тоже была заметна.
                var v = Math.Sqrt(level);
                _micPeak = v > _micPeak ? v : _micPeak * 0.85 + v * 0.15;
                MicLevel.Value = _micPeak;
            });
            _micMeter = meter;
            MicTestButton.Content = L.T("Стоп");
            MicTestHint.Text = L.T("Слушаю… скажи что-нибудь");
        }
        catch (Exception ex)
        {
            MicTestHint.Text = L.T("Не удалось открыть микрофон: ") + ex.Message;
        }
    }

    void StopMicTest()
    {
        if (_micMeter is null) return;
        _micMeter.Dispose();
        _micMeter = null;
        _micPeak = 0;
        MicLevel.Value = 0;
        MicTestButton.Content = L.T("Проверить");
        MicTestHint.Text = L.T("Скажи что-нибудь — полоска должна двигаться");
    }

    internal static void FillDevices(ComboBox combo, List<(string Id, string Name)> devices, string selectedId, string defaultName)
    {
        combo.Items.Clear();
        combo.Items.Add(new ComboBoxItem { Content = defaultName, Tag = "" });
        foreach (var (id, name) in devices)
            combo.Items.Add(new ComboBoxItem { Content = name, Tag = id });
        combo.SelectedItem = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (i.Tag as string) == selectedId)
                             ?? combo.Items[0];
    }

    async Task<string?> PickFolder(string title, string current)
    {
        var options = new FolderPickerOpenOptions { Title = title, AllowMultiple = false };
        try
        {
            if (Directory.Exists(current))
                options.SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(current);
        }
        catch { }
        var result = await StorageProvider.OpenFolderPickerAsync(options);
        return result.FirstOrDefault()?.TryGetLocalPath();
    }

    bool _fillingCameras;

    /// <summary>Список камер — в фоне: ffmpeg перечисляет устройства секунду-две.</summary>
    async Task FillCamerasAsync()
    {
        _fillingCameras = true;
        CameraCombo.Items.Clear();
        CameraCombo.Items.Add(new ComboBoxItem { Content = L.T("Камера по умолчанию"), Tag = "" });
        CameraCombo.SelectedIndex = 0;
        var ffmpeg = Recording.VideoTools.Ffmpeg;
        if (ffmpeg is not null)
        {
            var cams = await Task.Run(() => Recording.CameraBubble.ListCameras(ffmpeg));
            foreach (var c in cams)
            {
                var item = new ComboBoxItem { Content = c, Tag = c };
                CameraCombo.Items.Add(item);
                if (c == S.VideoCameraDevice) CameraCombo.SelectedItem = item;
            }
        }
        _fillingCameras = false;
    }
}
