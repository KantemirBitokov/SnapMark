using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using SnapMark.Annotations;
using SnapMark.Core;
using SnapMark.Platform;
using SnapMark.Services;

namespace SnapMark.Recording;

/// <summary>
/// Холст рисования поверх области записи. Окно холста НЕ исключено из захвата, поэтому всё, что на нём
/// появляется — линии, фигуры, метки, набираемый текст, — попадает в видео прямо в момент рисования.
/// Координаты аннотаций — в физических пикселях области (как в редакторе скриншота).
/// </summary>
public sealed class LiveDrawCanvas : Canvas
{
    static readonly TimeSpan SnapDelay = TimeSpan.FromMilliseconds(600);

    readonly CommentLabelStyle _commentStyle = new() { Enabled = true };
    readonly DispatcherTimer _snapTimer;

    Annotation? _drawing;
    List<Annotation>? _snapshot;
    Point _pressImg, _lastImg, _snapAnchor;
    ulong _lastTimestamp;
    double _inkWidth = 1;
    bool _erasing, _erasedAny, _snapped;

    // Набор текста: невидимый TextBox принимает клавиатуру, а текст рисуется самим холстом — так, как он
    // будет выглядеть в итоге, и его видно в видео по мере набора.
    TextBox? _input;
    TextAnnotation? _editText;
    bool _editTextIsNew;
    MarkerAnnotation? _editMarker;
    string _editMarkerBefore = "";

    public LiveDrawCanvas()
    {
        Focusable = true;
        ClipToBounds = true;
        _snapTimer = new DispatcherTimer { Interval = SnapDelay };
        _snapTimer.Tick += (_, _) => TrySnapShape();
    }

    public AnnotationDocument Document { get; } = new();
    public AnnotationStyle DrawStyle { get; } = new();
    public ToolKind Tool { get; set; } = ToolKind.Pen;

    /// <summary>Физических пикселей в одной логической точке окна.</summary>
    public double PixelScale { get; set; } = 1;

    /// <summary>Режим рисования: холст принимает мышь. Иначе мышь уходит приложениям под ним.</summary>
    public bool IsDrawing
    {
        get => Background is not null;
        set
        {
            if (!value) CommitTextInput();
            // Прозрачный (не null) фон нужен, чтобы холст ловил нажатия по пустым местам.
            Background = value ? Brushes.Transparent : null;
            Cursor = value ? new Cursor(Tool == ToolKind.Text ? StandardCursorType.Ibeam : StandardCursorType.Cross) : null;
        }
    }

    public bool HasContent => Document.Items.Count > 0 || _editText is not null;

    /// <summary>Изменилось содержимое (для кнопок «Отменить» / «Очистить» на панели).</summary>
    public event Action? ContentChanged;
    /// <summary>Esc на холсте — вернуться к курсору.</summary>
    public event Action? CursorRequested;
    /// <summary>Горячая клавиша инструмента на холсте.</summary>
    public event Action<ToolKind>? ToolRequested;

    Point ToImage(Point view) => new(view.X * PixelScale, view.Y * PixelScale);

    // ── Команды ───────────────────────────────────────────────────────
    public void Undo()
    {
        CommitTextInput();
        if (Document.Undo()) Changed();
    }

    public void Redo()
    {
        CommitTextInput();
        if (Document.Redo()) Changed();
    }

    public void Clear()
    {
        CommitTextInput();
        if (Document.Items.Count == 0) return;
        var snap = Document.Snapshot();
        Document.Items.Clear();
        Document.PushUndo(snap);
        Changed();
    }

    void Changed()
    {
        ContentChanged?.Invoke();
        InvalidateVisual();
    }

    // ── Отрисовка ─────────────────────────────────────────────────────
    public override void Render(DrawingContext ctx)
    {
        base.Render(ctx); // прозрачный фон
        var s = PixelScale;
        using (ctx.PushTransform(Matrix.CreateScale(1 / s, 1 / s)))
        {
            foreach (var a in Document.Items)
                if (!ReferenceEquals(a, _editText)) a.Render(ctx);

            if (_editText is not null)
            {
                // Набираемый текст с «кареткой» в конце.
                var preview = (TextAnnotation)_editText.Clone();
                preview.Text = (_input?.Text ?? "") + "|";
                preview.Render(ctx);
            }

            CommentLabels.Render(ctx, Document, _commentStyle, new Rect(0, 0, Bounds.Width * s, Bounds.Height * s));
        }
    }

