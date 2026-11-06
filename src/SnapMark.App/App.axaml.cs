using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using SnapMark.Capture;
using SnapMark.Core;
using SnapMark.Platform;
using SnapMark.Recording;
using SnapMark.Services;
using SnapMark.Settings;
using SnapMark.UI;

namespace SnapMark;

public partial class App : Application
{
    static Window? _probe;

    /// <summary>Логотип для уведомлений и окон.</summary>
    public static Bitmap? Logo { get; private set; }
    /// <summary>Упрощённая иконка для показа в 16–32 точках (уведомления, шапки) — чётче уменьшенной большой.</summary>
    public static Bitmap? SmallLogo { get; private set; }

    /// <summary>Невидимое окно: через него получаем список мониторов и буфер обмена (на macOS/Linux).</summary>
    public static TopLevel? ClipboardHost => _probe;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            AppServices.Init();
            InstallCrashGuard();
            ApplyTheme();
            UI.Glass.Transparency = AppServices.Settings.GlassTransparency / 100.0;
            UI.Glass.Frost = AppServices.Settings.GlassFrost / 100.0;
            UI.Glass.Variant = Enum.TryParse<UI.GlassVariant>(AppServices.Settings.GlassVariant, out var gv) ? gv : UI.GlassVariant.Regular;
            UI.Glass.Appearance = Enum.TryParse<UI.GlassAppearance>(AppServices.Settings.GlassAppearance, out var ga) ? ga : UI.GlassAppearance.Auto;
            // Системные «Уменьшить прозрачность» / «Высокий контраст» — проверяем при запуске и раз в 5 секунд.
            UI.Glass.RefreshSystemAccessibility();
            UI.UiType.Refresh(Resources); // системный «Размер текста» → шкала шрифтов
            var a11yTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            a11yTimer.Tick += (_, _) => { UI.Glass.RefreshSystemAccessibility(); UI.UiType.Refresh(Resources); };
            a11yTimer.Start();
            LoadLogo();
            ExcludePopupsFromCapture();

            // Окно-«зонд» не показывается; нужно только для Screens/Clipboard.
            _probe = new Window { Width = 1, Height = 1, ShowInTaskbar = false, SystemDecorations = SystemDecorations.None };

            SetupTray(desktop);

            PlatformServices.Hotkeys.Triggered += OnHotkey;
            PlatformServices.Hotkeys.Apply(AppServices.Settings);
            AppServices.Series.Changed += SeriesHud.Sync;

            desktop.Exit += (_, _) =>
            {
                PlatformServices.Hotkeys.Dispose();
                AppServices.Series.Clear();
            };

            CommandLine.StartServer();
            DispatcherTimer.RunOnce(() => _ = UpdateChecker.CheckOnStartupAsync(), TimeSpan.FromSeconds(20));
            if (CommandLine.StartupCommand is { } startup)
                DispatcherTimer.RunOnce(() => CommandLine.Execute(startup), TimeSpan.FromMilliseconds(400));
            else
                DispatcherTimer.RunOnce(ShowWelcome, TimeSpan.FromMilliseconds(600));
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Подсказки и выпадающие списки — отдельные окна. Исключаем их из захвата, чтобы при записи видео
    /// (и на скриншотах) было видно только то, что пользователь рисует, а не меню программы.
    /// </summary>
    static void ExcludePopupsFromCapture()
    {
        Avalonia.Controls.Primitives.Popup.IsOpenProperty.Changed.AddClassHandler<Avalonia.Controls.Primitives.Popup>((popup, e) =>
        {
            if (e.NewValue is not true) return;
            if (popup.Host is TopLevel shown)
            {
                PlatformServices.WindowHelper.ExcludeFromCapture(shown);
                return;
            }
            // Окно подсказки ещё не создано — дождёмся открытия.
            void OnOpened(object? sender, EventArgs args)
            {
                popup.Opened -= OnOpened;
                if (popup.Host is TopLevel host) PlatformServices.WindowHelper.ExcludeFromCapture(host);
            }
            popup.Opened += OnOpened;
        });
    }

