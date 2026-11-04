using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using SnapMark.Annotations;
using SnapMark.Capture;
using SnapMark.Core;
using SnapMark.Platform;
using SnapMark.Services;
using SnapMark.UI;

namespace SnapMark.Recording;

/// <summary>
/// Слой «интерфейса» над холстом: рамка выделенного объекта. Окно исключено из захвата и пропускает мышь,
/// поэтому в видео видны только сами рисунки, а не служебные рамки.
/// </summary>
public sealed class LiveChromeWindow : Window
{
    sealed class FrameLayer : Control
    {
        readonly LiveDrawCanvas _board;
        public FrameLayer(LiveDrawCanvas board)
        {
            _board = board;
            IsHitTestVisible = false;
        }

        static readonly IPen Dark = new Pen(new SolidColorBrush(Color.FromRgb(20, 20, 22)), 1.5, lineCap: PenLineCap.Square);
        static readonly IPen Light = new Pen(Brushes.White, 3.5, lineCap: PenLineCap.Square);

        public override void Render(DrawingContext ctx)
        {
            if (_board.TargetBounds() is Rect r)
            {
                double s = _board.PixelScale;
                var v = new Rect(r.X / s, r.Y / s, r.Width / s, r.Height / s).Inflate(5);
                ctx.DrawRectangle(null, new Pen(new SolidColorBrush(Color.Parse("#0A84FF")), 1.25, new DashStyle(new double[] { 3, 2.5 }, 0)), v, 5, 5);
            }
            if (_board.HideSystemCursor && _board.IsDrawing && _board.PointerView is Point p) DrawCursor(ctx, p, _board.Tool);
        }

        /// <summary>«Свой» курсор: виден тебе, но не попадает в видео (слой исключён из захвата).</summary>
        static void DrawCursor(DrawingContext ctx, Point p, ToolKind tool)
        {
            double x = Math.Round(p.X) + 0.5, y = Math.Round(p.Y) + 0.5;
            void Line(Point a, Point b)
            {
                ctx.DrawLine(Light, a, b);
                ctx.DrawLine(Dark, a, b);
            }
            switch (tool)
            {
                case ToolKind.Text:
                    Line(new Point(x, y - 9), new Point(x, y + 9));
                    Line(new Point(x - 4, y - 9), new Point(x + 4, y - 9));
                    Line(new Point(x - 4, y + 9), new Point(x + 4, y + 9));
                    break;
                case ToolKind.Select:
                {
                    var geo = new StreamGeometry();
                    using (var g = geo.Open())
                    {
                        g.BeginFigure(new Point(x, y), true);
                        g.LineTo(new Point(x, y + 16));
                        g.LineTo(new Point(x + 4.2, y + 12.2));
                        g.LineTo(new Point(x + 7, y + 18));
                        g.LineTo(new Point(x + 9.4, y + 17));
                        g.LineTo(new Point(x + 6.8, y + 11.4));
                        g.LineTo(new Point(x + 12, y + 11.4));
                        g.EndFigure(true);
                    }
                    ctx.DrawGeometry(Brushes.White, new Pen(new SolidColorBrush(Color.FromRgb(20, 20, 22)), 1.2, lineJoin: PenLineJoin.Round), geo);
                    break;
                }
                case ToolKind.Eraser:
                    ctx.DrawEllipse(null, Light, new Point(x, y), 8, 8);
                    ctx.DrawEllipse(null, Dark, new Point(x, y), 8, 8);
                    break;
                default:
                    Line(new Point(x - 10, y), new Point(x - 3, y));
                    Line(new Point(x + 3, y), new Point(x + 10, y));
                    Line(new Point(x, y - 10), new Point(x, y - 3));
                    Line(new Point(x, y + 3), new Point(x, y + 10));
                    break;
            }
        }
    }

    readonly PixelRect _rect;
    readonly FrameLayer _layer;

