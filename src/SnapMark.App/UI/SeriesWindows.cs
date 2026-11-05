using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using SnapMark.Core;
using SnapMark.Platform;
using SnapMark.Services;

namespace SnapMark.UI;

/// <summary>
/// Плавающая панель серии внизу экрана: счётчик, «Копировать всё», «Открыть»,
/// перетаскивание всех файлов разом (самый надёжный способ закинуть картинки в веб-чат).
/// Не попадает в скриншоты.
/// </summary>
public sealed class SeriesHud : Window
{
    static SeriesHud? _instance;

    readonly TextBlock _count = new() { FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    List<IStorageItem> _dragItems = new();
    bool _settingPosition;
    DispatcherTimer? _saveTimer; // позицию пишем на диск один раз, когда панель остановилась

    public static void Sync()
    {
        var shots = AppServices.Series.Shots;
        if (shots.Count == 0)
        {
            _instance?.Close();
            _instance = null;
            return;
        }

        if (_instance is null)
        {
            _instance = new SeriesHud();
            _instance.Show();
        }
        _instance.Refresh();
    }

    SeriesHud()
    {
        UiKit.MakeFloating(this);
        ShowActivated = false;
        Title = L.T("SnapMark — серия");
        Opacity = 0;

        var dragHandle = new Border
        {
            Background = Brushes.Transparent,
            Padding = new Thickness(8, 4),
            CornerRadius = new CornerRadius(7),
            Cursor = new Cursor(StandardCursorType.DragMove),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children =
                {
                    new TextBlock { Text = "⠿", FontSize = 15, VerticalAlignment = VerticalAlignment.Center },
                    UiKit.Caption(L.T("Перетащить файлы")),
                },
            },
        };
        ToolTip.SetTip(dragHandle, L.T("Зажми и перетащи в чат или папку — все скриншоты серии разом"));
        dragHandle.PointerPressed += OnDragPressed;

        // Ручка для перемещения панели: тянуть за неё (или за свободное место панели).
        // Двойной щелчок — вернуть панель на место по умолчанию.
        var moveGrip = new Border
        {
            Background = Brushes.Transparent,
            Width = 18,
            Cursor = new Cursor(StandardCursorType.SizeAll),
            VerticalAlignment = VerticalAlignment.Stretch,
            Child = new TextBlock
            {
                Text = "⋮⋮", FontSize = 14, Opacity = 0.45, FontWeight = FontWeight.Bold,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };
        ToolTip.SetTip(moveGrip, L.T("Перетащи, чтобы переместить панель. Двойной щелчок — вернуть вниз по центру"));
        moveGrip.DoubleTapped += (_, _) => ResetPosition();

        var stack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        stack.Children.Add(moveGrip);
        stack.Children.Add(new Border
        {
            Width = 10, Height = 10, CornerRadius = new CornerRadius(5),
            Background = new SolidColorBrush(Color.Parse("#0A84FF")),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0),
        });
        stack.Children.Add(_count);
        stack.Children.Add(UiKit.VSep());
        stack.Children.Add(UiKit.SoftButton(L.T("Копировать всё"), () => _ = CopyAll(), primary: true));
        stack.Children.Add(UiKit.SoftButton(L.T("Открыть"), SeriesWindow.ShowSingle));
        stack.Children.Add(dragHandle);
        stack.Children.Add(UiKit.VSep());
        stack.Children.Add(UiKit.ToolButton("Icon.Trash", L.T("Очистить серию"), () => AppServices.Series.Clear(), "danger"));

        var card = new GlassPanel { Margin = new Thickness(16), Padding = new Thickness(6), Child = stack };
        card.Classes.Add("pill");
        card.Classes.Add("bar"); // капсула
        Content = card;

        // Перемещение: нажатие на ручку или на пустое место панели (кнопки нажатие забирают себе).
        card.PointerPressed += (_, e) =>
        {
            if (e.Handled || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || e.ClickCount > 1) return;
            BeginMoveDrag(e);
        };
        // Любое перемещение, кроме нашего собственного (Reposition), — это пользователь двигает панель.
        PositionChanged += (_, _) =>
        {
            if (_settingPosition || Opacity == 0) return;
            AppServices.Settings.SeriesHudPosition = $"{Position.X},{Position.Y}";
            _saveTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background,
                (_, _) => { _saveTimer!.Stop(); AppServices.SaveSettings(); });
            _saveTimer.Stop();
            _saveTimer.Start();
        };

