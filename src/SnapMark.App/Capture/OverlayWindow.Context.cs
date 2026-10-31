using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using SnapMark.Annotations;
using SnapMark.Core;
using SnapMark.UI;

namespace SnapMark.Capture;

/// <summary>
/// Контекстная панель: появляется рядом с выделенным объектом (или текстом, который сейчас печатается)
/// и меняет настройки ТОЛЬКО этого объекта — как всплывающие меню в Markup на iOS/macOS.
///   • Текст: цвет, шрифт, начертание, курсив, размер, без рамки / подложка / рамка.
///   • Метка: цвет фона, цвет буквы, форма, размер; если включено «На картинке» — стиль её подписи.
///   • Прочие объекты: цвет, толщина, заливка.
/// Везде есть «Дублировать» и «Удалить».
/// </summary>
public partial class OverlayWindow
{
    static readonly string[] ContextPalette =
    {
        "#FF3B30", "#FF9500", "#FFCC00", "#34C759", "#00C7BE", "#0A84FF",
        "#5E5CE6", "#AF52DE", "#FF2D55", "#8E8E93", "#FFFFFF", "#000000",
    };

    static readonly string[] LabelPalette = { "#FFFFFF", "#000000", "#FFCC00", "#FF3B30", "#0A84FF", "#34C759" };

    static readonly (int Weight, string Name)[] Weights =
    {
        (300, L.T("Тонкий")), (400, L.T("Обычный")), (600, L.T("Полужирный")), (700, L.T("Жирный")), (900, L.T("Чёрный")),
    };

    Annotation? _contextTarget;
    bool _contextRebuildPending;

    /// <summary>Объект, который настраивает панель: печатаемый текст или выделенный объект.</summary>
    Annotation? ContextTarget => (Annotation?)_editing ?? Surface.SelectedAnnotation;

    void RequestContextRebuild()
    {
        if (_contextRebuildPending) return;
        _contextRebuildPending = true;
        // Откладываем: пересборка внутри обработчика контрола панели удалила бы сам этот контрол.
        Dispatcher.UIThread.Post(() =>
        {
            _contextRebuildPending = false;
            BuildContext();
            LayoutObjectBar();
            // И ещё раз после того, как выпадающие списки получат шаблоны и реальную высоту.
            Dispatcher.UIThread.Post(LayoutObjectBar, DispatcherPriority.Loaded);
        });
    }

    void LayoutObjectBar()
    {
        var a = ContextTarget;
        bool show = a is not null && !Surface.IsInteracting && _mode != CaptureMode.VideoRegion
                    && Surface.Selection is not null && !Surface.IsAdjustingSelection;
        ObjectBar.IsVisible = show;
        if (!show || a is null) return;

        double W = Layer.Bounds.Width, H = Layer.Bounds.Height;
        var r = Surface.ToView(a.Bounds);
        // В первый момент выпадающие списки ещё без шаблона и панель «кажется» ниже, чем есть.
        // Берём реальный размер после раскладки, а при его изменении панель переставится сама (см. OnObjectBarBounds).
        var measured = MeasureOf(ObjectBar);
        var actual = ObjectBar.Bounds.Size;
        var size = new Size(Math.Max(measured.Width, actual.Width), Math.Max(measured.Height, actual.Height));

        // Над объектом, иначе под ним, иначе сбоку — лишь бы не закрывать сам объект.
        double x = Clamp(r.Center.X - size.Width / 2, 8, W - size.Width - 8);
        double y = r.Top - size.Height - 12;
        if (y < 8) y = r.Bottom + 12;
        if (y + size.Height > H - 8)
        {
            y = Clamp(r.Center.Y - size.Height / 2, 8, H - size.Height - 8);
            x = r.Right + 12 + size.Width < W ? r.Right + 12 : Math.Max(8, r.Left - size.Width - 12);
        }
        x = Math.Round(x);
        y = Math.Round(y);
        double cl = Canvas.GetLeft(ObjectBar), ct = Canvas.GetTop(ObjectBar);
        if (double.IsNaN(cl) || double.IsNaN(ct) || Math.Abs(cl - x) > 0.5 || Math.Abs(ct - y) > 0.5)
            Place(ObjectBar, x, y);
    }

