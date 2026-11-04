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
        AutostartToggle.IsChecked = S.LaunchAtStartup;
        AutostartToggle.IsEnabled = PlatformServices.Autostart.IsSupported;
        NotifyToggle.IsChecked = S.ShowNotifications;
        UpdatesToggle.IsChecked = S.CheckForUpdates;

        HkRegion.Value = S.CaptureRegionHotkey.Clone();
        HkFull.Value = S.CaptureFullHotkey.Clone();
        HkVideo.Value = S.RecordVideoHotkey.Clone();

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
        bool usesPrtSc = new[] { S.CaptureRegionHotkey, S.CaptureFullHotkey, S.RecordVideoHotkey }
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
}