        Opened += (_, _) =>
        {
            PlatformServices.WindowHelper.ExcludeFromCapture(this);
            Reposition();
            Opacity = 1;
        };
    }

    static async Task CopyAll()
    {
        try
        {
            await AppServices.Series.CopyAllAsync();
            Toast.Show(L.F("Серия скопирована · {0}", AppServices.Series.Shots.Count), SmartPaste.IsArmed
                ? L.T("Ctrl+V вставит и картинки, и текст — в чат, мессенджер или Word.")
                : L.T("Вставь в чат или документ."));
        }
        catch (Exception ex)
        {
            Toast.Show(L.T("Не удалось скопировать"), ex.Message);
        }
    }

    void Refresh()
    {
        int n = AppServices.Series.Shots.Count;
        _count.Text = L.F("Серия: {0}", n);
        _ = PrepareDragItems();
        Dispatcher.UIThread.Post(Reposition, DispatcherPriority.Background);
    }

    async Task PrepareDragItems()
    {
        var list = new List<IStorageItem>();
        foreach (var shot in AppServices.Series.Shots)
        {
            try
            {
                var f = await StorageProvider.TryGetFileFromPathAsync(shot.ImagePath);
                if (f is not null) list.Add(f);
            }
            catch { }
        }
        _dragItems = list;
    }

    async void OnDragPressed(object? sender, PointerPressedEventArgs e)
    {
        e.Handled = true; // это перетаскивание файлов, а не самой панели
        if (_dragItems.Count == 0) return;
        var data = new DataObject();
        data.Set(DataFormats.Files, _dragItems);
        // Комментарии тоже кладём в перетаскиваемые данные — некоторые приложения возьмут текст.
        data.Set(DataFormats.Text, Series.SeriesService.BuildText(AppServices.Series.Shots));
        try { await DragDrop.DoDragDrop(e, data, DragDropEffects.Copy); }
        catch { /* перетаскивание отменено или цель не приняла данные */ }
    }

    void ResetPosition()
    {
        AppServices.Settings.SeriesHudPosition = "";
        AppServices.SaveSettings();
        Reposition();
    }

    void Reposition()
    {
        // Пользователь перетащил панель — держим её там, пока это место есть на каком-то мониторе.
        var saved = AppServices.Settings.SeriesHudPosition.Split(',');
        if (saved.Length == 2 && int.TryParse(saved[0], out var sx) && int.TryParse(saved[1], out var sy))
        {
            var p = new PixelPoint(sx, sy);
            if (Screens.All.Any(sc => sc.WorkingArea.Contains(p)))
            {
                SetPosition(p);
                return;
            }
        }

        var screen = Screens.Primary ?? Screens.All.FirstOrDefault();
        if (screen is null) return;
        var wa = screen.WorkingArea;
        int w = (int)Math.Ceiling(ClientSize.Width * screen.Scaling);
        int h = (int)Math.Ceiling(ClientSize.Height * screen.Scaling);
        SetPosition(new PixelPoint(wa.X + (wa.Width - w) / 2, wa.Bottom - h - (int)(8 * screen.Scaling)));
    }

    void SetPosition(PixelPoint p)
    {
        _settingPosition = true;
        Position = p;
        _settingPosition = false;
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_saveTimer is { IsEnabled: true }) { _saveTimer.Stop(); AppServices.SaveSettings(); }
        base.OnClosed(e);
        if (ReferenceEquals(_instance, this)) _instance = null;
    }
}

/// <summary>Окно серии: все кадры, правка комментариев, удаление, копирование и сохранение.</summary>
public sealed class SeriesWindow : Window
{
    static SeriesWindow? _instance;
    readonly StackPanel _list = new() { Spacing = 12 };
    readonly TextBlock _subtitle = UiKit.Caption("");

    public static void ShowSingle()
    {
        if (_instance is null)
        {
            _instance = new SeriesWindow();
            _instance.Show();
        }
        else
        {
            _instance.Activate();
        }
    }

    SeriesWindow()
    {
        Title = L.T("Серия скриншотов — SnapMark");
        Width = 760;
        Height = 640;
        MinWidth = 520;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ExtendClientAreaToDecorationsHint = true;
        if (App.Logo is not null) Icon = new WindowIcon(App.Logo);
        Classes.Add("sm");

        var title = new TextBlock { Text = L.T("Серия скриншотов") };
        title.Classes.Add("pagetitle");

        var header = new StackPanel { Margin = new Thickness(24, 40, 24, 12), Spacing = 2 };
        header.Children.Add(title);
        header.Children.Add(_subtitle);

        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(24, 12),
        };
        footer.Children.Add(UiKit.SoftButton(L.T("Очистить"), () => AppServices.Series.Clear()));
        footer.Children.Add(UiKit.SoftButton(L.T("Сохранить в папку…"), () => _ = SaveAll()));
        footer.Children.Add(UiKit.SoftButton(L.T("Копировать всё"), () => _ = CopyAll(), primary: true));