    public LiveChromeWindow(PixelRect rect, double scaling, LiveDrawCanvas board)
    {
        _rect = rect;
        SystemDecorations = SystemDecorations.None;
        Topmost = true;
        ShowInTaskbar = false;
        CanResize = false;
        ShowActivated = false;
        IsHitTestVisible = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Background = Brushes.Transparent;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        Position = rect.Position;
        Width = rect.Width / scaling;
        Height = rect.Height / scaling;
        MinWidth = 0;
        MinHeight = 0;
        Title = "SnapMark";
        _layer = new FrameLayer(board);
        Content = _layer;
        board.Invalidated += () => _layer.InvalidateVisual();
        board.PointerTracked += () => _layer.InvalidateVisual();

        Opened += (_, _) =>
        {
            var wh = PlatformServices.WindowHelper;
            wh.SetPixelBounds(this, _rect);
            if (OperatingSystem.IsWindows())
            {
                // Windows: на «я прозрачен для мыши» (HTTRANSPARENT) система отдаёт мышь окну под слоем
                // из того же потока — холсту. Стиль WS_EX_LAYERED тут не годится: после него Windows
                // переставала исключать слой из записи, и «свой» курсор с рамкой выделения попадали в видео.
                Win32Properties.AddWndProcHookCallback(this, _hitTestHook);
                ClickThroughOk = true;
            }
            else ClickThroughOk = wh.SetClickThrough(this, true);
            wh.ExcludeFromCapture(this); // после всех изменений стиля окна
            Excluded = wh.IsExcludedFromCapture(this);
        };
    }

    const uint WM_NCHITTEST = 0x0084;
    static readonly IntPtr HTTRANSPARENT = new(-1);

    // Делегат храним в поле, чтобы сборщик мусора не убрал его, пока окно живо.
    readonly Win32Properties.CustomWndProcHookCallback _hitTestHook = (IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
    {
        if (msg != WM_NCHITTEST) return IntPtr.Zero;
        handled = true;
        return HTTRANSPARENT;
    };

    /// <summary>Мышь проходит сквозь слой. Если нет — слой нельзя держать над холстом.</summary>
    public bool ClickThroughOk { get; private set; }

    /// <summary>Слой действительно не попадает в видео — только тогда на нём можно рисовать «свой» курсор.</summary>
    public bool Excluded { get; private set; }
}

/// <summary>
/// Панель стиля рисования на видео — те же настройки, что в редакторе скриншота: для выделенного объекта
/// (или того, что сейчас печатается) и для новых объектов текущего инструмента. Окно исключено из захвата.
/// </summary>
public sealed class LiveStyleWindow : Window
{
    readonly LiveDrawCanvas _board;
    readonly StackPanel _content = new() { Spacing = 8 };
    bool _rebuildPending;

    public LiveStyleWindow(LiveDrawCanvas board)
    {
        _board = board;
        UiKit.MakeFloating(this);
        ShowActivated = false;
        Title = L.T("SnapMark — стиль");

        var card = new GlassPanel
        {
            Margin = new Thickness(12),
            Padding = new Thickness(14, 10),
            MaxWidth = 520,
            Child = _content,
        };
        card.Classes.Add("pill");
        Content = card;

        Opened += (_, _) => PlatformServices.WindowHelper.ExcludeFromCapture(this);
    }

    /// <summary>Есть что показать (для инструментов «Ластик» и «Выбор» без выделения панель не нужна).</summary>
    public bool HasContent => _content.Children.Count > 0;

    public event Action? Rebuilt;

    public void RequestRebuild()
    {
        if (_rebuildPending) return;
        _rebuildPending = true;
        // Откладываем: пересборка внутри обработчика контрола панели удалила бы сам этот контрол.
        Dispatcher.UIThread.Post(() =>
        {
            _rebuildPending = false;
            Build();
            Rebuilt?.Invoke();
        });
    }

    void Build()
    {
        _content.Children.Clear();
        var target = _board.StyleTarget;
        if (target is not null)
        {
            _content.Children.Add(Group(target is TextAnnotation && !_board.Document.Items.Contains(target)
                ? L.T("ТЕКСТ, КОТОРЫЙ ПЕЧАТАЕШЬ")
                : L.T("ВЫДЕЛЕННЫЙ ОБЪЕКТ")));
            switch (target)
            {
                case TextAnnotation t: BuildText(t); break;
                case MarkerAnnotation m: BuildMarker(m); break;
                default: BuildShape(target); break;
            }
            return;
        }

        var tool = _board.Tool;
        if (tool is ToolKind.Eraser or ToolKind.Select) return;
        _content.Children.Add(Group(L.T("ДЛЯ НОВЫХ ОБЪЕКТОВ")));
        switch (tool)
        {
            case ToolKind.Text: BuildTextDefaults(); break;
            case ToolKind.Marker or ToolKind.MarkerArrow: BuildMarkerDefaults(); break;
            default: BuildShapeDefaults(tool); break;
        }
    }

    static TextBlock Group(string text)
    {
        var t = new TextBlock { Text = text, Margin = new Thickness(0) };
        t.Classes.Add("group");
        return t;
    }

