using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using SnapMark.Platform;
using SnapMark.Services;

namespace SnapMark.UI;

/// <summary>Уведомление в стиле macOS — компактная карточка в правом нижнем углу основного монитора,
/// над панелью задач / Dock (сверху она закрывала кнопки окон «свернуть» и «закрыть»).</summary>
public sealed class Toast : Window
{
    static Toast? _current;
    readonly DispatcherTimer _timer;

    public static void Show(string title, string message, string? actionText = null, Action? action = null)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!AppServices.Settings.ShowNotifications && action is null) return;
            _current?.Close();
            var toast = new Toast(title, message, actionText, action);
            _current = toast;
            toast.Show();
        });
    }

    Toast(string title, string message, string? actionText, Action? action)
    {
        UiKit.MakeFloating(this);
        ShowActivated = false;
        Opacity = 0;
        Title = "SnapMark";

        var icon = new Image
        {
            Source = App.Logo,
            Width = 24,
            Height = 24,
            VerticalAlignment = VerticalAlignment.Top,
        };

        var titleBlock = new TextBlock { Text = title }.Style("headline");
        var messageBlock = UiKit.Caption(message);
        messageBlock.MaxWidth = 210;

        var texts = new StackPanel { Spacing = 1, Margin = new Thickness(9, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(titleBlock);
        texts.Children.Add(messageBlock);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        grid.Children.Add(icon);
        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);

        if (actionText is not null && action is not null)
        {
            var btn = UiKit.SoftButton(actionText, () =>
            {
                action();
                Close();
            });
            btn.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(btn, 2);
            grid.Children.Add(btn);
        }

        var card = new GlassPanel { Padding = new Thickness(10, 8), Margin = new Thickness(12), Width = 300, Child = grid };
        card.Classes.Add("pill");
        card.PointerPressed += (_, _) => Close();
        Content = card;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(action is null ? 3.5 : 7) };
        _timer.Tick += (_, _) => Close();

        Opened += (_, _) =>
        {
            PlatformServices.WindowHelper.ExcludeFromCapture(this);
            PositionBottomRight();
            Opacity = 1;
            _timer.Start();
        };
    }

    void PositionBottomRight()
    {
        var screen = Screens.Primary ?? Screens.All.FirstOrDefault();
        if (screen is null) return;
        // WorkingArea уже без панели задач / Dock — карточка встаёт прямо над ней.
        var wa = screen.WorkingArea;
        int width = (int)Math.Ceiling(ClientSize.Width * screen.Scaling);
        int height = (int)Math.Ceiling(ClientSize.Height * screen.Scaling);
        Position = new PixelPoint(wa.Right - width, wa.Bottom - height);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _timer.Stop();
        if (ReferenceEquals(_current, this)) _current = null;
    }
}
