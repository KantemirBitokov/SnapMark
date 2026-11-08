using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using SnapMark.Annotations;
using SnapMark.Core;
using SnapMark.Export;
using SnapMark.Platform;
using SnapMark.Platform.Common;
using SnapMark.Recording;
using SnapMark.Series;
using SnapMark.Services;
using SnapMark.UI;

namespace SnapMark.Capture;

/// <summary>
/// Полноэкранное окно поверх одного монитора: выделение, рисование, действия.
/// Для каждого монитора создаётся своё окно (так проще и точнее при разном масштабе экранов).
/// </summary>
public partial class OverlayWindow : Window
{
    // Палитра в духе Markup: системные цвета macOS + белый и чёрный.
    static readonly string[] Palette =
    {
        "#FF3B30", "#FF9500", "#FFCC00", "#34C759", "#00C7BE", "#0A84FF",
        "#5E5CE6", "#AF52DE", "#FF2D55", "#A2845E", "#FFFFFF", "#000000",
    };

    readonly Screen? _screen;
    readonly Bitmap? _shot;
    readonly CaptureMode _mode;

    TextBox? _editor;
    TextAnnotation? _editing;
    bool _editingIsNew;
    string _commentsSignature = "";
    bool _withMic;
    bool _withSystem;
    bool _syncingStyle;
    ToolKind _currentShape = ToolKind.Arrow;

    sealed record RenderResult(byte[] Png, int Width, int Height, List<SeriesMarker> Markers);

    public OverlayWindow()
    {
        InitializeComponent();
    }

    public OverlayWindow(Screen screen, Bitmap shot, CaptureMode mode) : this()
    {
        _screen = screen;
        _shot = shot;
        _mode = mode;

        Position = screen.Bounds.Position;
        Width = screen.Bounds.Width / screen.Scaling;
        Height = screen.Bounds.Height / screen.Scaling;
        // На macOS «полноэкранный режим» — это отдельный рабочий стол с анимацией; там окно просто
        // растягивается на весь монитор и поднимается выше строки меню (MakeOverlay).
        if (!OperatingSystem.IsMacOS()) WindowState = WindowState.FullScreen;

        var s = AppServices.Settings;
        Surface.Screenshot = shot;
        // Стеклянные панели редактора размывают замороженный снимок под собой.
        SnapMark.UI.Glass.SetStaticBackdrop(this, shot);
        Surface.Mode = mode;
        Surface.FrameColor = TryParseColor(s.SelectionColor, Color.Parse("#0A84FF"));
        Surface.DrawStyle.Color = TryParseColor(s.LastColor, Colors.Red);
        Surface.DrawStyle.Thickness = s.LastThickness;
        Surface.DrawStyle.FontSize = s.LastFontSize;
        Surface.DrawStyle.Opacity = Math.Clamp(s.LastOpacity, 0.1, 1);
        Surface.DrawStyle.FontFamily = s.TextFont;
        Surface.DrawStyle.FontWeight = s.TextWeight;
        Surface.DrawStyle.Italic = s.TextItalic;
        Surface.DrawStyle.TextFrame = s.TextFrame;
        Surface.DrawStyle.TextShadow = s.TextShadow;
        Surface.DrawStyle.ShadowColor = Color.TryParse(s.TextShadowColor, out var shc) ? shc : null;
        Surface.DrawStyle.PlateColor = Color.TryParse(s.TextPlateColor, out var tpc) ? tpc : null;
        Surface.DrawStyle.BorderColor = Color.TryParse(s.TextBorderColor, out var tbc) ? tbc : null;
        Surface.DrawStyle.MarkerShape = s.MarkerShape;
        Surface.Document.Numbering = s.MarkerNumbering;
        var cs = Surface.CommentStyle;
        cs.Enabled = s.CommentsOnImage;
        cs.FontFamily = s.CommentFont;
        cs.FontWeight = s.CommentWeight;
        cs.Italic = s.CommentItalic;
        cs.Size = s.CommentSize;
        cs.Color = TryParseColor(s.CommentColor, Color.Parse("#1C1C1E"));
        cs.Frame = s.CommentFrame;
        cs.Shadow = s.CommentShadow;
        cs.ShadowColor = Color.TryParse(s.CommentShadowColor, out var cshc) ? cshc : null;
        cs.PlateColor = Color.TryParse(s.CommentPlateColor, out var cpc) ? cpc : null;
        cs.BorderColor = Color.TryParse(s.CommentBorderColor, out var cbc) ? cbc : null;
        if (Enum.TryParse<ToolKind>(s.LastShape, out var shape) && ToolKinds.IsShape(shape)) _currentShape = shape;
        SetRedactMode(Enum.TryParse<RedactMode>(s.LastRedactMode, out var rm) ? rm : RedactMode.Blur, announce: false);
        Surface.RedactStrength = Math.Clamp(s.LastRedactStrength, 1, 10);
        _withMic = s.Audio is AudioSource.Microphone or AudioSource.MicrophoneAndSystem;
        _withSystem = s.Audio is AudioSource.System or AudioSource.MicrophoneAndSystem;

        HintText.Text = mode == CaptureMode.VideoRegion
            ? L.T("Выдели область для записи видео  ·  двойной клик — весь экран  ·  Esc — отмена")
            : L.T("Выдели область  ·  двойной клик — весь экран  ·  Esc — отмена");

        Surface.SelectionChanged += LayoutBars;
        Surface.AnnotationsChanged += OnAnnotationsChanged;
        Surface.SelectedAnnotationChanged += OnSelectedAnnotationChanged;
        Surface.InteractionChanged += () =>
        {
            LayoutObjectBar();
            UpdateHistoryButtons();
        };
        Surface.TextEditRequested += BeginTextEdit;
        Surface.MarkerAdded += OnMarkerAdded;
        Layer.PropertyChanged += (_, e) =>
        {
            if (e.Property == BoundsProperty) LayoutBars();
        };
        ObjectBar.PropertyChanged += (_, e) =>
        {
            if (e.Property == BoundsProperty) OnObjectBarBounds();
        };

        ThicknessSlider.PropertyChanged += OnSliderChanged;
        FontSizeSlider.PropertyChanged += OnSliderChanged;
        OpacitySlider.PropertyChanged += OnSliderChanged;
        CommentSizeSlider.PropertyChanged += OnCommentSizeChanged;

        // Горячие клавиши ловим на этапе «туннеля» — до того, как их заберёт поле комментария.
        // Благодаря этому Ctrl+Z / Ctrl+C работают, даже когда курсор стоит в комментарии.
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);

        BuildSwatches();
        BuildFontCombos();
        BuildFrameSwatches();
        BuildShadowSwatches();
        BuildCommentSwatches();
        SyncCommentControls();
        UpdateShapeButton();
        SyncStyleControls();
        UpdateToolButtons();
        UpdateMicButton();
        UpdateSeriesButton();
        UpdateHistoryButtons();
        InitVideoOptions();