    Size _lastObjectBarSize;

    /// <summary>Панель изменила размер (дорисовались списки, поменялось содержимое) — переставляем.</summary>
    void OnObjectBarBounds()
    {
        var size = ObjectBar.Bounds.Size;
        if (Math.Abs(size.Width - _lastObjectBarSize.Width) < 0.5 && Math.Abs(size.Height - _lastObjectBarSize.Height) < 0.5) return;
        _lastObjectBarSize = size;
        LayoutObjectBar();
    }

    // ═══════════════════════════ Сборка панели ═══════════════════════════
    void BuildContext()
    {
        var target = ContextTarget;
        _contextTarget = target;
        ContextContent.Children.Clear();
        if (target is null) return;

        switch (target)
        {
            case TextAnnotation t:
                BuildTextContext(t);
                break;
            case MarkerAnnotation m:
                BuildMarkerContext(m);
                break;
            default:
                BuildShapeContext(target);
                break;
        }
    }

    void BuildTextContext(TextAnnotation t)
    {
        ContextContent.Children.Add(Row(Label(L.T("Цвет")), ColorRow(ContextPalette, t.Color, c => Mutate(a => a.Color = Annotation.WithOpacity(c, a.Color.A / 255.0)))));

        var font = FontPicker(t.FontFamily, name => Mutate(a => ((TextAnnotation)a).FontFamily = name));
        var weight = WeightPicker(t.FontWeight, w => Mutate(a => ((TextAnnotation)a).FontWeight = w));
        var italic = Chip("I", t.Italic, v => Mutate(a => ((TextAnnotation)a).Italic = v), italic: true, tip: L.T("Курсив"));
        var size = Stepper(t.FontSize, 8, 160, 2, v => Mutate(a => ((TextAnnotation)a).FontSize = v), L.T("Размер текста"));
        ContextContent.Children.Add(Row(font, weight, italic, size));

        ContextContent.Children.Add(Row(
            FrameChips(t.Frame, f => Mutate(a => ((TextAnnotation)a).Frame = f)),
            Spacer(),
            ObjectActions()));
        foreach (var row in FrameColorRows(t.Frame, t.BorderColor, t.PlateColor,
                     c => Mutate(a => ((TextAnnotation)a).BorderColor = c),
                     c => Mutate(a => ((TextAnnotation)a).PlateColor = c),
                     L.T("Цвет текста")))
            ContextContent.Children.Add(row);

        ContextContent.Children.Add(ShadowRow(t.Shadow, t.ShadowColor,
            on => Mutate(a => ((TextAnnotation)a).Shadow = on),
            c => Mutate(a =>
            {
                var ta = (TextAnnotation)a;
                ta.Shadow = true;
                ta.ShadowColor = c;
            })));
    }

    /// <summary>Строка «Тень»: вкл/выкл, «Авто» и цвета.</summary>
    static StackPanel ShadowRow(bool enabled, Color? color, Action<bool> toggle, Action<Color?> pick)
    {
        var on = Chip(L.T("Тень"), enabled, toggle, tip: L.T("Тень под текстом (или под плашкой)"));
        var auto = Chip(L.T("Авто"), color is null, _ => pick(null), tip: L.T("Тёмная под светлым текстом, светлая под тёмным"));
        auto.IsEnabled = enabled;
        var swatches = ColorRow(ShadowPalette, color, c => pick(c));
        swatches.IsEnabled = enabled;
        return Row(Label(L.T("Тень")), on, auto, swatches);
    }

    static readonly string[] ShadowPalette = { "#000000", "#FFFFFF", "#8E8E93", "#0A84FF", "#FF3B30", "#FFCC00" };