    // ── Мышь ──────────────────────────────────────────────────────────
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!IsDrawing) return;
        var pt = e.GetCurrentPoint(this);
        var img = ToImage(pt.Position);
        e.Handled = true;

        if (_input is not null && !ReferenceEquals(e.Source, _input)) CommitTextInput();

        if (pt.Properties.IsRightButtonPressed)
        {
            // ПКМ по объекту — удалить его, как в редакторе.
            if (Hit(img, 7) is { } target) Remove(target);
            return;
        }
        if (!pt.Properties.IsLeftButtonPressed) return;

        Focus();
        _pressImg = _lastImg = _snapAnchor = img;
        _lastTimestamp = e.Timestamp;

        switch (Tool)
        {
            case ToolKind.Text:
                if (Hit(img) is TextAnnotation existing) BeginTextInput(existing, false);
                else
                {
                    var ta = new TextAnnotation { Position = new Point(img.X, img.Y - DrawStyle.FontSize * 0.6) };
                    ta.ApplyStyle(DrawStyle);
                    BeginTextInput(ta, true);
                }
                return;

            case ToolKind.Eraser:
                _snapshot = Document.Snapshot();
                _erasing = true;
                _erasedAny = false;
                e.Pointer.Capture(this);
                EraseAt(img);
                return;

            case ToolKind.Marker:
            case ToolKind.MarkerArrow:
            {
                // Нажми — метка; потяни — метка со стрелкой к цели. Потом сразу можно набрать комментарий.
                _snapshot = Document.Snapshot();
                var m = Document.NewMarker();
                m.Center = img;
                m.Tail = img;
                m.ApplyStyle(DrawStyle);
                m.ApplyCommentStyle(_commentStyle);
                _drawing = m;
                Document.Items.Add(m);
                e.Pointer.Capture(this);
                InvalidateVisual();
                return;
            }

            default:
            {
                _snapshot = Document.Snapshot();
                _snapped = false;
                _inkWidth = 1;
                _drawing = CreateShape(Tool, img);
                Document.Items.Add(_drawing);
                e.Pointer.Capture(this);
                if (_drawing is FreehandAnnotation) _snapTimer.Start();
                InvalidateVisual();
                return;
            }
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_drawing is null && !_erasing) return;
        var pt = e.GetCurrentPoint(this);
        var img = ToImage(pt.Position);
        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        if (_erasing) EraseAt(img);
        else
        {
            switch (_drawing)
            {
                case FreehandAnnotation f:
                    f.AddPoint(img, f.Kind == StrokeKind.Ink ? InkFactor(e, pt, Annotation.Distance(img, _lastImg)) : 1);
                    if (!_snapped && Annotation.Distance(img, _snapAnchor) > 3 * PixelScale)
                    {
                        _snapAnchor = img;
                        _snapTimer.Stop();
                        _snapTimer.Start();
                    }
                    break;
                case LineAnnotation l:
                    l.End = shift ? Snap45(l.Start, img) : img;
                    break;
                case BoxAnnotation b:
                    b.End = shift ? SnapSquare(b.Start, img) : img;
                    break;
                case MarkerAnnotation m:
                    m.Tail = shift ? Snap45(m.Center, img) : img;
                    break;
            }
        }
        _lastImg = img;
        _lastTimestamp = e.Timestamp;
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        e.Pointer.Capture(null);
        Finish();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        Finish();
    }

    void Finish()
    {
        _snapTimer.Stop();
        if (_erasing)
        {
            _erasing = false;
            if (_erasedAny && _snapshot is not null) Document.PushUndo(_snapshot);
            _snapshot = null;
            Changed();
            return;
        }
        if (_drawing is null) return;
        var d = _drawing;
        _drawing = null;

        if (d is MarkerAnnotation m)
        {
            if (m.Tail is Point t && Annotation.Distance(t, m.Center) < m.Radius * 1.3) m.Tail = null;
            if (_snapshot is not null) Document.PushUndo(_snapshot);
            _snapshot = null;
            Changed();
            BeginCommentInput(m);
            return;
        }

        if (d.IsDegenerate) Document.Items.Remove(d);
        else if (_snapshot is not null) Document.PushUndo(_snapshot);
        _snapshot = null;
        Changed();
    }

    Annotation CreateShape(ToolKind tool, Point p)
    {
        Annotation a = tool switch
        {
            ToolKind.Pencil => new FreehandAnnotation { Kind = StrokeKind.Pencil },
            ToolKind.Highlighter => new FreehandAnnotation { Kind = StrokeKind.Highlighter },
            ToolKind.Line => new LineAnnotation { Start = p, End = p },
            ToolKind.Arrow => new LineAnnotation { Start = p, End = p, ArrowEnd = true },
            ToolKind.DoubleArrow => new LineAnnotation { Start = p, End = p, ArrowStart = true, ArrowEnd = true },
            ToolKind.Rectangle => new RectAnnotation { Start = p, End = p },
            ToolKind.Ellipse => new EllipseAnnotation { Start = p, End = p },
            _ => new FreehandAnnotation { Kind = StrokeKind.Ink },
        };
        a.ApplyStyle(DrawStyle);
        if (a is FreehandAnnotation f) f.AddPoint(p, 1);
        return a;
    }

    /// <summary>Перо мышью: быстрее — тоньше; стилусом — по нажиму (как в редакторе).</summary>
    double InkFactor(PointerEventArgs e, PointerPoint pt, double distanceImg)
    {
        if (e.Pointer.Type == PointerType.Pen && pt.Properties.Pressure > 0)
            return 0.3 + pt.Properties.Pressure * 1.2;
        ulong dt = e.Timestamp > _lastTimestamp ? e.Timestamp - _lastTimestamp : 16;
        double speed = distanceImg / PixelScale / Math.Max(1, dt);
        double target = Math.Clamp(1.25 - speed * 0.45, 0.5, 1.25);
        _inkWidth = _inkWidth * 0.75 + target * 0.25;
        return _inkWidth;
    }

    /// <summary>Задержал перо в конце линии — каракуля превращается в ровную фигуру.</summary>
    void TrySnapShape()
    {
        _snapTimer.Stop();
        if (_snapped || _drawing is not FreehandAnnotation stroke) return;
        var shape = ShapeRecognizer.Recognize(stroke);
        if (shape is null) return;
        int index = Document.Items.IndexOf(stroke);
        if (index < 0) return;
        Document.Items[index] = shape;
        _drawing = shape;
        _snapped = true;
        InvalidateVisual();
    }

    Annotation? Hit(Point img, double tolerancePx = 5)
    {
        var tol = tolerancePx * PixelScale;
        for (int i = Document.Items.Count - 1; i >= 0; i--)
            if (Document.Items[i].HitTest(img, tol)) return Document.Items[i];
        return null;
    }

    void EraseAt(Point img)
    {
        if (Hit(img, 8) is not { } hit) return;
        Document.Items.Remove(hit);
        if (hit is MarkerAnnotation) Document.RelabelMarkers();
        _erasedAny = true;
        InvalidateVisual();
    }

    void Remove(Annotation a)
    {
        var snap = Document.Snapshot();
        Document.Items.Remove(a);
        if (a is MarkerAnnotation) Document.RelabelMarkers();
        Document.PushUndo(snap);
        Changed();
    }

    static Point Snap45(Point from, Point to)
    {
        double dx = to.X - from.X, dy = to.Y - from.Y;
        double len = Math.Sqrt(dx * dx + dy * dy);
        double angle = Math.Round(Math.Atan2(dy, dx) / (Math.PI / 4)) * (Math.PI / 4);
        return new Point(from.X + Math.Cos(angle) * len, from.Y + Math.Sin(angle) * len);
    }

    static Point SnapSquare(Point from, Point to)
    {
        double dx = to.X - from.X, dy = to.Y - from.Y;
        double size = Math.Max(Math.Abs(dx), Math.Abs(dy));
        return new Point(from.X + Math.Sign(dx == 0 ? 1 : dx) * size, from.Y + Math.Sign(dy == 0 ? 1 : dy) * size);
    }

    // ── Клавиатура: текст и комментарии к меткам ──────────────────────
    void BeginTextInput(TextAnnotation ta, bool isNew)
    {
        CommitTextInput();
        _editText = ta;
        _editTextIsNew = isNew;
        OpenInput(ta.Text, ta.Position);
    }

    void BeginCommentInput(MarkerAnnotation m)
    {
        CommitTextInput();
        _editMarker = m;
        _editMarkerBefore = Document.GetComment(m);
        OpenInput(_editMarkerBefore, m.Center);
        UpdateMarkerComment();
    }

    void OpenInput(string text, Point imgPos)
    {
        var tb = new TextBox
        {
            Text = text,
            AcceptsReturn = true,
            Width = 2,
            Height = 2,
            MinWidth = 0,
            MinHeight = 0,
            Opacity = 0, // ввод невидимый — текст рисует холст
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
        };
        // Около места набора — туда же система поставит окно подсказок ввода (IME).
        SetLeft(tb, imgPos.X / PixelScale);
        SetTop(tb, imgPos.Y / PixelScale);
        tb.AddHandler(KeyDownEvent, OnInputKeyDown, RoutingStrategies.Tunnel);
        tb.PropertyChanged += (_, e) =>
        {
            if (e.Property != TextBox.TextProperty) return;
            if (_editMarker is not null) UpdateMarkerComment();
            InvalidateVisual();
        };
        tb.LostFocus += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            if (ReferenceEquals(_input, tb)) CommitTextInput();
        });
        Children.Add(tb);
        _input = tb;
        // Без активного окна клавиатура ушла бы программе под областью.
        if (TopLevel.GetTopLevel(this) is Window window) PlatformServices.WindowHelper.ForceForeground(window);
        InvalidateVisual();
        Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(_input, tb)) return;
            tb.Focus();
            tb.CaretIndex = tb.Text?.Length ?? 0;
        });
    }

    void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        // Enter — готово, Shift+Enter — новая строка, Esc — готово.
        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        if (e.Key == Key.Escape || (e.Key == Key.Enter && !shift))
        {
            CommitTextInput();
            Focus();
            e.Handled = true;
        }
    }

    void UpdateMarkerComment()
    {
        if (_editMarker is null) return;
        // Пока печатаем — с «кареткой», чтобы в видео было видно, что идёт набор.
        Document.Comments[_editMarker.Id] = (_input?.Text ?? "") + "|";
    }

    public void CommitTextInput()
    {
        if (_input is null) return;
        var tb = _input;
        _input = null;
        Children.Remove(tb);
        var text = (tb.Text ?? "").TrimEnd();

        if (_editText is { } ta)
        {
            _editText = null;
            if (_editTextIsNew)
            {
                if (text.Length > 0)
                {
                    var snap = Document.Snapshot();
                    ta.Text = text;
                    Document.Items.Add(ta);
                    Document.PushUndo(snap);
                }
            }
            else if (text != ta.Text)
            {
                var snap = Document.Snapshot();
                if (text.Length == 0) Document.Items.Remove(ta);
                else ta.Text = text;
                Document.PushUndo(snap);
            }
        }

        if (_editMarker is { } m)
        {
            _editMarker = null;
            if (text.Length > 0) Document.Comments[m.Id] = text;
            else Document.Comments.Remove(m.Id);
        }
        Changed();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || _input is not null) return;
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        if (ctrl && e.Key == Key.Z) { if (shift) Redo(); else Undo(); e.Handled = true; return; }
        if (ctrl && e.Key == Key.Y) { Redo(); e.Handled = true; return; }
        if (ctrl || e.KeyModifiers.HasFlag(KeyModifiers.Alt)) return;

        ToolKind? tool = e.Key switch
        {
            Key.P => ToolKind.Pen,
            Key.H => ToolKind.Highlighter,
            Key.A => ToolKind.Arrow,
            Key.L => ToolKind.Line,
            Key.R => ToolKind.Rectangle,
            Key.O => ToolKind.Ellipse,
            Key.M => ToolKind.Marker,
            Key.T => ToolKind.Text,
            Key.E => ToolKind.Eraser,
            _ => null,
        };
        if (tool is { } t) { ToolRequested?.Invoke(t); e.Handled = true; }
        else if (e.Key is Key.Escape or Key.V) { CursorRequested?.Invoke(); e.Handled = true; }
    }
}