        Opened += (_, _) =>
        {
            PlatformServices.WindowHelper.MakeOverlay(this);
            if (mode == CaptureMode.FullScreen) Surface.SelectAll();
            LayoutBars();
        };
    }

    public Screen? TargetScreen => _screen;

    public void FocusSurface()
    {
        PlatformServices.WindowHelper.ForceForeground(this);
        Surface.Focus();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _shot?.Dispose();
    }

    // ═══════════════════════════ Раскладка панелей ═══════════════════════════
    void LayoutBars()
    {
        double W = Layer.Bounds.Width, H = Layer.Bounds.Height;
        if (W <= 0 || H <= 0) return;

        if (Surface.Selection is not PixelRect sel || sel.Width <= 0)
        {
            HideAllBars();
            Hint.IsVisible = true;
            var hs = MeasureOf(Hint);
            Place(Hint, (W - hs.Width) / 2, 28);
            return;
        }

        Hint.IsVisible = false;

        // Пока тянем рамку — панели прячем, остаётся только чистый прямоугольник.
        if (Surface.IsAdjustingSelection)
        {
            HideAllBars();
            return;
        }

        var v = Surface.ToView(sel);

        if (_mode == CaptureMode.VideoRegion)
        {
            LayoutVideoBars(v, W, H);
            return;
        }

        ToolsBar.IsVisible = ActionsBar.IsVisible = true;
        CommentsPanel.IsVisible = Surface.Document.Items.OfType<MarkerAnnotation>().Any();

        // Инструменты — под выделением, по центру (иначе сверху, иначе внутри).
        var tb = MeasureOf(ToolsBar);
        double tx = Clamp(v.Center.X - tb.Width / 2, 8, W - tb.Width - 8);
        double ty = v.Bottom + 10;
        bool below = true;
        if (ty + tb.Height > H - 8) { ty = v.Top - tb.Height - 10; below = false; }
        if (ty < 8) { ty = v.Bottom - tb.Height - 10; below = false; }
        Place(ToolsBar, tx, ty);

        // Действия — справа от выделения (иначе слева, иначе внутри), но так, чтобы не налезать
        // на инструменты: у маленькой зоны тулбар шире выделения и заходит под панель действий.
        var ab = MeasureOf(ActionsBar);
        double topY = Clamp(v.Top, 8, H - ab.Height - 8);
        // Над тулбаром (если он снизу) или под ним (если сверху).
        double clearY = below ? ty - ab.Height - 10 : ty + tb.Height + 10;
        double rightX = v.Right + 10, leftX = v.Left - ab.Width - 10, insideX = v.Right - ab.Width - 10;
        // Варианты по порядку: X, Y, сторона, куда сдвинуть тулбар (null — оставить по центру).
        var candidates = new (double X, double Y, bool Right, double? ToolsX)[]
        {
            (rightX, topY, true, null),
            (rightX, topY, true, rightX - tb.Width - 10),          // тулбар левее, действия у зоны справа
            (leftX, topY, false, null),
            (leftX, topY, false, leftX + ab.Width + 10),           // тулбар правее, действия у зоны слева
            (rightX, clearY, true, null), (leftX, clearY, false, null),
            (W - ab.Width - 8, topY, true, null), (8, topY, false, null),
            (insideX, topY, false, null),
        };
        double ax = rightX, ay = topY;
        bool actionsRight = true, placed = false;
        foreach (var c in candidates)
        {
            if (c.X < 8 || c.X + ab.Width > W - 8 || c.Y < 8 || c.Y + ab.Height > H - 8) continue;
            double cx = c.ToolsX ?? tx;
            if (cx < 8 || cx + tb.Width > W - 8) continue;
            var toolsRect = new Rect(cx, ty, tb.Width, tb.Height).Inflate(6);
            if (new Rect(c.X, c.Y, ab.Width, ab.Height).Intersects(toolsRect)) continue;
            (ax, ay, actionsRight, placed, tx) = (c.X, c.Y, c.Right, true, cx);
            break;
        }
        if (!placed)
        {
            // Совсем тесно: действия у выделения, тулбар — под (или над) ними.
            ax = rightX + ab.Width <= W - 8 ? rightX : leftX >= 8 ? leftX : insideX;
            actionsRight = ax == rightX;
            double ny = ay + ab.Height + 10;
            if (ny + tb.Height <= H - 8) { ty = ny; below = true; }
            else { ty = Math.Max(8, ay - tb.Height - 10); below = false; }
        }
        Place(ToolsBar, tx, ty);
        Place(ActionsBar, ax, ay);

        // Всплывающие панели — рядом с тулбаром, со стороны от выделения.
        PlacePopover(StylePanel, StyleButton, tx, ty, tb, below, W, H);
        PlacePopover(ShapesPanel, ShapeButton, tx, ty, tb, below, W, H);

        if (CommentsPanel.IsVisible)
        {
            // Комментарии — там, где они не закрывают панель действий и инструменты: рядом с действиями снаружи,
            // по другую сторону выделения, слева от действий, под или над ними, в углах. Если выделение во весь
            // экран, действия стоят внутри у правого края — тогда панель встаёт левее них, а не поверх.
            var cp = MeasureOf(CommentsPanel);
            var actionsRect = new Rect(ax, ay, ab.Width, ab.Height).Inflate(8);
            var toolsRect = new Rect(tx, ty, tb.Width, tb.Height).Inflate(8);
            var popovers = new[] { StylePanel, ShapesPanel }.Where(p => p.IsVisible)
                .Select(p => new Rect(Canvas.GetLeft(p), Canvas.GetTop(p), MeasureOf(p).Width, MeasureOf(p).Height).Inflate(8)).ToList();
            double topCy = Clamp(v.Top, 8, H - cp.Height - 8);
            var spots = new (double X, double Y)[]
            {
                (actionsRight ? ax + ab.Width + 10 : ax - cp.Width - 10, topCy),
                (actionsRight ? v.Left - cp.Width - 10 : v.Right + 10, topCy),
                (ax - cp.Width - 10, Clamp(ay, 8, H - cp.Height - 8)),
                (ax + ab.Width + 10, Clamp(ay, 8, H - cp.Height - 8)),
                (Clamp(ax + ab.Width - cp.Width, 8, W - cp.Width - 8), ay + ab.Height + 10),
                (Clamp(ax + ab.Width - cp.Width, 8, W - cp.Width - 8), ay - cp.Height - 10),
                (Clamp(v.Left + 10, 8, W - cp.Width - 8), topCy),
                (8, 8), (W - cp.Width - 8, 8), (8, H - cp.Height - 8), (W - cp.Width - 8, H - cp.Height - 8),
            };
            double cx = ax - cp.Width - 10, cy = topCy;
            foreach (var (sx, sy) in spots)
            {
                if (sx < 8 || sy < 8 || sx + cp.Width > W - 8 || sy + cp.Height > H - 8) continue;
                var r = new Rect(sx, sy, cp.Width, cp.Height);
                if (r.Intersects(actionsRect) || r.Intersects(toolsRect) || popovers.Any(p => r.Intersects(p))) continue;
                (cx, cy) = (sx, sy);
                break;
            }
            Place(CommentsPanel, Clamp(cx, 8, Math.Max(8, W - cp.Width - 8)), cy);
        }

        LayoutObjectBar();
    }

    void PlacePopover(Control panel, Control anchor, double tx, double ty, Size tb, bool below, double W, double H)
    {
        if (!panel.IsVisible) return;
        var size = MeasureOf(panel);
        double anchorX = tx + tb.Width / 2;
        // Относительно тулбара: его могли только что сдвинуть, а раскладка ещё не обновилась.
        var origin = anchor.TranslatePoint(new Point(anchor.Bounds.Width / 2, 0), ToolsBar);
        if (origin is Point o) anchorX = tx + o.X;
        double px = Clamp(anchorX - size.Width / 2, 8, W - size.Width - 8);
        double py = below ? ty + tb.Height + 8 : ty - size.Height - 8;
        if (py + size.Height > H - 8 || py < 8) py = Clamp(ty - size.Height - 8, 8, H - size.Height - 8);
        Place(panel, px, py);
    }

    void HideAllBars()
    {
        ToolsBar.IsVisible = ActionsBar.IsVisible = VideoBar.IsVisible = VideoOptionsPanel.IsVisible = false;
        StylePanel.IsVisible = ShapesPanel.IsVisible = CommentsPanel.IsVisible = ObjectBar.IsVisible = false;
    }

    static Size MeasureOf(Control c)
    {
        c.Measure(Size.Infinity);
        return c.DesiredSize;
    }

    static void Place(Control c, double x, double y)
    {
        Canvas.SetLeft(c, Math.Round(x));
        Canvas.SetTop(c, Math.Round(y));
    }

    static double Clamp(double v, double min, double max) => max < min ? min : Math.Max(min, Math.Min(max, v));

    // ═══════════════════════════ Инструменты ═══════════════════════════
    void OnToolClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && Enum.TryParse<ToolKind>(tag, out var tool)) SetTool(tool);
    }

    void SetTool(ToolKind tool)
    {
        // «Скрыть» уже выбран — повторное нажатие меняет режим: размытие ⇄ пиксели.
        if (tool == ToolKind.Redact && Surface.Tool == ToolKind.Redact)
            SetRedactMode(Surface.RedactMode == RedactMode.Blur ? RedactMode.Pixelate : RedactMode.Blur, announce: true);
        CommitTextEdit();
        if (ToolKinds.IsShape(tool) && tool != _currentShape)
        {
            _currentShape = tool;
            AppServices.Settings.LastShape = tool.ToString();
            AppServices.SaveSettings();
            UpdateShapeButton();
        }
        Surface.Tool = tool;
        ShapesPanel.IsVisible = false;
        UpdateToolButtons();
        LayoutBars();
        Surface.Focus();
    }

    void OnShapesMenuClick(object? sender, RoutedEventArgs e)
    {
        ShapesPanel.IsVisible = !ShapesPanel.IsVisible;
        StylePanel.IsVisible = false;
        LayoutBars();
    }

    void OnShapeChoice(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && Enum.TryParse<ToolKind>(tag, out var tool)) SetTool(tool);
    }

    void UpdateShapeButton()
    {
        ShapeButton.Tag = _currentShape.ToString();
        ShapeIcon.Data = UiKit.Geometry(_currentShape switch
        {
            ToolKind.Line => "Icon.Line",
            ToolKind.DoubleArrow => "Icon.DoubleArrow",
            ToolKind.Rectangle => "Icon.Rect",
            ToolKind.Ellipse => "Icon.Ellipse",
            _ => "Icon.Arrow",
        });
    }

    void UpdateToolButtons()
    {
        var current = Surface.Tool.ToString();
        foreach (var b in ToolButtons.Children.OfType<Button>().Concat(ToolButtons2.Children.OfType<Button>()))
            b.Classes.Set("selected", b.Tag is string t && t == current);
        foreach (var b in ShapeChoices.Children.OfType<Button>())
            b.Classes.Set("selected", b.Tag is string t && t == _currentShape.ToString());
    }

    void SetRedactMode(RedactMode mode, bool announce)
    {
        Surface.RedactMode = mode;
        RedactIcon.Data = this.FindResource(mode == RedactMode.Pixelate ? "Icon.Pixelate" : "Icon.Blur") as Geometry;
        if (AppServices.Settings.LastRedactMode != mode.ToString())
        {
            AppServices.Settings.LastRedactMode = mode.ToString();
            AppServices.SaveSettings();
        }
        if (announce)
            Toast.Show(L.T("Скрыть"), mode == RedactMode.Pixelate ? L.T("Режим: пиксели") : L.T("Режим: размытие"));
    }

    void UpdateHistoryButtons()
    {
        UndoButton.IsEnabled = Surface.Document.CanUndo;
        RedoButton.IsEnabled = Surface.Document.CanRedo;
    }

    // ═══════════════════════════ Стиль ═══════════════════════════
    void BuildSwatches()
    {
        foreach (var hex in Palette)
        {
            var color = Color.Parse(hex);
            var btn = new Button { Background = new SolidColorBrush(color), Tag = hex };
            btn.Classes.Add("swatch");
            btn.Click += (_, _) =>
            {
                Surface.DrawStyle.Color = color;
                OnStyleEdited();
            };
            Swatches.Children.Add(btn);
        }
    }

    void SyncStyleControls()
    {
        _syncingStyle = true;
        var st = Surface.DrawStyle;
        ColorDot.Fill = new SolidColorBrush(Annotation.WithOpacity(st.Color, st.Opacity));
        ThicknessSlider.Value = st.Thickness;
        FontSizeSlider.Value = st.FontSize;
        OpacitySlider.Value = Math.Round(st.Opacity * 100);
        ThicknessValue.Text = ((int)st.Thickness).ToString();
        FontSizeValue.Text = ((int)st.FontSize).ToString();
        OpacityValue.Text = (int)Math.Round(st.Opacity * 100) + "%";
        ItalicToggle.IsChecked = st.Italic;
        DashToggle.IsChecked = st.Dashed;
        FillToggle.IsChecked = st.Filled;
        CheckChips(WeightChips, st.FontWeight.ToString());
        CheckChips(TextFrameChips, st.TextFrame.ToString());
        Dispatcher.UIThread.Post(RebuildTextFrameColors);
        ShadowToggle.IsChecked = st.TextShadow;
        ShadowAutoToggle.IsChecked = st.ShadowColor is null;
        ShadowAutoToggle.IsEnabled = ShadowSwatches.IsEnabled = st.TextShadow;
        var shadowHex = st.ShadowColor is Color sc ? ToHex(sc) : null;
        foreach (var b in ShadowSwatches.Children.OfType<Button>())
            b.Classes.Set("current", string.Equals(b.Tag as string, shadowHex, StringComparison.OrdinalIgnoreCase));
        SelectFont(FontCombo, st.FontFamily);
        foreach (var b in MarkerShapeButtons.Children.OfType<Button>())
            b.Classes.Set("selected", b.Tag is string t && t == st.MarkerShape.ToString());
        LettersToggle.IsChecked = Surface.Document.Numbering == MarkerNumbering.Letters;
        NumbersToggle.IsChecked = Surface.Document.Numbering == MarkerNumbering.Numbers;

        var hex = ToHex(st.Color);
        foreach (var b in Swatches.Children.OfType<Button>())
            b.Classes.Set("current", string.Equals(b.Tag as string, hex, StringComparison.OrdinalIgnoreCase));
        foreach (var b in WidthButtons.Children.OfType<Button>())
            b.Classes.Set("selected", b.Tag is string t && double.TryParse(t, out var w) && Math.Abs(w - st.Thickness) < 0.5);
        _syncingStyle = false;
    }

    void OnSliderChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != RangeBase.ValueProperty || _syncingStyle) return;
        Surface.DrawStyle.Thickness = ThicknessSlider.Value;
        Surface.DrawStyle.FontSize = FontSizeSlider.Value;
        Surface.DrawStyle.Opacity = OpacitySlider.Value / 100.0;
        OnStyleEdited();
    }

    void OnWidthClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string t } && double.TryParse(t, out var w))
        {
            Surface.DrawStyle.Thickness = w;
            OnStyleEdited();
        }
    }

    void OnStyleToggle(object? sender, RoutedEventArgs e)
    {
        if (_syncingStyle) return;
        var st = Surface.DrawStyle;
        st.Italic = ItalicToggle.IsChecked == true;
        st.Dashed = DashToggle.IsChecked == true;
        st.Filled = FillToggle.IsChecked == true;
        OnStyleEdited();
    }

    /// <summary>
    /// Общая панель — это значения ПО УМОЛЧАНИЮ для новых объектов. Уже нарисованные объекты она не трогает:
    /// их меняют в контекстной панели рядом с объектом. Исключение — текст, который сейчас набирается впервые:
    /// он ещё «создаётся», поэтому получает новые значения по умолчанию.
    /// </summary>
    void OnStyleEdited()
    {
        if (_editing is not null && _editor is not null && _editingIsNew)
        {
            _editing.ApplyStyle(Surface.DrawStyle);
            StyleEditor(_editor, _editing);
        }
        SyncStyleControls();
        SaveStyleSettings();
        RequestContextRebuild();
    }

    void SaveStyleSettings()
    {
        var s = AppServices.Settings;
        s.LastColor = ToHex(Surface.DrawStyle.Color);
        s.LastThickness = Surface.DrawStyle.Thickness;
        s.LastFontSize = Surface.DrawStyle.FontSize;
        s.LastOpacity = Surface.DrawStyle.Opacity;
        s.TextFont = Surface.DrawStyle.FontFamily;
        s.TextWeight = Surface.DrawStyle.FontWeight;
        s.TextItalic = Surface.DrawStyle.Italic;
        s.TextFrame = Surface.DrawStyle.TextFrame;
        s.TextShadow = Surface.DrawStyle.TextShadow;
        s.TextShadowColor = Surface.DrawStyle.ShadowColor is Color tsc ? ToHex(tsc) : "";
        s.TextPlateColor = Surface.DrawStyle.PlateColor is Color tp ? ToHex(tp) : "";
        s.TextBorderColor = Surface.DrawStyle.BorderColor is Color tb ? ToHex(tb) : "";
        AppServices.SaveSettings();
    }

    // ── Текст: шрифт, начертание, рамка ────────────────────────────────
    void BuildFontCombos()
    {
        foreach (var combo in new[] { FontCombo, CommentFontCombo })
        {
            foreach (var name in FontCatalog.Names)
                combo.Items.Add(new ComboBoxItem { Content = name, Tag = name, FontFamily = FontCatalog.Get(name) });
        }
        FontCombo.SelectionChanged += (_, _) =>
        {
            if (_syncingStyle || FontCombo.SelectedItem is not ComboBoxItem { Tag: string name }) return;
            Surface.DrawStyle.FontFamily = name;
            OnStyleEdited();
        };
        CommentFontCombo.SelectionChanged += (_, _) =>
        {
            if (_syncingStyle || CommentFontCombo.SelectedItem is not ComboBoxItem { Tag: string name }) return;
            Surface.CommentStyle.FontFamily = name;
            OnCommentStyleEdited();
        };
    }

    static void SelectFont(ComboBox combo, string font)
    {
        combo.SelectedItem = combo.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(i => string.Equals(i.Tag as string, font, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Группа чипов как радиокнопки: отмечен только чип с нужным Tag.</summary>
    static void CheckChips(Panel panel, string tag)
    {
        foreach (var t in panel.Children.OfType<ToggleButton>())
            if (t.Tag is string tg) t.IsChecked = tg == tag;
    }

    void OnWeightClick(object? sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: string t } && int.TryParse(t, out var w))
        {
            Surface.DrawStyle.FontWeight = w;
            OnStyleEdited();
        }
    }

    void OnTextFrameClick(object? sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: string t } && Enum.TryParse<TextFrame>(t, out var f))
        {
            Surface.DrawStyle.TextFrame = f;
            OnStyleEdited();
        }
    }

    // ── Метки: форма и обозначения ───────────────────────────────────
    void OnMarkerShapeClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string t } || !Enum.TryParse<MarkerShape>(t, out var shape)) return;
        Surface.DrawStyle.MarkerShape = shape; // форма по умолчанию для новых меток
        AppServices.Settings.MarkerShape = shape;
        AppServices.SaveSettings();
        SyncStyleControls();
    }

    void OnNumberingClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: string t } || !Enum.TryParse<MarkerNumbering>(t, out var numbering)) return;
        Surface.SetNumbering(numbering);
        AppServices.Settings.MarkerNumbering = numbering;
        AppServices.SaveSettings();
        SyncStyleControls();
    }

    // ── Цвет рамки выделения (сохраняется в настройках) ────────────────
    static readonly string[] FrameColorChoices =
        { "#0A84FF", "#FFFFFF", "#34C759", "#FFCC00", "#FF9500", "#FF3B30", "#AF52DE", "#FF2D55", "#8E8E93", "#000000" };

    void BuildFrameSwatches()
    {
        foreach (var hex in FrameColorChoices)
        {
            var color = Color.Parse(hex);
            var btn = new Button { Background = new SolidColorBrush(color), Tag = hex };
            btn.Classes.Add("swatch");
            btn.Classes.Add("mini");
            btn.Click += (_, _) =>
            {
                Surface.FrameColor = color;
                AppServices.Settings.SelectionColor = hex;
                AppServices.SaveSettings();
                UpdateFrameSwatches();
            };
            FrameSwatches.Children.Add(btn);
        }
        UpdateFrameSwatches();
    }

    void UpdateFrameSwatches()
    {
        foreach (var b in FrameSwatches.Children.OfType<Button>())
            b.Classes.Set("current", string.Equals(b.Tag as string, AppServices.Settings.SelectionColor, StringComparison.OrdinalIgnoreCase));
    }

    // ── Комментарии на картинке ───────────────────────────────────────
    static readonly string[] CommentColorChoices =
        { "#1C1C1E", "#FFFFFF", "#FF3B30", "#FF9500", "#FFCC00", "#34C759", "#0A84FF", "#AF52DE" };

    void BuildCommentSwatches()
    {
        foreach (var hex in CommentColorChoices)
        {
            var color = Color.Parse(hex);
            var btn = new Button { Background = new SolidColorBrush(color), Tag = hex };
            btn.Classes.Add("swatch");
            btn.Classes.Add("mini");
            btn.Click += (_, _) =>
            {
                Surface.CommentStyle.Color = color;
                OnCommentStyleEdited();
            };
            CommentColorSwatches.Children.Add(btn);
        }
    }

    void SyncCommentControls()
    {
        _syncingStyle = true;
        var cs = Surface.CommentStyle;
        OnImageToggle.IsChecked = cs.Enabled;
        CommentItalicToggle.IsChecked = cs.Italic;
        CommentSizeSlider.Value = cs.Size;
        CommentSizeValue.Text = ((int)cs.Size).ToString();
        CheckChips(CommentWeightChips, cs.FontWeight.ToString());
        CheckChips(CommentFrameChips, cs.Frame.ToString());
        Dispatcher.UIThread.Post(RebuildCommentFrameColors);
        CommentShadowToggle.IsChecked = cs.Shadow;
        SelectFont(CommentFontCombo, cs.FontFamily);
        var hex = ToHex(cs.Color);
        foreach (var b in CommentColorSwatches.Children.OfType<Button>())
            b.Classes.Set("current", string.Equals(b.Tag as string, hex, StringComparison.OrdinalIgnoreCase));
        CommentsCaption.Text = cs.Enabled
            ? L.T("Рисуются на картинке и копируются текстом.")
            : L.T("Копируются текстом. Enter — готово, Shift+Enter — новая строка, × — удалить.");
        _syncingStyle = false;
    }

    void OnCommentStyleEdited()
    {
        // Общие настройки подписей — значения по умолчанию для новых меток (у существующих — свои, в их панели).
        RequestContextRebuild();
        SyncCommentControls();
        Surface.InvalidateVisual();
        var s = AppServices.Settings;
        var cs = Surface.CommentStyle;
        s.CommentsOnImage = cs.Enabled;
        s.CommentFont = cs.FontFamily;
        s.CommentWeight = cs.FontWeight;
        s.CommentItalic = cs.Italic;
        s.CommentSize = cs.Size;
        s.CommentColor = ToHex(cs.Color);
        s.CommentFrame = cs.Frame;
        s.CommentShadow = cs.Shadow;
        s.CommentShadowColor = cs.ShadowColor is Color csc ? ToHex(csc) : "";
        s.CommentPlateColor = cs.PlateColor is Color cp ? ToHex(cp) : "";
        s.CommentBorderColor = cs.BorderColor is Color cb ? ToHex(cb) : "";
        AppServices.SaveSettings();
    }

    void OnOnImageClick(object? sender, RoutedEventArgs e)
    {
        Surface.CommentStyle.Enabled = OnImageToggle.IsChecked == true;
        OnCommentStyleEdited();
    }

    void OnCommentStyleClick(object? sender, RoutedEventArgs e)
    {
        CommentStyleBox.IsVisible = !CommentStyleBox.IsVisible;
        LayoutBars();
    }

    void OnCommentWeightClick(object? sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: string t } && int.TryParse(t, out var w))
        {
            Surface.CommentStyle.FontWeight = w;
            OnCommentStyleEdited();
        }
    }

    void OnCommentItalicClick(object? sender, RoutedEventArgs e)
    {
        Surface.CommentStyle.Italic = CommentItalicToggle.IsChecked == true;
        OnCommentStyleEdited();
    }

    void OnCommentFrameClick(object? sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: string t } && Enum.TryParse<TextFrame>(t, out var f))
        {
            Surface.CommentStyle.Frame = f;
            OnCommentStyleEdited();
        }
    }

    /// <summary>Цвета рамки/подложки в общей панели (строки видны только для выбранного режима).</summary>
    void RebuildTextFrameColors()
    {
        var st = Surface.DrawStyle;
        TextFrameColors.Children.Clear();
        foreach (var row in FrameColorRows(st.TextFrame, st.BorderColor, st.PlateColor,
                     c => { st.BorderColor = c; OnStyleEdited(); },
                     c => { st.PlateColor = c; OnStyleEdited(); },
                     L.T("Цвет текста")))
            TextFrameColors.Children.Add(row);
    }

    void RebuildCommentFrameColors()
    {
        var cs = Surface.CommentStyle;
        CommentFrameColors.Children.Clear();
        foreach (var row in FrameColorRows(cs.Frame, cs.BorderColor, cs.PlateColor,
                     c => { cs.BorderColor = c; OnCommentStyleEdited(); },
                     c => { cs.PlateColor = c; OnCommentStyleEdited(); },
                     L.T("Цвет метки")))
            CommentFrameColors.Children.Add(row);
    }

    void OnCommentShadowClick(object? sender, RoutedEventArgs e)
    {
        Surface.CommentStyle.Shadow = CommentShadowToggle.IsChecked == true;
        OnCommentStyleEdited();
    }

    // ── Тень текста ───────────────────────────────────────────────────
    static readonly string[] ShadowColorChoices = { "#000000", "#FFFFFF", "#8E8E93", "#0A84FF", "#FF3B30", "#FFCC00" };

    void BuildShadowSwatches()
    {
        foreach (var hex in ShadowColorChoices)
        {
            var color = Color.Parse(hex);
            var btn = new Button { Background = new SolidColorBrush(color), Tag = hex };
            btn.Classes.Add("swatch");
            btn.Classes.Add("mini");
            ToolTip.SetTip(btn, L.T("Цвет тени"));
            btn.Click += (_, _) =>
            {
                Surface.DrawStyle.TextShadow = true;
                Surface.DrawStyle.ShadowColor = color;
                OnStyleEdited();
            };
            ShadowSwatches.Children.Add(btn);
        }
    }

    void OnShadowToggle(object? sender, RoutedEventArgs e)
    {
        Surface.DrawStyle.TextShadow = ShadowToggle.IsChecked == true;
        OnStyleEdited();
    }

    void OnShadowAutoClick(object? sender, RoutedEventArgs e)
    {
        Surface.DrawStyle.ShadowColor = null;
        OnStyleEdited();
    }

    void OnCommentSizeChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != RangeBase.ValueProperty || _syncingStyle) return;
        Surface.CommentStyle.Size = CommentSizeSlider.Value;
        OnCommentStyleEdited();
    }

    void OnSelectedAnnotationChanged()
    {
        // Стиль выделенного объекта показывается в его контекстной панели, общие настройки не меняются.
        RequestContextRebuild();
    }

    void OnStyleClick(object? sender, RoutedEventArgs e)
    {
        StylePanel.IsVisible = !StylePanel.IsVisible;
        ShapesPanel.IsVisible = false;
        LayoutBars();
    }

    void OnUndoClick(object? sender, RoutedEventArgs e) => DoUndo();
    void OnRedoClick(object? sender, RoutedEventArgs e) => DoRedo();

    void DoUndo()
    {
        CommitTextEdit();
        Surface.Undo();
    }

    void DoRedo()
    {
        CommitTextEdit();
        Surface.Redo();
    }

    void OnDuplicateClick(object? sender, RoutedEventArgs e) => Surface.DuplicateSelected();
    void OnDeleteObjectClick(object? sender, RoutedEventArgs e) => Surface.DeleteSelected();

    // ═══════════════════════════ Текст ═══════════════════════════
    void BeginTextEdit(TextAnnotation ta, bool isNew)
    {
        CommitTextEdit();
        _editing = ta;
        _editingIsNew = isNew;
        Surface.EditingText = ta;
        var tb = new TextBox
        {
            Text = ta.Text,
            AcceptsReturn = true,
            MinWidth = 80,
            TextWrapping = TextWrapping.NoWrap,
        };
        tb.Classes.Add("inline");
        StyleEditor(tb, ta);

        tb.KeyDown += (_, e) =>
        {
            bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
            if (e.Key == Key.Escape || (e.Key == Key.Enter && ctrl))
            {
                CommitTextEdit();
                Surface.Focus();
                e.Handled = true;
            }
        };
        tb.LostFocus += (_, _) => CommitTextEdit();

        Layer.Children.Add(tb);
        _editor = tb;
        RequestContextRebuild();
        Dispatcher.UIThread.Post(() =>
        {
            tb.Focus();
            tb.CaretIndex = tb.Text?.Length ?? 0;
        });
        Surface.InvalidateVisual();
    }

    void StyleEditor(TextBox tb, TextAnnotation ta)
    {
        var s = Surface.Scale;
        var brush = new SolidColorBrush(ta.Color);
        tb.FontFamily = FontCatalog.Get(ta.FontFamily);
        tb.FontSize = ta.FontSize / s;
        tb.FontWeight = (FontWeight)Math.Clamp(ta.FontWeight, 100, 950);
        tb.FontStyle = ta.Italic ? FontStyle.Italic : FontStyle.Normal;
        tb.Foreground = brush;
        tb.CaretBrush = brush;
        Canvas.SetLeft(tb, ta.Position.X / s - 3);
        Canvas.SetTop(tb, ta.Position.Y / s - 1);
    }

    void CommitTextEdit()
    {
        if (_editor is null || _editing is null) return;
        var tb = _editor;
        var ta = _editing;
        bool isNew = _editingIsNew;
        _editor = null;
        _editing = null;
        Surface.EditingText = null;
        Layer.Children.Remove(tb);

        var text = (tb.Text ?? "").TrimEnd();
        var doc = Surface.Document;
        if (isNew)
        {
            if (text.Length > 0)
            {
                var snap = doc.Snapshot();
                ta.Text = text;
                doc.Items.Add(ta);
                doc.PushUndo(snap);
            }
        }
        else if (text != ta.Text)
        {
            var snap = doc.Snapshot();
            if (text.Length == 0) doc.Items.Remove(ta);
            else ta.Text = text;
            doc.PushUndo(snap);
        }
        // Готовый текст остаётся выделенным — рядом его панель настроек.
        Surface.Select(doc.Items.Contains(ta) ? ta : null);
        Surface.InvalidateVisual();
        OnAnnotationsChanged();
        RequestContextRebuild();
    }

    // ═══════════════════════════ Метки и комментарии ═══════════════════════════
    void OnMarkerAdded(MarkerAnnotation m)
    {
        RebuildComments(force: true);
        // Фокус в поле комментария новой метки — можно сразу печатать.
        Dispatcher.UIThread.Post(() =>
        {
            var box = CommentsList.Children.OfType<Grid>()
                .Select(g => g.Children.OfType<TextBox>().FirstOrDefault())
                .FirstOrDefault(t => t?.Tag is Guid id && id == m.Id);
            box?.Focus();
        });
    }

    void OnAnnotationsChanged()
    {
        RebuildComments(force: false);
        UpdateHistoryButtons();
        LayoutObjectBar();
    }

    void RebuildComments(bool force)
    {
        var markers = Surface.Document.MarkersInOrder().ToList();
        var signature = string.Join(",", markers.Select(m => m.Id + ":" + m.Label + ":" + m.Color));
        if (!force && signature == _commentsSignature) return;
        _commentsSignature = signature;

        CommentsList.Children.Clear();
        foreach (var m in markers)
        {
            var id = m.Id;
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };

            var badge = new Border
            {
                Background = new SolidColorBrush(m.Color),
                Child = new TextBlock { Text = m.Label },
            };
            badge.Classes.Add("badge");

            var box = new TextBox
            {
                Watermark = L.F("Комментарий к {0}…", m.Label),
                Text = Surface.Document.GetComment(m),
                Tag = id,
                Margin = new Thickness(8, 0, 4, 0),
                AcceptsReturn = true, // Shift+Enter — новая строка (см. обработчик ниже)
                MaxHeight = 160,
            };
            box.Classes.Add("comment");
            box.PropertyChanged += (_, e) =>
            {
                if (e.Property != TextBox.TextProperty) return;
                Surface.Document.Comments[id] = box.Text ?? "";
                if (Surface.CommentStyle.Enabled) Surface.InvalidateVisual();
            };
            // Перехватываем раньше самого поля (туннель): Enter — готово, Shift+Enter — перенос строки, Esc — выйти.
            box.AddHandler(KeyDownEvent, (_, e) =>
            {
                if ((e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift)) || e.Key == Key.Escape)
                {
                    Surface.Focus();
                    e.Handled = true;
                }
            }, RoutingStrategies.Tunnel);

            var delete = UiKit.ToolButton("Icon.Close", L.F("Удалить метку {0}", m.Label), () => Surface.DeleteMarker(id), "small", "danger");
            delete.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;
            delete.Margin = new Thickness(0, 2, 0, 0);

            Grid.SetColumn(box, 1);
            Grid.SetColumn(delete, 2);
            row.Children.Add(badge);
            row.Children.Add(box);
            row.Children.Add(delete);
            CommentsList.Children.Add(row);
        }

        CommentsPanel.IsVisible = markers.Count > 0 && _mode != CaptureMode.VideoRegion;
        LayoutBars();
    }

    // ═══════════════════════════ Клавиатура ═══════════════════════════
    /// <summary>
    /// Туннельный обработчик: срабатывает раньше, чем клавишу получит TextBox.
    /// В поле комментария перехватываем только «редакторские» сочетания: Ctrl+Z/Y — отмена рисования,
    /// Ctrl+C — копировать скриншот (если в поле ничего не выделено), Ctrl+S/P/G/D/R/J.
    /// Во встроенном редакторе текста на картинке ничего не трогаем.
    /// </summary>
    void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        var focused = FocusManager?.GetFocusedElement();
        if (focused is not TextBox tb || ReferenceEquals(tb, _editor)) return;

        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        if (!ctrl) return;
        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        bool hasTextSelection = tb.SelectionStart != tb.SelectionEnd;

        switch (e.Key)
        {
            case Key.C when hasTextSelection:
                return; // копируем выделенный текст комментария
            case Key.Z or Key.Y or Key.C or Key.S or Key.P or Key.G or Key.D or Key.R or Key.J:
                e.Handled = TryEditorShortcut(e.Key, ctrl: true, shift);
                return;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // Пока печатаем в TextBox — остальное обрабатывает сам TextBox (см. OnPreviewKeyDown).
        if (FocusManager?.GetFocusedElement() is TextBox)
        {
            base.OnKeyDown(e);
            return;
        }

        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        bool hasSelection = Surface.Selection is not null;

        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                if (StylePanel.IsVisible || ShapesPanel.IsVisible || VideoOptionsPanel.IsVisible)
                {
                    StylePanel.IsVisible = ShapesPanel.IsVisible = VideoOptionsPanel.IsVisible = false;
                    LayoutBars();
                }
                else if (Surface.SelectedAnnotation is not null) Surface.ClearSelectedAnnotation();
                else CaptureController.CloseAll();
                return;

            case Key.Enter when _mode == CaptureMode.VideoRegion && hasSelection:
                e.Handled = true;
                StartRecording();
                return;

            case Key.A when ctrl:
                e.Handled = true;
                Surface.SelectAll();
                return;
        }

        if (hasSelection && _mode != CaptureMode.VideoRegion && TryEditorShortcut(e.Key, ctrl, shift))
        {
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    /// <summary>Сочетания редактора. Возвращает true, если клавиша обработана.</summary>
    bool TryEditorShortcut(Key key, bool ctrl, bool shift)
    {
        if (Surface.Selection is null || _mode == CaptureMode.VideoRegion) return false;

        if (ctrl)
        {
            switch (key)
            {
                case Key.Z when shift: DoRedo(); return true;
                case Key.Z: DoUndo(); return true;
                case Key.Y: DoRedo(); return true;
                case Key.C: _ = DoCopyAsync(); return true;
                case Key.S when shift: _ = DoSaveAsAsync(); return true;
                case Key.S: _ = DoQuickSaveAsync(); return true;
                case Key.P: _ = DoPrintAsync(); return true;
                case Key.G: _ = DoSearchAsync(); return true;
                case Key.T: _ = DoOcrAsync(); return true;
                case Key.F: DoPin(); return true;
                case Key.L: DoLongShot(); return true;
                case Key.D: DoAddToSeries(); return true;
                case Key.R: StartRecording(); return true;
                case Key.J: Surface.DuplicateSelected(); return true;
            }
            return false;
        }

        switch (key)
        {
            case Key.Delete:
            case Key.Back:
                Surface.DeleteSelected();
                return true;
            case Key.V: SetTool(ToolKind.Select); return true;
            case Key.P: SetTool(ToolKind.Pen); return true;
            case Key.C: SetTool(ToolKind.Pencil); return true;
            case Key.H: SetTool(ToolKind.Highlighter); return true;
            case Key.E: SetTool(ToolKind.Eraser); return true;
            case Key.S: SetTool(_currentShape); return true;
            case Key.L: SetTool(ToolKind.Line); return true;
            case Key.A: SetTool(ToolKind.Arrow); return true;
            case Key.D: SetTool(ToolKind.DoubleArrow); return true;
            case Key.R: SetTool(ToolKind.Rectangle); return true;
            case Key.O: SetTool(ToolKind.Ellipse); return true;
            case Key.T: SetTool(ToolKind.Text); return true;
            case Key.M: SetTool(ToolKind.Marker); return true;
            case Key.N: SetTool(ToolKind.MarkerArrow); return true;
            case Key.B: SetTool(ToolKind.Redact); return true;
        }
        return false;
    }

    // ═══════════════════════════ Действия ═══════════════════════════
    void OnCopyClick(object? s, RoutedEventArgs e) => _ = DoCopyAsync();
    void OnSaveClick(object? s, RoutedEventArgs e) => _ = DoSaveAsAsync();
    void OnPrintClick(object? s, RoutedEventArgs e) => _ = DoPrintAsync();
    void OnSearchClick(object? s, RoutedEventArgs e) => _ = DoSearchAsync();
    void OnSeriesClick(object? s, RoutedEventArgs e) => DoAddToSeries();
    void OnOcrClick(object? s, RoutedEventArgs e) => _ = DoOcrAsync();
    void OnPinClick(object? s, RoutedEventArgs e) => DoPin();
    void OnLongShotClick(object? s, RoutedEventArgs e) => DoLongShot();

    /// <summary>Длинный скриншот выделенной области: прокрутка и склейка.</summary>
    void DoLongShot()
    {
        CommitTextEdit();
        if (_screen is null || Surface.Selection is not PixelRect sel || sel.Width < 40 || sel.Height < 60)
        {
            Toast.Show(L.T("Длинный скриншот"), L.T("Выдели прокручиваемую часть окна — страницу, документ, список."));
            return;
        }
        var at = new PixelRect(_screen.Bounds.X + sel.X, _screen.Bounds.Y + sel.Y, sel.Width, sel.Height);
        var scaling = _screen.Scaling;
        Finish();
        LongShotController.Start(at, scaling);
    }
    void OnCloseClick(object? s, RoutedEventArgs e) => CaptureController.CloseAll();
    void OnRecordClick(object? s, RoutedEventArgs e) => StartRecording();
    void OnRecordFromScreenshotClick(object? s, RoutedEventArgs e) => StartRecording();

    void OnMicClick(object? s, RoutedEventArgs e)
    {
        _withMic = !_withMic;
        UpdateMicButton();
        SaveAudioChoice();
        SyncVideoOptions();
    }

    void OnSpeakerClick(object? s, RoutedEventArgs e)
    {
        _withSystem = !_withSystem;
        UpdateMicButton();
        SaveAudioChoice();
        SyncVideoOptions();
    }

    /// <summary>Выбор в панели записи запоминается как источник звука по умолчанию.</summary>
    void SaveAudioChoice()
    {
        AppServices.Settings.Audio = (_withMic, _withSystem) switch
        {
            (true, true) => AudioSource.MicrophoneAndSystem,
            (true, false) => AudioSource.Microphone,
            (false, true) => AudioSource.System,
            _ => AudioSource.None,
        };
        AppServices.SaveSettings();
    }

    void UpdateMicButton()
    {
        MicButton.Classes.Set("off", !_withMic);
        SpeakerButton.Classes.Set("off", !_withSystem);
        ToolTip.SetTip(MicButton, _withMic ? L.T("Микрофон: включён") : L.T("Микрофон: выключен"));
        ToolTip.SetTip(SpeakerButton, _withSystem ? L.T("Звук компьютера: включён") : L.T("Звук компьютера: выключен"));
    }

    void UpdateSeriesButton()
    {
        int n = AppServices.Series.Shots.Count;
        ToolTip.SetTip(SeriesButton, n == 0
            ? L.T("Добавить в серию (Ctrl+D)")
            : L.F("Добавить в серию (Ctrl+D). Сейчас в серии: {0}", n));
    }

    RenderResult? RenderCurrent()
    {
        CommitTextEdit();
        if (_shot is null || Surface.Selection is not PixelRect sel || sel.Width < 1 || sel.Height < 1) return null;

        byte[] png;
        var area = new Rect(sel.X, sel.Y, sel.Width, sel.Height);
        using (var rtb = ImageRenderer.Render(_shot, sel, Surface.Document.Items,
                   ctx => CommentLabels.Render(ctx, Surface.Document, Surface.CommentStyle, area)))
            png = ImageEncoder.ToPng(rtb);

        var markers = Surface.Document.MarkersInOrder()
            .Select(m => new SeriesMarker { Label = m.Label, Comment = Surface.Document.GetComment(m) })
            .ToList();
        return new RenderResult(png, sel.Width, sel.Height, markers);
    }

    static void Finish() => CaptureController.CloseAll();

    static string PasteHint() =>
        SmartPaste.IsArmed
            ? L.T("Ctrl+V вставит и картинки, и текст — в чат, мессенджер или Word.")
            : L.T("Вставь в чат или документ.");

    async Task DoCopyAsync()
    {
        var r = RenderCurrent();
        if (r is null) return;
        AutoSaveIfEnabled(r);
        var series = AppServices.Series;

        try
        {
            // Идёт серия — текущий снимок становится её последним кадром, копируем всё сразу.
            if (series.Shots.Count > 0)
            {
                series.Add(r.Png, r.Width, r.Height, r.Markers);
                Finish();
                await series.CopyAllAsync();
                Toast.Show(L.F("Серия скопирована · {0}", series.Shots.Count), PasteHint());
                return;
            }

            if (r.Markers.Count > 0)
            {
                // Один скриншот с метками: картинка + комментарии, с умной вставкой.
                var path = TempFiles.Write(r.Png, "Screenshot_1.png");
                var shot = new SeriesShot { Number = 1, ImagePath = path, Width = r.Width, Height = r.Height, Markers = r.Markers };
                Finish();
                await SmartPaste.CopyAsync(SeriesService.BuildBundle(new[] { shot }, r.Png));
                Toast.Show(L.T("Скопировано"), PasteHint());
            }
            else
            {
                Finish();
                await SmartPaste.CopyPlainAsync(new ClipboardPayload { Png = r.Png, Dib = ImageEncoder.ToDib(r.Png) });
                Toast.Show(L.T("Скопировано"), L.T("Скриншот в буфере обмена."));
            }
        }
        catch (Exception ex)
        {
            Toast.Show(L.T("Не удалось скопировать"), ex.Message);
        }
    }

    async Task DoQuickSaveAsync()
    {
        var r = RenderCurrent();
        if (r is null) return;
        try
        {
            var path = SaveToFolder(r.Png);
            Finish();
            Toast.Show(L.T("Сохранено"), Path.GetFileName(path), L.T("Показать"), () => ShellOpen.ShowInFolder(path));
        }
        catch (Exception ex)
        {
            Toast.Show(L.T("Не удалось сохранить"), ex.Message);
        }
        await Task.CompletedTask;
    }

    async Task DoSaveAsAsync()
    {
        var r = RenderCurrent();
        if (r is null) return;
        var s = AppServices.Settings;
        var ext = ImageEncoder.Extension(s.ImageFormat);

        // Диалог иначе может оказаться под нашим «поверх всех» окном.
        Topmost = false;
        try
        {
            var options = new FilePickerSaveOptions
            {
                Title = L.T("Сохранить скриншот"),
                SuggestedFileName = FileNaming.Build(s.FileNameTemplate, DateTime.Now, AppServices.PeekShotNumber()) + "." + ext,
                DefaultExtension = ext,
                FileTypeChoices = FileTypes(s.ImageFormat),
            };
            try
            {
                Directory.CreateDirectory(s.SaveFolder);
                options.SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(s.SaveFolder);
            }
            catch { }

            var file = await StorageProvider.SaveFilePickerAsync(options);
            if (file is null)
            {
                Topmost = true;
                FocusSurface();
                return;
            }

            var localPath = file.TryGetLocalPath();
            var format = ImageEncoder.FromExtension(Path.GetExtension(localPath ?? file.Name)) ?? s.ImageFormat;
            var bytes = ImageEncoder.Encode(r.Png, format, s.ImageQuality);
            if (localPath is not null)
            {
                await File.WriteAllBytesAsync(localPath, bytes);
            }
            else
            {
                await using var stream = await file.OpenWriteAsync();
                stream.SetLength(0);
                await stream.WriteAsync(bytes);
            }

            AppServices.NextShotNumber();
            Finish();
            if (localPath is not null)
                Toast.Show(L.T("Сохранено"), Path.GetFileName(localPath), L.T("Показать"), () => ShellOpen.ShowInFolder(localPath));
        }
        catch (Exception ex)
        {
            Topmost = true;
            Toast.Show(L.T("Не удалось сохранить"), ex.Message);
        }
    }

    static IReadOnlyList<FilePickerFileType> FileTypes(ImageFileFormat preferred)
    {
        var all = new List<(ImageFileFormat f, FilePickerFileType t)>
        {
            (ImageFileFormat.Png, new FilePickerFileType("PNG") { Patterns = new[] { "*.png" }, MimeTypes = new[] { "image/png" } }),
            (ImageFileFormat.Jpg, new FilePickerFileType("JPEG") { Patterns = new[] { "*.jpg", "*.jpeg" }, MimeTypes = new[] { "image/jpeg" } }),
            (ImageFileFormat.WebP, new FilePickerFileType("WebP") { Patterns = new[] { "*.webp" }, MimeTypes = new[] { "image/webp" } }),
            (ImageFileFormat.Bmp, new FilePickerFileType("BMP") { Patterns = new[] { "*.bmp" }, MimeTypes = new[] { "image/bmp" } }),
        };
        return all.OrderBy(x => x.f == preferred ? 0 : 1).Select(x => x.t).ToList();
    }

    async Task DoPrintAsync()
    {
        var r = RenderCurrent();
        if (r is null) return;
        var path = TempFiles.Write(r.Png, "SnapMark_print.png");
        Finish();
        try { await PlatformServices.Print.PrintAsync(path); }
        catch (Exception ex) { Toast.Show(L.T("Печать недоступна"), ex.Message); }
    }

    async Task DoSearchAsync()
    {
        var r = RenderCurrent();
        if (r is null) return;
        Finish();
        try { await GoogleLens.SearchAsync(r.Png, AppServices.Settings.ImageSearch); }
        catch (Exception ex) { Toast.Show(L.T("Поиск не удался"), ex.Message); }
    }

    /// <summary>
    /// Текст с картинки: снимок области остаётся на месте экрана, распознанные слова можно выделять
    /// мышью и копировать, как в текстовом редакторе (по чистому снимку, без рисунков).
    /// </summary>
    async Task DoOcrAsync()
    {
        CommitTextEdit();
        if (_shot is null || _screen is null || Surface.Selection is not PixelRect sel || sel.Width < 4 || sel.Height < 4) return;
        byte[] png;
        using (var rtb = ImageRenderer.Render(_shot, sel, Array.Empty<Annotation>()))
            png = ImageEncoder.ToPng(rtb);
        var at = new PixelRect(_screen.Bounds.X + sel.X, _screen.Bounds.Y + sel.Y, sel.Width, sel.Height);
        Finish();
        await Task.Yield();
        OcrWindow.Open(png, at);
    }

    /// <summary>Закрепить скриншот поверх окон — на том же месте экрана, где он был выделен.</summary>
    void DoPin()
    {
        var r = RenderCurrent();
        if (r is null || _screen is null || Surface.Selection is not PixelRect sel) return;
        var at = new PixelRect(_screen.Bounds.X + sel.X, _screen.Bounds.Y + sel.Y, sel.Width, sel.Height);
        Finish();
        PinWindow.Pin(r.Png, at);
    }

    void DoAddToSeries()
    {
        var r = RenderCurrent();
        if (r is null) return;
        AutoSaveIfEnabled(r);
        var shot = AppServices.Series.Add(r.Png, r.Width, r.Height, r.Markers);
        Finish();
        Toast.Show(L.F("Добавлено в серию · {0}", shot.Number),
            L.T("Делай следующий скриншот. Ctrl+C в редакторе или «Копировать всё» на панели серии — скопировать всё."));
    }

    void StartRecording()
    {
        if (_recordRequested) return; // повторное нажатие / Enter, пока окна закрываются
        if (_screen is null || Surface.Selection is not PixelRect sel || sel.Width < 16 || sel.Height < 16)
        {
            Toast.Show(L.T("Запись видео"), L.T("Выдели область побольше (минимум 16×16)."));
            return;
        }
        var rect = new PixelRect(_screen.Bounds.X + sel.X, _screen.Bounds.Y + sel.Y, sel.Width, sel.Height);
        var scaling = _screen.Scaling;
        var mic = _withMic;
        var system = _withSystem;
        _recordRequested = true;
        Finish();
        // Даём оверлеям закрыться, чтобы они не попали в первый кадр.
        DispatcherTimer.RunOnce(() => RecordingController.Start(rect, scaling, mic, system), TimeSpan.FromMilliseconds(200));
    }

    // ═══════════════════════════ Файлы ═══════════════════════════
    static string SaveToFolder(byte[] png)
    {
        var s = AppServices.Settings;
        Directory.CreateDirectory(s.SaveFolder);
        var name = FileNaming.Build(s.FileNameTemplate, DateTime.Now, AppServices.NextShotNumber());
        var path = FileNaming.EnsureUnique(Path.Combine(s.SaveFolder, name + "." + ImageEncoder.Extension(s.ImageFormat)));
        File.WriteAllBytes(path, ImageEncoder.Encode(png, s.ImageFormat, s.ImageQuality));
        return path;
    }

    static void AutoSaveIfEnabled(RenderResult r)
    {
        if (!AppServices.Settings.AutoSaveCopy) return;
        try { SaveToFolder(r.Png); } catch { }
    }

    // ═══════════════════════════ Утилиты ═══════════════════════════
    static Color TryParseColor(string hex, Color fallback) => Color.TryParse(hex, out var c) ? c : fallback;

    static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
}
