using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using SnapMark.Core;
using SnapMark.Localization;
using SnapMark.Services;

namespace SnapMark.UI;

/// <summary>
/// Библиотека эмодзи: поиск (по-русски и по-английски), категории, недавние. Выбор — событие <see cref="Picked"/>.
/// </summary>
public sealed class EmojiPicker : UserControl
{
    /// <summary>Цветные эмодзи-шрифты систем: Windows, macOS, Linux.</summary>
    public static readonly FontFamily EmojiFont = new("Segoe UI Emoji, Apple Color Emoji, Noto Color Emoji, EmojiOne Color, Twemoji Mozilla");

    const int MaxRecent = 24;
    const double Cell = 38;

    public event Action<string>? Picked;

    readonly TextBox _search;
    readonly WrapPanel _grid = new() { Orientation = Orientation.Horizontal };
    readonly ScrollViewer _scroll;
    readonly StackPanel _tabs = new() { Orientation = Orientation.Horizontal, Spacing = 2 };
    readonly TextBlock _empty;
    int _category; // -1 — недавние

    public EmojiPicker()
    {
        _search = new TextBox { Watermark = L.T("Поиск эмодзи"), MinWidth = 200 };
        _search.Classes.Add("search");
        _search.TextChanged += (_, _) => Refill();
        _search.KeyDown += (_, e) =>
        {
            // Enter — вставить первый найденный.
            if (e.Key == Key.Enter && _grid.Children.OfType<Button>().FirstOrDefault() is { Tag: string first })
            {
                Pick(first);
                e.Handled = true;
            }
        };

        _scroll = new ScrollViewer
        {
            Height = Cell * 6 + 4,
            Width = Cell * 9 + 4,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _grid,
        };
        _empty = new TextBlock
        {
            Text = L.T("Ничего не нашлось"),
            Margin = new Thickness(8, 12),
            Opacity = 0.6,
            IsVisible = false,
        };

        Content = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                _search,
                _tabs,
                new Panel { Children = { _scroll, _empty } },
            },
        };

        BuildTabs();
        _category = AppServices.Settings.RecentEmoji.Count > 0 ? -1 : 0;
        Refill();
    }

    /// <summary>Сбросить поиск и показать недавние (если есть) при каждом открытии.</summary>
    public void Reset()
    {
        _search.Text = "";
        _category = AppServices.Settings.RecentEmoji.Count > 0 ? -1 : 0;
        Refill();
        _scroll.Offset = default;
    }

    void BuildTabs()
    {
        _tabs.Children.Clear();
        _tabs.Children.Add(Tab("🕘", L.T("Недавние"), -1));
        for (int i = 0; i < EmojiCatalog.Categories.Length; i++)
            _tabs.Children.Add(Tab(EmojiCatalog.Categories[i].Icon, L.T(EmojiCatalog.Categories[i].Name), i));
    }

    Button Tab(string icon, string tip, int index)
    {
        var b = new Button
        {
            Content = new TextBlock { Text = icon, FontFamily = EmojiFont, FontSize = 17, HorizontalAlignment = HorizontalAlignment.Center },
            Tag = index,
            Width = 34, Height = 30,
            Padding = new Thickness(0),
        };
        b.Classes.Add("chip");
        ToolTip.SetTip(b, tip);
        b.Click += (_, _) =>
        {
            _category = index;
            _search.Text = "";
            Refill();
            _scroll.Offset = default;
        };
        return b;
    }

    void Refill()
    {
        var query = (_search.Text ?? "").Trim();
        IEnumerable<string> items = query.Length > 0
            ? EmojiCatalog.Search(query).Select(e => e.Text)
            : _category < 0
                ? AppServices.Settings.RecentEmoji
                : EmojiCatalog.Categories[Math.Clamp(_category, 0, EmojiCatalog.Categories.Length - 1)].Items.Select(e => e.Text);

        foreach (var t in _tabs.Children.OfType<Button>())
            t.Classes.Set("selected", query.Length == 0 && t.Tag is int i && i == _category);

        _grid.Children.Clear();
        foreach (var text in items.Take(400))
            _grid.Children.Add(Item(text));
        _empty.IsVisible = _grid.Children.Count == 0;
        if (_empty.IsVisible && query.Length == 0 && _category < 0) _empty.Text = L.T("Здесь появятся недавние эмодзи");
        else _empty.Text = L.T("Ничего не нашлось");
    }

    Button Item(string text)
    {
        var glyph = new TextBlock
        {
            Text = text, FontFamily = EmojiFont, FontSize = 24,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        var picture = new Image { Width = 26, Height = 26, IsVisible = false };
        var content = new Panel { Width = 30, Height = 30, Children = { glyph, picture } };

        var b = new Button
        {
            Content = content,
            Tag = text,
            Width = Cell, Height = Cell,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        b.Classes.Add("emoji");
        b.Click += (_, _) => Pick(text);

        if (EmojiImages.Enabled) ShowPicture(text, glyph, picture);
        return b;
    }

    /// <summary>Эмодзи картинкой (набор Apple): из кэша сразу, иначе подгружаем и подменяем символ.</summary>
    static void ShowPicture(string text, TextBlock glyph, Image picture)
    {
        if (EmojiImages.Cached(text) is { } cached)
        {
            picture.Source = cached;
            picture.IsVisible = true;
            glyph.IsVisible = false;
            return;
        }

        _ = Task.Run(async () =>
        {
            var bitmap = await EmojiImages.Get(text);
            if (bitmap is null) return;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                picture.Source = bitmap;
                picture.IsVisible = true;
                glyph.IsVisible = false;
            });
        });
    }

    void Pick(string text)
    {
        var recent = AppServices.Settings.RecentEmoji;
        recent.Remove(text);
        recent.Insert(0, text);
        if (recent.Count > MaxRecent) recent.RemoveRange(MaxRecent, recent.Count - MaxRecent);
        AppServices.SaveSettings();
        Picked?.Invoke(text);
    }
}