    void BuildMarkerContext(MarkerAnnotation m)
    {
        ContextContent.Children.Add(Row(Label(L.T("Фон")), ColorRow(ContextPalette, m.Color, c => Mutate(a => a.Color = c))));

        var labelSwatches = ColorRow(LabelPalette, m.LabelColor, c => Mutate(a => ((MarkerAnnotation)a).LabelColor = c));
        var auto = Chip(L.T("Авто"), m.LabelColor is null, _ => Mutate(a => ((MarkerAnnotation)a).LabelColor = null),
            tip: L.T("Белый на тёмном, тёмный на светлом"));
        ContextContent.Children.Add(Row(Label(m.Label.All(char.IsDigit) ? L.T("Цифра") : L.T("Буква")), auto, labelSwatches));

        var shapes = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        foreach (var (shape, icon, tip) in new[]
                 {
                     (MarkerShape.Circle, "Icon.ShapeCircle", L.T("Круг")),
                     (MarkerShape.RoundedSquare, "Icon.ShapeRounded", L.T("Скруглённый квадрат")),
                     (MarkerShape.Diamond, "Icon.ShapeDiamond", L.T("Ромб")),
                     (MarkerShape.Pin, "Icon.ShapePin", L.T("Булавка")),
                 })
        {
            var s = shape;
            var b = UiKit.ToolButton(icon, tip, () => Mutate(a => ((MarkerAnnotation)a).Shape = s), "small");
            b.Classes.Set("selected", m.Shape == shape);
            shapes.Children.Add(b);
        }
        var size = Stepper(m.FontSize, 12, 120, 2, v => Mutate(a => ((MarkerAnnotation)a).FontSize = v), L.T("Размер метки"));
        ContextContent.Children.Add(Row(shapes, size, Spacer(), ObjectActions()));

        if (!Surface.CommentStyle.Enabled) return;

        // Стиль подписи-комментария именно этой метки.
        ContextContent.Children.Add(Separator());
        var font = FontPicker(m.CommentFont, name => Mutate(a => ((MarkerAnnotation)a).CommentFont = name));
        var weight = WeightPicker(m.CommentWeight, w => Mutate(a => ((MarkerAnnotation)a).CommentWeight = w));
        var italic = Chip("I", m.CommentItalic, v => Mutate(a => ((MarkerAnnotation)a).CommentItalic = v), italic: true, tip: L.T("Курсив"));
        var csize = Stepper(m.CommentSize, 8, 96, 2, v => Mutate(a => ((MarkerAnnotation)a).CommentSize = v), L.T("Размер подписи"));
        ContextContent.Children.Add(Row(Label(L.T("Подпись")), font, weight, italic, csize));
        ContextContent.Children.Add(Row(Label(L.T("Цвет")),
            ColorRow(ContextPalette, m.CommentColor, c => Mutate(a => ((MarkerAnnotation)a).CommentColor = c))));
        ContextContent.Children.Add(Row(Label(L.T("Вид")),
            FrameChips(m.CommentFrame, f => Mutate(a => ((MarkerAnnotation)a).CommentFrame = f))));
        foreach (var row in FrameColorRows(m.CommentFrame, m.CommentBorderColor, m.CommentPlateColor,
                     c => Mutate(a => ((MarkerAnnotation)a).CommentBorderColor = c),
                     c => Mutate(a => ((MarkerAnnotation)a).CommentPlateColor = c),
                     L.T("Цвет метки")))
            ContextContent.Children.Add(row);
        ContextContent.Children.Add(ShadowRow(m.CommentShadow, m.CommentShadowColor,
            on => Mutate(a => ((MarkerAnnotation)a).CommentShadow = on),
            c => Mutate(a =>
            {
                var mk = (MarkerAnnotation)a;
                mk.CommentShadow = true;
                mk.CommentShadowColor = c;
            })));
    }

    void BuildShapeContext(Annotation target)
    {
        ContextContent.Children.Add(Row(ColorRow(ContextPalette, target.Color,
            c => Mutate(a => a.Color = Annotation.WithOpacity(c, a.Color.A / 255.0)))));

        var controls = new List<Control>
        {
            Stepper(target.Thickness, 1, 40, 1, v => Mutate(a => a.Thickness = v), L.T("Толщина")),
            Chip(L.T("Пунктир"), target.Dashed, v => Mutate(a => a.Dashed = v)),
        };
        if (target is BoxAnnotation box)
            controls.Add(Chip(L.T("Заливка"), box.Filled, v => Mutate(a => ((BoxAnnotation)a).Filled = v)));
        controls.Add(Spacer());
        controls.Add(ObjectActions());
        ContextContent.Children.Add(Row(controls.ToArray()));
    }