    void Add(Control c) => _content.Children.Add(c);

    // ── Выделенный объект (как контекстная панель редактора) ───────────
    void Mutate(Action<Annotation> change) => _board.MutateTarget(change);

    void BuildText(TextAnnotation t)
    {
        Add(OverlayWindow.Row(OverlayWindow.Label(L.T("Цвет")), OverlayWindow.ColorRow(OverlayWindow.ContextPalette, t.Color,
            c => Mutate(a => a.Color = Annotation.WithOpacity(c, a.Color.A / 255.0)))));
        Add(OverlayWindow.Row(
            OverlayWindow.FontPicker(t.FontFamily, name => Mutate(a => ((TextAnnotation)a).FontFamily = name)),
            OverlayWindow.WeightPicker(t.FontWeight, w => Mutate(a => ((TextAnnotation)a).FontWeight = w)),
            OverlayWindow.Chip("I", t.Italic, v => Mutate(a => ((TextAnnotation)a).Italic = v), italic: true, tip: L.T("Курсив")),
            OverlayWindow.Stepper(t.FontSize, 8, 160, 2, v => Mutate(a => ((TextAnnotation)a).FontSize = v), L.T("Размер текста"))));
        Add(OverlayWindow.Row(
            OverlayWindow.FrameChips(t.Frame, f => Mutate(a => ((TextAnnotation)a).Frame = f)),
            OverlayWindow.Spacer(),
            ObjectActions()));
        foreach (var row in OverlayWindow.FrameColorRows(t.Frame, t.BorderColor, t.PlateColor,
                     c => Mutate(a => ((TextAnnotation)a).BorderColor = c),
                     c => Mutate(a => ((TextAnnotation)a).PlateColor = c),
                     L.T("Цвет текста")))
            Add(row);
        Add(OverlayWindow.ShadowRow(t.Shadow, t.ShadowColor,
            on => Mutate(a => ((TextAnnotation)a).Shadow = on),
            c => Mutate(a =>
            {
                var ta = (TextAnnotation)a;
                ta.Shadow = true;
                ta.ShadowColor = c;
            })));
    }

    void BuildMarker(MarkerAnnotation m)
    {
        Add(OverlayWindow.Row(OverlayWindow.Label(L.T("Фон")), OverlayWindow.ColorRow(OverlayWindow.ContextPalette, m.Color, c => Mutate(a => a.Color = c))));
        var auto = OverlayWindow.Chip(L.T("Авто"), m.LabelColor is null, _ => Mutate(a => ((MarkerAnnotation)a).LabelColor = null),
            tip: L.T("Белый на тёмном, тёмный на светлом"));
        Add(OverlayWindow.Row(OverlayWindow.Label(m.Label.All(char.IsDigit) ? L.T("Цифра") : L.T("Буква")), auto,
            OverlayWindow.ColorRow(OverlayWindow.LabelPalette, m.LabelColor, c => Mutate(a => ((MarkerAnnotation)a).LabelColor = c))));
        Add(NumberingRow());
        Add(OverlayWindow.Row(
            ShapeButtons(m.Shape, shape => Mutate(a => ((MarkerAnnotation)a).Shape = shape)),
            OverlayWindow.Stepper(m.FontSize, 12, 120, 2, v => Mutate(a => ((MarkerAnnotation)a).FontSize = v), L.T("Размер метки")),
            OverlayWindow.Spacer(),
            ObjectActions()));

        // Стиль подписи-комментария именно этой метки.
        Add(Separator());
        Add(OverlayWindow.Row(OverlayWindow.Label(L.T("Подпись")),
            OverlayWindow.FontPicker(m.CommentFont, name => Mutate(a => ((MarkerAnnotation)a).CommentFont = name)),
            OverlayWindow.WeightPicker(m.CommentWeight, w => Mutate(a => ((MarkerAnnotation)a).CommentWeight = w)),
            OverlayWindow.Chip("I", m.CommentItalic, v => Mutate(a => ((MarkerAnnotation)a).CommentItalic = v), italic: true, tip: L.T("Курсив")),
            OverlayWindow.Stepper(m.CommentSize, 8, 96, 2, v => Mutate(a => ((MarkerAnnotation)a).CommentSize = v), L.T("Размер подписи"))));
        Add(OverlayWindow.Row(OverlayWindow.Label(L.T("Цвет")),
            OverlayWindow.ColorRow(OverlayWindow.ContextPalette, m.CommentColor, c => Mutate(a => ((MarkerAnnotation)a).CommentColor = c))));
        Add(OverlayWindow.Row(OverlayWindow.Label(L.T("Вид")),
            OverlayWindow.FrameChips(m.CommentFrame, f => Mutate(a => ((MarkerAnnotation)a).CommentFrame = f))));
        foreach (var row in OverlayWindow.FrameColorRows(m.CommentFrame, m.CommentBorderColor, m.CommentPlateColor,
                     c => Mutate(a => ((MarkerAnnotation)a).CommentBorderColor = c),
                     c => Mutate(a => ((MarkerAnnotation)a).CommentPlateColor = c),
                     L.T("Цвет метки")))
            Add(row);
        Add(OverlayWindow.ShadowRow(m.CommentShadow, m.CommentShadowColor,
            on => Mutate(a => ((MarkerAnnotation)a).CommentShadow = on),
            c => Mutate(a =>
            {
                var mk = (MarkerAnnotation)a;
                mk.CommentShadow = true;
                mk.CommentShadowColor = c;
            })));
    }