    /// <summary>
    /// Страховка: непредвиденная ошибка в обработчике не должна закрывать приложение из трея.
    /// Ошибка пишется в %AppData%\SnapMark\error.log и показывается уведомлением.
    /// </summary>
    static void InstallCrashGuard()
    {
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            e.Handled = true;
            LogError(e.Exception);
            Toast.Show(L.T("Что-то пошло не так"), e.Exception.Message);
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            LogError(e.Exception);
            e.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) LogError(ex);
        };
    }

    public static void LogError(Exception ex)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SnapMark");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, "error.log");
            if (File.Exists(file) && new FileInfo(file).Length > 512 * 1024) File.Delete(file);
            File.AppendAllText(file, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] v{typeof(App).Assembly.GetName().Version}\n{ex}\n\n");
        }
        catch { }
    }

    public static IReadOnlyList<Screen> GetScreens() => _probe?.Screens.All ?? Array.Empty<Screen>();

    /// <summary>Перезапуск (например, после смены языка): новый процесс дождётся, пока этот завершится.</summary>
    public static void Restart()
    {
        var exe = Environment.ProcessPath;
        if (exe is null) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, "--restart") { UseShellExecute = false });
        }
        catch
        {
            return;
        }
        if (Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.Shutdown();
    }

    public static void ApplyTheme()
    {
        if (Current is null) return;
        Current.RequestedThemeVariant = AppServices.Settings.Theme switch
        {
            ThemeMode.Light => ThemeVariant.Light,
            ThemeMode.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
    }

    static void LoadLogo()
    {
        try
        {
            using var s = AssetLoader.Open(new Uri("avares://SnapMark/Assets/snapmark.png"));
            Logo = new Bitmap(s);
            using var small = AssetLoader.Open(new Uri("avares://SnapMark/Assets/snapmark-32@2x.png"));
            SmallLogo = new Bitmap(small);
        }
        catch { }
    }

    static void OnHotkey(HotkeyAction action) => RunHotkeyAction(action);

    public static void RunHotkeyAction(HotkeyAction action)
    {
        switch (action)
        {
            case HotkeyAction.CaptureRegion:
                CaptureController.Start(CaptureMode.Region);
                break;
            case HotkeyAction.CaptureFull:
                CaptureController.Start(CaptureMode.FullScreen);
                break;
            case HotkeyAction.RecordVideo:
                if (RecordingController.IsRecording) _ = RecordingController.StopAsync();
                else CaptureController.Start(CaptureMode.VideoRegion);
                break;
            case HotkeyAction.StepGuide:
                Steps.StepRecorder.Toggle();
                break;
        }
    }

    void SetupTray(IClassicDesktopStyleApplicationLifetime desktop)
    {
        WindowIcon? icon = null;
        try
        {
            using var s = AssetLoader.Open(new Uri("avares://SnapMark/Assets/tray.png"));
            icon = new WindowIcon(new Bitmap(s));
        }
        catch { }

        var menu = new NativeMenu();
        menu.Items.Add(Item(() => L.F("Скриншот области\t{0}", AppServices.Settings.CaptureRegionHotkey),
            () => Delay(() => CaptureController.Start(CaptureMode.Region))));
        menu.Items.Add(Item(() => L.F("Весь экран\t{0}", AppServices.Settings.CaptureFullHotkey),
            () => Delay(() => CaptureController.Start(CaptureMode.FullScreen))));
        menu.Items.Add(Item(() => L.T("Записать видео…"),
            () =>
            {
                if (RecordingController.IsRecording) _ = RecordingController.StopAsync();
                else Delay(() => CaptureController.Start(CaptureMode.VideoRegion));
            }));
        menu.Items.Add(Item(() => L.F("Пошаговая инструкция\t{0}", AppServices.Settings.StepGuideHotkey),
            () => Delay(Steps.StepRecorder.Toggle)));
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(Item(() => L.T("Серия скриншотов…"), SeriesWindow.ShowSingle));
        menu.Items.Add(Item(() => L.T("Открыть папку скриншотов"), () => ShellOpen.OpenFolder(AppServices.Settings.SaveFolder)));
        menu.Items.Add(Item(() => L.T("Настройки…"), () => SettingsWindow.ShowSingle()));
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(Item(() => L.T("Выход"), () => desktop.Shutdown()));

        var tray = new TrayIcon
        {
            Icon = icon,
            ToolTipText = L.T("SnapMark — скриншоты с метками"),
            Menu = menu,
            IsVisible = true,
        };
        // Клик по иконке в трее — сразу скриншот области.
        tray.Clicked += (_, _) => Delay(() => CaptureController.Start(CaptureMode.Region));

        TrayIcon.SetIcons(this, new TrayIcons { tray });
    }

    static NativeMenuItem Item(Func<string> header, Action onClick)
    {
        var item = new NativeMenuItem(header());
        item.Click += (_, _) => onClick();
        return item;
    }

    /// <summary>Меню трея должно успеть исчезнуть, иначе попадёт в скриншот.</summary>
    static void Delay(Action action) => DispatcherTimer.RunOnce(action, TimeSpan.FromMilliseconds(250));

    static void ShowWelcome()
    {
        var s = AppServices.Settings;
        var conflict = PlatformServices.PrintScreenConflict;

        if (!s.FirstRunDone)
        {
            s.FirstRunDone = true;
            AppServices.SaveSettings();
            Toast.Show(L.T("SnapMark запущен"),
                L.F("Живу в трее. {0} — скриншот, клик по иконке в трее — тоже.", s.CaptureRegionHotkey),
                L.T("Настройки"), () => SettingsWindow.ShowSingle());
        }

        // macOS: без разрешения «Запись экрана» скриншоты будут пустыми (только обои).
        if (OperatingSystem.IsMacOS() && !Platform.Mac.MacScreenPermission.IsGranted())
        {
            Platform.Mac.MacScreenPermission.Request();
            DispatcherTimer.RunOnce(() => Toast.Show(L.T("Нужно разрешение «Запись экрана»"),
                L.T("Системные настройки → Конфиденциальность и безопасность → Запись экрана: включи SnapMark и перезапусти его."),
                L.T("Открыть настройки"), Platform.Mac.MacScreenPermission.OpenSettings), TimeSpan.FromSeconds(2));
        }

        // Linux/Wayland: программам нельзя перехватывать клавиши — подсказываем про системное сочетание.
        if (OperatingSystem.IsLinux() && !PlatformServices.Hotkeys.IsSupported)
        {
            DispatcherTimer.RunOnce(() => Toast.Show(L.T("Горячие клавиши"), PlatformServices.Hotkeys.Status,
                L.T("Настройки"), () => SettingsWindow.ShowSingle(1)), TimeSpan.FromSeconds(3));
        }

        bool usesPrtSc = s.CaptureRegionHotkey.Key is "PrintScreen" or "Snapshot";
        if (usesPrtSc && conflict.IsSupported && conflict.IsSystemToolBoundToPrintScreen())
        {
            DispatcherTimer.RunOnce(() => Toast.Show(L.T("PrtSc и «Ножницы»"),
                L.T("Windows тоже реагирует на PrtSc. SnapMark перехватывает клавишу, но если откроются «Ножницы» — отключи их в настройках."),
                L.T("Исправить"), () => SettingsWindow.ShowSingle(1)), TimeSpan.FromSeconds(4));
        }
    }
}