    // ═══════════════════════════ Изменение объекта ═══════════════════════════
    /// <summary>Изменить объект панели (с отменой через Ctrl+Z) и запомнить стиль как «последний».</summary>
    void Mutate(Action<Annotation> change)
    {
        var target = ContextTarget;
        if (target is null) return;

        var doc = Surface.Document;
        List<Annotation>? snap = doc.Items.Contains(target) ? doc.Snapshot() : null;
        change(target);
        if (snap is not null) doc.PushUndo(snap);

        if (ReferenceEquals(target, _editing) && _editor is not null) StyleEditor(_editor, _editing);

        // Локальные настройки меняют только этот объект; значения по умолчанию остаются в общей панели.
        Surface.InvalidateVisual();
        UpdateHistoryButtons();
        RebuildComments(force: true);
        RequestContextRebuild();
    }

    // ═══════════════════════════ Маленькие контролы ═══════════════════════════
    internal static StackPanel Row(params Control[] children)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        foreach (var c in children)
        {
            c.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(c);
        }
        return row;
    }

    static Control Spacer() => new Border { Width = 6 };

    static Control Separator()
    {
        var b = new Border();
        b.Classes.Add("hsep");
        b.Classes.Add("tight");
        return b;
    }

    static TextBlock Label(string text)
    {
        var t = UiKit.Caption(text);
        t.MinWidth = 44;
        t.TextWrapping = TextWrapping.NoWrap;
        return t;
    }

    internal static StackPanel ColorRow(string[] palette, Color? current, Action<Color> pick)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 0 };
        var currentHex = current is Color c ? $"#{c.R:X2}{c.G:X2}{c.B:X2}" : null;
        foreach (var hex in palette)
        {
            var color = Color.Parse(hex);
            var b = new Button { Background = new SolidColorBrush(color) };
            b.Classes.Add("swatch");
            b.Classes.Add("mini");
            b.Classes.Set("current", string.Equals(hex, currentHex, StringComparison.OrdinalIgnoreCase));
            b.Click += (_, _) => pick(color);
            panel.Children.Add(b);
        }
        return panel;
    }

    static ToggleButton Chip(string text, bool isChecked, Action<bool> changed, bool italic = false, string? tip = null)
    {
        var chip = new ToggleButton { Content = text, IsChecked = isChecked, Margin = new Thickness(0) };
        chip.Classes.Add("chip");
        if (italic)
        {
            chip.FontStyle = FontStyle.Italic;
            chip.FontWeight = FontWeight.Bold;
        }
        if (tip is not null) ToolTip.SetTip(chip, tip);
        chip.Click += (_, _) => changed(chip.IsChecked == true);
        return chip;
    }

    static ComboBox FontPicker(string current, Action<string> pick)
    {
        var combo = new ComboBox { Width = 150, MaxDropDownHeight = 320 };
        foreach (var name in FontCatalog.Names)
            combo.Items.Add(new ComboBoxItem { Content = name, Tag = name, FontFamily = FontCatalog.Get(name) });
        combo.SelectedItem = combo.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(i => string.Equals(i.Tag as string, current, StringComparison.OrdinalIgnoreCase));
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is ComboBoxItem { Tag: string name } && !string.Equals(name, current, StringComparison.OrdinalIgnoreCase))
                pick(name);
        };
        ToolTip.SetTip(combo, L.T("Шрифт"));
        return combo;
    }

    static ComboBox WeightPicker(int current, Action<int> pick)
    {
        var combo = new ComboBox { Width = 122 };
        foreach (var (w, name) in Weights)
            combo.Items.Add(new ComboBoxItem { Content = name, Tag = w, FontWeight = (FontWeight)w });
        var nearest = Weights.OrderBy(x => Math.Abs(x.Weight - current)).First().Weight;
        combo.SelectedItem = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag is int w && w == nearest);
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is ComboBoxItem { Tag: int w } && w != current) pick(w);
        };
        ToolTip.SetTip(combo, L.T("Начертание"));
        return combo;
    }

    static Control Stepper(double value, double min, double max, double step, Action<double> set, string tip)
    {
        var minus = new Button { Content = "−", Width = 26, Height = 26, Padding = new Thickness(0) };
        var plus = new Button { Content = "+", Width = 26, Height = 26, Padding = new Thickness(0) };
        minus.Classes.Add("tool");
        minus.Classes.Add("small");
        plus.Classes.Add("tool");
        plus.Classes.Add("small");
        var text = new TextBlock
        {
            Text = ((int)Math.Round(value)).ToString(),
            MinWidth = 26,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = FontWeight.SemiBold,
        };
        minus.Click += (_, _) => set(Math.Max(min, Math.Round(value - step)));
        plus.Click += (_, _) => set(Math.Min(max, Math.Round(value + step)));

        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 0 };
        panel.Children.Add(minus);
        panel.Children.Add(text);
        panel.Children.Add(plus);
        ToolTip.SetTip(panel, tip);
        return panel;
    }

    static WrapPanel FrameChips(TextFrame current, Action<TextFrame> pick)
    {
        var panel = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var (frame, text) in new[]
                 {
                     (TextFrame.None, L.T("Без всего")), (TextFrame.Outline, L.T("Рамка")),
                     (TextFrame.Fill, L.T("Подложка")), (TextFrame.FillOutline, L.T("Рамка + подложка")),
                 })
        {
            var f = frame;
            var chip = Chip(text, current == frame, _ => pick(f));
            chip.Margin = new Thickness(0, 0, 4, 0);
            panel.Children.Add(chip);
        }
        return panel;
    }

    /// <summary>Подложка — светлые и тёмные «бумажные» оттенки плюс основные цвета.</summary>
    internal static readonly string[] PlatePalette =
        { "#FFFFFF", "#F2F2F7", "#FFF3B0", "#D6E9FF", "#DFF5E1", "#FFD6E0", "#1C1C1E", "#3A3A3C", "#0A84FF", "#FF3B30", "#FFCC00", "#34C759" };

    /// <summary>
    /// Строки «Цвет рамки» и «Цвет подложки» — показываются только для выбранного режима.
    /// null в выборе означает «Авто» (рамка — цвет текста/метки, подложка — белая или тёмная по контрасту).
    /// </summary>
    internal static IEnumerable<Control> FrameColorRows(TextFrame frame, Color? border, Color? plate,
        Action<Color?> pickBorder, Action<Color?> pickPlate, string borderAutoTip)
    {
        if (frame is TextFrame.Outline or TextFrame.FillOutline)
            yield return AutoColorRow(L.T("Рамка"), border, ContextPalette, pickBorder, borderAutoTip);
        if (frame is TextFrame.Fill or TextFrame.FillOutline)
            yield return AutoColorRow(L.T("Подложка"), plate, PlatePalette, pickPlate, L.T("Белая под тёмным текстом, тёмная под светлым"));
    }

    static StackPanel AutoColorRow(string label, Color? current, string[] palette, Action<Color?> pick, string autoTip)
    {
        var auto = Chip(L.T("Авто"), current is null, _ => pick(null), tip: autoTip);
        return Row(Label(label), auto, ColorRow(palette, current, c => pick(c)));
    }

    Control ObjectActions()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        panel.Children.Add(UiKit.ToolButton("Icon.Duplicate", L.T("Дублировать (Ctrl+J)"), () =>
        {
            CommitTextEdit();
            Surface.DuplicateSelected();
        }, "small"));
        panel.Children.Add(UiKit.ToolButton("Icon.Trash", L.T("Удалить (Delete или правый клик по объекту)"), () =>
        {
            CommitTextEdit();
            Surface.DeleteSelected();
        }, "small", "danger"));
        return panel;
    }
}