    void BuildShape(Annotation target)
    {
        Add(OverlayWindow.Row(OverlayWindow.ColorRow(OverlayWindow.ContextPalette, target.Color,
            c => Mutate(a => a.Color = Annotation.WithOpacity(c, a.Color.A / 255.0)))));
        var controls = new List<Control>
        {
            OverlayWindow.Stepper(target.Thickness, 1, 40, 1, v => Mutate(a => a.Thickness = v), L.T("Толщина")),
            OverlayWindow.Chip(L.T("Пунктир"), target.Dashed, v => Mutate(a => a.Dashed = v)),
        };
        if (target is BoxAnnotation box)
            controls.Add(OverlayWindow.Chip(L.T("Заливка"), box.Filled, v => Mutate(a => ((BoxAnnotation)a).Filled = v)));
        controls.Add(OverlayWindow.Spacer());
        controls.Add(ObjectActions());
        Add(OverlayWindow.Row(controls.ToArray()));
    }

    Control ObjectActions()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        // Дублировать и удалить можно то, что уже нарисовано (не текст, который ещё печатается).
        if (_board.SelectedAnnotation is null) return panel;
        panel.Children.Add(UiKit.ToolButton("Icon.Duplicate", L.T("Дублировать (Ctrl+J)"), () => _board.DuplicateSelected(), "small"));
        panel.Children.Add(UiKit.ToolButton("Icon.Trash", L.T("Удалить (Delete или правый клик по объекту)"), () => _board.DeleteSelected(), "small", "danger"));
        return panel;
    }

    // ── Значения по умолчанию для новых объектов ──────────────────────
    AnnotationStyle St => _board.DrawStyle;

    void Defaults(Action change)
    {
        change();
        RequestRebuild();
        _board.RefocusInput();
    }

    void BuildShapeDefaults(ToolKind tool)
    {
        Add(OverlayWindow.Row(OverlayWindow.ColorRow(OverlayWindow.ContextPalette, St.Color, c => Defaults(() => St.Color = c))));
        var controls = new List<Control>
        {
            OverlayWindow.Stepper(St.Thickness, 1, 40, 1, v => Defaults(() => St.Thickness = v), L.T("Толщина")),
            OverlayWindow.Stepper(St.Opacity * 100, 10, 100, 10, v => Defaults(() => St.Opacity = v / 100.0), L.T("Прозрачность")),
            OverlayWindow.Chip(L.T("Пунктир"), St.Dashed, v => Defaults(() => St.Dashed = v)),
        };
        if (tool is ToolKind.Rectangle or ToolKind.Ellipse)
            controls.Add(OverlayWindow.Chip(L.T("Заливка"), St.Filled, v => Defaults(() => St.Filled = v)));
        Add(OverlayWindow.Row(controls.ToArray()));
    }