        var scroll = new ScrollViewer
        {
            Content = _list,
            Padding = new Thickness(24, 0, 24, 8),
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        };

        var root = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(header);
        root.Children.Add(footer);
        root.Children.Add(scroll);
        Content = root;

        AppServices.Series.Changed += Rebuild;
        Rebuild();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        AppServices.Series.Changed -= Rebuild;
        if (ReferenceEquals(_instance, this)) _instance = null;
    }

    void Rebuild()
    {
        var shots = AppServices.Series.Shots;
        _subtitle.Text = shots.Count == 0
            ? L.T("Пусто. Сделай скриншот и нажми «Добавить в серию» (Ctrl+D).")
            : L.F("Скриншотов: {0}, меток: {1}. Комментарии копируются текстом, картинки — файлами.", shots.Count, shots.Sum(s => s.Markers.Count));

        _list.Children.Clear();
        foreach (var shot in shots) _list.Children.Add(BuildCard(shot));
    }

    Control BuildCard(SeriesShot shot)
    {
        Bitmap? thumb = null;
        try
        {
            using var fs = File.OpenRead(shot.ImagePath);
            thumb = Bitmap.DecodeToWidth(fs, 440);
        }
        catch { }

        var image = new Border
        {
            CornerRadius = new CornerRadius(8),
            ClipToBounds = true,
            BorderBrush = new SolidColorBrush(Color.FromArgb(40, 0, 0, 0)),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Top,
            Child = new Image { Source = thumb, MaxWidth = 220, MaxHeight = 160, Stretch = Stretch.Uniform },
        };
        image.PointerPressed += (_, _) => ShellOpen.Open(shot.ImagePath);
        ToolTip.SetTip(image, L.T("Открыть в просмотрщике"));

        var title = new TextBlock { Text = $"Screenshot_{shot.Number}", FontWeight = FontWeight.SemiBold, FontSize = 14 };
        var info = UiKit.Caption($"{shot.Width} × {shot.Height}  ·  {shot.CreatedAt:HH:mm:ss}");
        var head = new StackPanel { Spacing = 1 };
        head.Children.Add(title);
        head.Children.Add(info);

        var right = new StackPanel { Spacing = 8, Margin = new Thickness(16, 0, 0, 0) };
        right.Children.Add(head);

        if (shot.Markers.Count == 0)
            right.Children.Add(UiKit.Caption(L.T("Без меток")));

        foreach (var m in shot.Markers)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
            var badge = new Border
            {
                Background = new SolidColorBrush(Color.Parse("#0A84FF")),
                Child = new TextBlock { Text = m.Label },
            };
            badge.Classes.Add("badge");
            var box = new TextBox
            {
                Text = m.Comment, Watermark = L.F("Комментарий к {0}…", m.Label), Margin = new Thickness(8, 0, 0, 0),
                AcceptsReturn = true, MaxHeight = 160, // многострочные комментарии
            };
            box.Classes.Add("comment");
            var marker = m;
            box.PropertyChanged += (_, e) =>
            {
                if (e.Property == TextBox.TextProperty) marker.Comment = box.Text ?? "";
            };
            Grid.SetColumn(box, 1);
            row.Children.Add(badge);
            row.Children.Add(box);
            right.Children.Add(row);
        }

        var delete = UiKit.ToolButton("Icon.Trash", L.T("Удалить из серии"), () => AppServices.Series.Remove(shot), "danger");
        delete.VerticalAlignment = VerticalAlignment.Top;

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(14) };
        grid.Children.Add(image);
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);
        Grid.SetColumn(delete, 2);
        grid.Children.Add(delete);

        var card = new Border { Child = grid };
        card.Classes.Add("card");
        return card;
    }

    static async Task CopyAll()
    {
        try
        {
            await AppServices.Series.CopyAllAsync();
            Toast.Show(L.T("Серия скопирована"), SmartPaste.IsArmed
                ? L.T("Ctrl+V вставит и картинки, и текст — в чат, мессенджер или Word.")
                : L.T("Вставь в чат или документ."));
        }
        catch (Exception ex)
        {
            Toast.Show(L.T("Не удалось скопировать"), ex.Message);
        }
    }

    async Task SaveAll()
    {
        if (AppServices.Series.Shots.Count == 0) return;
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = L.T("Куда сохранить серию"),
            AllowMultiple = false,
        });
        var path = folders.FirstOrDefault()?.TryGetLocalPath();
        if (path is null) return;
        try
        {
            var target = AppServices.Series.SaveAllTo(path);
            Toast.Show(L.T("Серия сохранена"), Path.GetFileName(target), L.T("Открыть"), () => ShellOpen.OpenFolder(target));
        }
        catch (Exception ex)
        {
            Toast.Show(L.T("Не удалось сохранить"), ex.Message);
        }
    }
}