/// <summary>Прозрачное окно ровно поверх области записи с холстом рисования.</summary>
public sealed class LiveDrawWindow : Window
{
    readonly PixelRect _rect;

    public LiveDrawWindow(PixelRect rect, double scaling)
    {
        _rect = rect;
        SystemDecorations = SystemDecorations.None;
        Topmost = true;
        ShowInTaskbar = false;
        CanResize = false;
        ShowActivated = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Background = Brushes.Transparent;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        Position = rect.Position;
        Width = rect.Width / scaling;
        Height = rect.Height / scaling;
        MinWidth = 0;
        MinHeight = 0;
        Title = L.T("SnapMark — рисование на видео");
        FontFamily = UiFonts.Family;

        Board = new LiveDrawCanvas { PixelScale = scaling };
        var s = AppServices.Settings;
        if (Color.TryParse(s.LastColor, out var c)) Board.DrawStyle.Color = c;
        if (s.LastThickness > 0) Board.DrawStyle.Thickness = s.LastThickness;
        if (s.LastFontSize > 0) Board.DrawStyle.FontSize = s.LastFontSize;
        Content = Board;

        Opened += (_, _) =>
        {
            PlatformServices.WindowHelper.SetPixelBounds(this, _rect);
            if (RenderScaling > 0) Board.PixelScale = RenderScaling;
        };
    }

    public LiveDrawCanvas Board { get; }

    /// <summary>Окно реально прозрачное (на Linux без композитора оно было бы чёрным и закрыло бы область).</summary>
    public bool IsTransparent => ActualTransparencyLevel != WindowTransparencyLevel.None;
}