    void BuildTextDefaults()
    {
        Add(OverlayWindow.Row(OverlayWindow.Label(L.T("Цвет")), OverlayWindow.ColorRow(OverlayWindow.ContextPalette, St.Color, c => Defaults(() => St.Color = c))));
        Add(OverlayWindow.Row(
            OverlayWindow.FontPicker(St.FontFamily, name => Defaults(() => St.FontFamily = name)),
            OverlayWindow.WeightPicker(St.FontWeight, w => Defaults(() => St.FontWeight = w)),
            OverlayWindow.Chip("I", St.Italic, v => Defaults(() => St.Italic = v), italic: true, tip: L.T("Курсив")),
            OverlayWindow.Stepper(St.FontSize, 8, 160, 2, v => Defaults(() => St.FontSize = v), L.T("Размер текста"))));
        Add(OverlayWindow.FrameChips(St.TextFrame, f => Defaults(() => St.TextFrame = f)));
        foreach (var row in OverlayWindow.FrameColorRows(St.TextFrame, St.BorderColor, St.PlateColor,
                     c => Defaults(() => St.BorderColor = c), c => Defaults(() => St.PlateColor = c), L.T("Цвет текста")))
            Add(row);
        Add(OverlayWindow.ShadowRow(St.TextShadow, St.ShadowColor,
            on => Defaults(() => St.TextShadow = on),
            c => Defaults(() =>
            {
                St.TextShadow = true;
                St.ShadowColor = c;
            })));
    }

    void BuildMarkerDefaults()
    {
        var cs = _board.CommentStyle;
        Add(OverlayWindow.Row(OverlayWindow.Label(L.T("Фон")), OverlayWindow.ColorRow(OverlayWindow.ContextPalette, St.Color, c => Defaults(() => St.Color = c))));
        Add(NumberingRow());
        Add(OverlayWindow.Row(
            ShapeButtons(St.MarkerShape, shape => Defaults(() => St.MarkerShape = shape)),
            OverlayWindow.Stepper(St.FontSize, 12, 120, 2, v => Defaults(() => St.FontSize = v), L.T("Размер метки"))));
        Add(Separator());
        Add(OverlayWindow.Row(OverlayWindow.Label(L.T("Подпись")),
            OverlayWindow.FontPicker(cs.FontFamily, name => Defaults(() => cs.FontFamily = name)),
            OverlayWindow.WeightPicker(cs.FontWeight, w => Defaults(() => cs.FontWeight = w)),
            OverlayWindow.Chip("I", cs.Italic, v => Defaults(() => cs.Italic = v), italic: true, tip: L.T("Курсив")),
            OverlayWindow.Stepper(cs.Size, 8, 96, 2, v => Defaults(() => cs.Size = v), L.T("Размер подписи"))));
        Add(OverlayWindow.Row(OverlayWindow.Label(L.T("Цвет")),
            OverlayWindow.ColorRow(OverlayWindow.ContextPalette, cs.Color, c => Defaults(() => cs.Color = c))));
        Add(OverlayWindow.Row(OverlayWindow.Label(L.T("Вид")), OverlayWindow.FrameChips(cs.Frame, f => Defaults(() => cs.Frame = f))));
        foreach (var row in OverlayWindow.FrameColorRows(cs.Frame, cs.BorderColor, cs.PlateColor,
                     c => Defaults(() => cs.BorderColor = c), c => Defaults(() => cs.PlateColor = c), L.T("Цвет метки")))
            Add(row);
        Add(OverlayWindow.ShadowRow(cs.Shadow, cs.ShadowColor,
            on => Defaults(() => cs.Shadow = on),
            c => Defaults(() =>
            {
                cs.Shadow = true;
                cs.ShadowColor = c;
            })));
    }

    /// <summary>A B C / 1 2 3 — меняется сразу у всех меток, запоминается для следующих записей и скриншотов.</summary>
    Control NumberingRow()
    {
        var current = _board.Document.Numbering;
        void Pick(MarkerNumbering n)
        {
            _board.SetNumbering(n);
            AppServices.Settings.MarkerNumbering = n;
            AppServices.SaveSettings();
            RequestRebuild();
            _board.RefocusInput();
        }
        var letters = OverlayWindow.Chip("A B C", current == MarkerNumbering.Letters, _ => Pick(MarkerNumbering.Letters),
            tip: L.T("Буквы — у всех меток сразу"));
        var numbers = OverlayWindow.Chip("1 2 3", current == MarkerNumbering.Numbers, _ => Pick(MarkerNumbering.Numbers),
            tip: L.T("Цифры — у всех меток сразу"));
        return OverlayWindow.Row(OverlayWindow.Label(L.T("Метки")), letters, numbers);
    }

    static StackPanel ShapeButtons(MarkerShape current, Action<MarkerShape> pick)
    {
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
            var b = UiKit.ToolButton(icon, tip, () => pick(s), "small");
            b.Classes.Set("selected", current == shape);
            shapes.Children.Add(b);
        }
        return shapes;
    }

    static Control Separator()
    {
        var b = new Border();
        b.Classes.Add("hsep");
        b.Classes.Add("tight");
        return b;
    }
}
