using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using SnapMark.Annotations;
using SnapMark.Core;

namespace SnapMark.Capture;

public enum CaptureMode { Region, FullScreen, VideoRegion }

/// <summary>
/// Полноэкранная поверхность: замороженный снимок экрана, затемнение, рамка выделения, рисование.
/// Все данные хранятся в пикселях снимка; отображение масштабируется под окно.
///
/// Рамка — один тонкий прямоугольник выбранного цвета. Маркеров-точек нет: размер меняется
/// перетаскиванием за край или угол (курсор подсказывает), размер в пикселях виден только во время перетаскивания.
/// </summary>
public sealed class CaptureSurface : Control
{
    enum Drag { None, Selecting, MovingSelection, Resizing, Drawing, MovingAnnotation, Erasing }

    static readonly IBrush DimBrush = new SolidColorBrush(Color.FromArgb(120, 0, 0, 0));
    static readonly IBrush LabelBg = new SolidColorBrush(Color.FromArgb(200, 22, 22, 24));
    const double EdgeHit = 7;
    const double CornerHit = 12;
    static readonly TimeSpan SnapDelay = TimeSpan.FromMilliseconds(600);

    Drag _drag;
    Point _pressImg, _lastImg, _snapAnchor;
    PixelRect _origSelection;
    int _handle = -1;
    Annotation? _drawing;
    List<Annotation>? _snapshot;
    bool _moved, _snapped, _erasedAny;
    ToolKind _tool = ToolKind.Select;
    ulong _lastTimestamp;
    double _inkWidth = 1;
    readonly DispatcherTimer _snapTimer;
    IBrush _frameBrush = new SolidColorBrush(Color.Parse("#0A84FF"));

    Cursor? _cross, _arrow, _hand, _sizeAll, _ibeam, _eraser;
    readonly Cursor?[] _handleCursors = new Cursor?[8];

    public CaptureSurface()
    {
        Focusable = true;
        ClipToBounds = true;
        _snapTimer = new DispatcherTimer { Interval = SnapDelay };
        _snapTimer.Tick += (_, _) => TrySnapShape();
    }

    // ── Публичное состояние ───────────────────────────────────────────
    public Bitmap? Screenshot { get; set; }
    public CaptureMode Mode { get; set; }
    public AnnotationStyle DrawStyle { get; } = new();
    public AnnotationDocument Document { get; } = new();
    /// <summary>Подписи-комментарии на картинке (если включены).</summary>
    public CommentLabelStyle CommentStyle { get; } = new();
    public PixelRect? Selection { get; private set; }
    public Annotation? SelectedAnnotation { get; private set; }
    /// <summary>Текст, который сейчас редактируется в TextBox поверх — его не рисуем, чтобы не двоилось.</summary>
    public TextAnnotation? EditingText { get; set; }

    public Color FrameColor
    {
        set
        {
            _frameBrush = new SolidColorBrush(value);
            InvalidateVisual();
        }
    }

    public bool IsAdjustingSelection => _drag is Drag.Selecting or Drag.MovingSelection or Drag.Resizing;
    public bool IsInteracting => _drag != Drag.None;

    public ToolKind Tool
    {
        get => _tool;
        set
        {
            _tool = value;
            if (value != ToolKind.Select) SetSelected(null);
        }
    }

    public event Action? SelectionChanged;
    public event Action? AnnotationsChanged;
    public event Action? SelectedAnnotationChanged;
    public event Action? InteractionChanged;
    public event Action<TextAnnotation, bool>? TextEditRequested;
    public event Action<MarkerAnnotation>? MarkerAdded;

    /// <summary>Сколько пикселей снимка в одной логической точке окна.</summary>
    public double Scale => Screenshot is null || Bounds.Width <= 0 ? 1 : Screenshot.PixelSize.Width / Bounds.Width;

    public Point ToImage(Point view) => new(view.X * Scale, view.Y * Scale);
    public Point ToView(Point img) => new(img.X / Scale, img.Y / Scale);

    public Rect ToView(PixelRect r)
    {
        var s = Scale;
        return new Rect(r.X / s, r.Y / s, r.Width / s, r.Height / s);
    }

    public Rect ToView(Rect imgRect)
    {
        var s = Scale;
        return new Rect(imgRect.X / s, imgRect.Y / s, imgRect.Width / s, imgRect.Height / s);
    }

    // ── Команды ───────────────────────────────────────────────────────
    public void SelectAll()
    {
        if (Screenshot is null) return;
        Selection = new PixelRect(0, 0, Screenshot.PixelSize.Width, Screenshot.PixelSize.Height);
        SelectionChanged?.Invoke();
        InvalidateVisual();
    }

    /// <summary>Задать выделение точно в пикселях (размер области записи); остаётся в пределах снимка.</summary>
    public void SetSelection(PixelRect r)
    {
        if (Screenshot is null) return;
        int sw = Screenshot.PixelSize.Width, sh = Screenshot.PixelSize.Height;
        int w = Math.Clamp(r.Width, 1, sw), h = Math.Clamp(r.Height, 1, sh);
        int x = Math.Clamp(r.X, 0, sw - w), y = Math.Clamp(r.Y, 0, sh - h);
        Selection = new PixelRect(x, y, w, h);
        SelectionChanged?.Invoke();
        InvalidateVisual();
    }

    public void Undo()
    {
        if (Document.Undo()) AfterHistoryChange();
    }

    public void Redo()
    {
        if (Document.Redo()) AfterHistoryChange();
    }

    void AfterHistoryChange()
    {
        SetSelected(null);
        AnnotationsChanged?.Invoke();
        InvalidateVisual();
    }

    public void DeleteSelected()
    {
        if (SelectedAnnotation is { } a) Delete(a);
    }

    public void Delete(Annotation a)
    {
        if (!Document.Items.Contains(a)) return;
        var snap = Document.Snapshot();
        Document.Items.Remove(a);
        if (a is MarkerAnnotation) Document.RelabelMarkers();
        Document.PushUndo(snap);
        if (ReferenceEquals(SelectedAnnotation, a)) SetSelected(null);
        AnnotationsChanged?.Invoke();
        InvalidateVisual();
    }

    public void DeleteMarker(Guid id)
    {
        var m = Document.Items.OfType<MarkerAnnotation>().FirstOrDefault(x => x.Id == id);
        if (m is not null) Delete(m);
    }

    /// <summary>Дублировать выделенный объект (копия со сдвигом). Метка получает следующую букву.</summary>
    public void DuplicateSelected()
    {
        if (SelectedAnnotation is not { } a) return;
        var snap = Document.Snapshot();
        Annotation copy;
        if (a is MarkerAnnotation m)
        {
            // Копия со всеми цветами и стилем подписи, но с новой буквой и своим Id.
            var numbered = Document.NewMarker();
            var fresh = (MarkerAnnotation)m.Clone();
            fresh.Id = Guid.NewGuid();
            fresh.Sequence = numbered.Sequence;
            fresh.Label = numbered.Label;
            copy = fresh;
        }
        else
        {
            copy = a.Clone();
        }
        var shift = 18 * Scale;
        copy.Offset(shift, shift);
        Document.Items.Add(copy);
        Document.PushUndo(snap);
        SetSelected(copy);
        AnnotationsChanged?.Invoke();
        if (copy is MarkerAnnotation added) MarkerAdded?.Invoke(added);
        InvalidateVisual();
    }

    public void ApplyStyleToSelected()
    {
        if (SelectedAnnotation is null) return;
        var snap = Document.Snapshot();
        SelectedAnnotation.ApplyStyle(DrawStyle);
        Document.PushUndo(snap);
        AnnotationsChanged?.Invoke();
        InvalidateVisual();
    }

    public void ClearSelectedAnnotation() => SetSelected(null);

    /// <summary>Выделить объект программно (например, только что созданный текст).</summary>
    public void Select(Annotation? a) => SetSelected(a);

    /// <summary>Переключить A-B-C / 1-2-3 для всех меток скриншота.</summary>
    public void SetNumbering(MarkerNumbering numbering)
    {
        Document.Numbering = numbering;
        Document.RelabelMarkers();
        AnnotationsChanged?.Invoke();
        InvalidateVisual();
    }

    /// <summary>Форма меток: для выделенной метки — только для неё, иначе для всех меток скриншота.</summary>
    public void ApplyMarkerShape(MarkerShape shape)
    {
        var targets = SelectedAnnotation is MarkerAnnotation sm
            ? new List<MarkerAnnotation> { sm }
            : Document.Items.OfType<MarkerAnnotation>().ToList();
        if (targets.Count == 0) return;
        var snap = Document.Snapshot();
        foreach (var m in targets) m.Shape = shape;
        Document.PushUndo(snap);
        AnnotationsChanged?.Invoke();
        InvalidateVisual();
    }

    void SetSelected(Annotation? a)
    {
        if (ReferenceEquals(SelectedAnnotation, a)) return;
        SelectedAnnotation = a;
        SelectedAnnotationChanged?.Invoke();
        InvalidateVisual();
    }

    // ── Отрисовка ─────────────────────────────────────────────────────
    public override void Render(DrawingContext ctx)
    {
        if (Screenshot is null) return;
        var full = new Rect(Bounds.Size);
        ctx.DrawImage(Screenshot, new Rect(Screenshot.Size), full);

        if (Selection is not PixelRect sel || sel.Width <= 0 || sel.Height <= 0)
        {
            ctx.DrawRectangle(DimBrush, null, full);
            return;
        }

        var v = ToView(sel);

        // Затемнение вокруг выделения.
        ctx.DrawRectangle(DimBrush, null, new Rect(0, 0, full.Width, v.Top));
        ctx.DrawRectangle(DimBrush, null, new Rect(0, v.Bottom, full.Width, Math.Max(0, full.Height - v.Bottom)));
        ctx.DrawRectangle(DimBrush, null, new Rect(0, v.Top, v.Left, v.Height));
        ctx.DrawRectangle(DimBrush, null, new Rect(v.Right, v.Top, Math.Max(0, full.Width - v.Right), v.Height));

        // Аннотации — в пикселях снимка, обрезаются по выделению.
        var s = Scale;
        using (ctx.PushClip(v))
        using (ctx.PushTransform(Matrix.CreateScale(1 / s, 1 / s)))
        {
            foreach (var a in Document.Items)
                if (!ReferenceEquals(a, EditingText)) a.Render(ctx);
            CommentLabels.Render(ctx, Document, CommentStyle, new Rect(sel.X, sel.Y, sel.Width, sel.Height));

            if (SelectedAnnotation is not null)
            {
                var b = SelectedAnnotation.Bounds.Inflate(5 * s);
                var pen = new Pen(_frameBrush, 1.25 * s, new DashStyle(new double[] { 3, 2.5 }, 0));
                ctx.DrawRectangle(null, pen, b, 5 * s, 5 * s);
            }
        }

        // Рамка: один чистый прямоугольник. Если выделен весь экран, рамка уходила бы за край —
        // тогда рисуем её внутрь, чтобы она всегда была видна.
        var frame = v.Inflate(0.75);
        var inner = full.Deflate(0.75);
        double fl = Math.Max(frame.Left, inner.Left), ft = Math.Max(frame.Top, inner.Top);
        double fr = Math.Min(frame.Right, inner.Right), fb = Math.Min(frame.Bottom, inner.Bottom);
        if (fr > fl && fb > ft) frame = new Rect(fl, ft, fr - fl, fb - ft);
        ctx.DrawRectangle(null, new Pen(_frameBrush, 1.5), frame);

        if (IsAdjustingSelection) DrawSizeLabel(ctx, sel, v);
    }

    void DrawSizeLabel(DrawingContext ctx, PixelRect sel, Rect v)
    {
        int w = sel.Width, h = sel.Height;
        if (Mode == CaptureMode.VideoRegion) { w &= ~1; h &= ~1; }
        var ft = new FormattedText($"{w} × {h}", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(UiFonts.Family, FontStyle.Normal, FontWeight.SemiBold), 11.5, Brushes.White);
        var box = new Rect(0, 0, ft.Width + 14, ft.Height + 6);
        double x = v.Left, y = v.Top - box.Height - 8;
        if (y < 4) y = v.Top + 8;
        if (x + box.Width > Bounds.Width - 4) x = Bounds.Width - box.Width - 4;
        box = box.WithX(Math.Max(4, x)).WithY(Math.Max(4, y));
        ctx.DrawRectangle(LabelBg, null, box, 6, 6);
        ctx.DrawText(ft, new Point(box.X + 7, box.Y + 3));
    }

    /// <summary>Невидимые зоны: 0 TL, 1 T, 2 TR, 3 R, 4 BR, 5 B, 6 BL, 7 L.</summary>
    int HitHandle(Point view)
    {
        if (Selection is not PixelRect sel) return -1;
        var v = ToView(sel);

        bool nearL = Math.Abs(view.X - v.Left) <= CornerHit, nearR = Math.Abs(view.X - v.Right) <= CornerHit;
        bool nearT = Math.Abs(view.Y - v.Top) <= CornerHit, nearB = Math.Abs(view.Y - v.Bottom) <= CornerHit;
        if (nearL && nearT) return 0;
        if (nearR && nearT) return 2;
        if (nearR && nearB) return 4;
        if (nearL && nearB) return 6;

        bool inX = view.X > v.Left && view.X < v.Right;
        bool inY = view.Y > v.Top && view.Y < v.Bottom;
        if (inX && Math.Abs(view.Y - v.Top) <= EdgeHit) return 1;
        if (inY && Math.Abs(view.X - v.Right) <= EdgeHit) return 3;
        if (inX && Math.Abs(view.Y - v.Bottom) <= EdgeHit) return 5;
        if (inY && Math.Abs(view.X - v.Left) <= EdgeHit) return 7;
        return -1;
    }

    Annotation? HitAnnotation(Point img, double tolerancePx = 5)
    {
        var tol = tolerancePx * Scale;
        for (int i = Document.Items.Count - 1; i >= 0; i--)
            if (Document.Items[i].HitTest(img, tol)) return Document.Items[i];
        return null;
    }

    static bool Contains(PixelRect r, Point p) => p.X >= r.X && p.Y >= r.Y && p.X <= r.Right && p.Y <= r.Bottom;

    // ── Ввод ──────────────────────────────────────────────────────────
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Screenshot is null) return;

        var pt = e.GetCurrentPoint(this);
        var img = ToImage(pt.Position);

        if (pt.Properties.IsRightButtonPressed)
        {
            e.Handled = true;
            // ПКМ по объекту — удалить его (любым инструментом).
            if (Selection is PixelRect s0 && Contains(s0, img) && HitAnnotation(img, 7) is { } target)
            {
                Delete(target);
                return;
            }
            // ПКМ по пустому месту — сбросить выделение, если ещё ничего не нарисовано.
            if (Selection is not null && Document.Items.Count == 0)
            {
                Selection = null;
                SetSelected(null);
                SelectionChanged?.Invoke();
                InvalidateVisual();
            }
            return;
        }
        if (!pt.Properties.IsLeftButtonPressed) return;

        Focus();
        e.Handled = true;
        _pressImg = _lastImg = _snapAnchor = img;
        _moved = false;
        _lastTimestamp = e.Timestamp;

        if (e.ClickCount >= 2 && HandleDoubleClick(img)) return;

        e.Pointer.Capture(this);

        if (Selection is not PixelRect sel)
        {
            BeginSelecting(img);
            return;
        }

        _handle = HitHandle(pt.Position);
        if (_handle >= 0)
        {
            SetDrag(Drag.Resizing);
            _origSelection = sel;
            return;
        }

        if (!Contains(sel, img))
        {
            // Клик снаружи: пока ничего не нарисовано — новое выделение.
            if (Document.Items.Count == 0) BeginSelecting(img);
            else e.Pointer.Capture(null);
            return;
        }

        if (Mode == CaptureMode.VideoRegion || Tool == ToolKind.Select)
        {
            var hit = Mode == CaptureMode.VideoRegion ? null : HitAnnotation(img);
            if (hit is not null)
            {
                SetSelected(hit);
                _snapshot = Document.Snapshot();
                SetDrag(Drag.MovingAnnotation);
            }
            else
            {
                SetSelected(null);
                SetDrag(Drag.MovingSelection);
                _origSelection = sel;
            }
            return;
        }

        switch (Tool)
        {
            case ToolKind.Eraser:
                SetSelected(null);
                _snapshot = Document.Snapshot();
                _erasedAny = false;
                SetDrag(Drag.Erasing);
                EraseAt(img);
                return;

            case ToolKind.Text:
            {
                e.Pointer.Capture(null);
                SetSelected(null);
                if (HitAnnotation(img) is TextAnnotation existing)
                {
                    TextEditRequested?.Invoke(existing, false);
                }
                else
                {
                    var ta = new TextAnnotation { Position = new Point(img.X, img.Y - DrawStyle.FontSize * 0.6) };
                    ta.ApplyStyle(DrawStyle);
                    TextEditRequested?.Invoke(ta, true);
                }
                return;
            }

            case ToolKind.Marker:
            {
                var snap = Document.Snapshot();
                var m = Document.NewMarker();
                m.Center = img;
                m.ApplyStyle(DrawStyle);
                m.ApplyCommentStyle(CommentStyle);
                Document.Items.Add(m);
                Document.PushUndo(snap);
                e.Pointer.Capture(null);
                SetSelected(m); // рядом сразу появится панель настроек этой метки
                AnnotationsChanged?.Invoke();
                MarkerAdded?.Invoke(m);
                InvalidateVisual();
                return;
            }

            case ToolKind.MarkerArrow:
            {
                _snapshot = Document.Snapshot();
                var m = Document.NewMarker();
                m.Center = img;
                m.Tail = img;
                m.ApplyStyle(DrawStyle);
                m.ApplyCommentStyle(CommentStyle);
                SetSelected(null);
                _drawing = m;
                Document.Items.Add(m);
                SetDrag(Drag.Drawing);
                return;
            }

            default:
            {
                SetSelected(null);
                _snapshot = Document.Snapshot();
                _snapped = false;
                _inkWidth = 1;
                _drawing = CreateShape(Tool, img, InkFactor(e, pt, 0));
                Document.Items.Add(_drawing);
                SetDrag(Drag.Drawing);
                if (_drawing is FreehandAnnotation) _snapTimer.Start();
                return;
            }
        }
    }

    bool HandleDoubleClick(Point img)
    {
        if (Selection is null)
        {
            SelectAll();
            return true;
        }
        if (Tool is ToolKind.Select or ToolKind.Text && HitAnnotation(img) is TextAnnotation ta)
        {
            TextEditRequested?.Invoke(ta, false);
            return true;
        }
        return false;
    }

    void BeginSelecting(Point img)
    {
        SetDrag(Drag.Selecting);
        SetSelected(null);
        Selection = new PixelRect((int)img.X, (int)img.Y, 0, 0);
        SelectionChanged?.Invoke();
    }

    void SetDrag(Drag d)
    {
        _drag = d;
        InteractionChanged?.Invoke();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (Screenshot is null) return;

        var view = e.GetPosition(this);
        var img = ToImage(view);

        if (_drag == Drag.None)
        {
            UpdateCursor(view);
            return;
        }

        if (Annotation.Distance(img, _pressImg) > 2 * Scale) _moved = true;
        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        switch (_drag)
        {
            case Drag.Selecting:
                Selection = Normalize(_pressImg, img);
                SelectionChanged?.Invoke();
                break;

            case Drag.MovingSelection:
            {
                int maxX = Screenshot.PixelSize.Width - _origSelection.Width;
                int maxY = Screenshot.PixelSize.Height - _origSelection.Height;
                int nx = Math.Clamp(_origSelection.X + (int)Math.Round(img.X - _pressImg.X), 0, Math.Max(0, maxX));
                int ny = Math.Clamp(_origSelection.Y + (int)Math.Round(img.Y - _pressImg.Y), 0, Math.Max(0, maxY));
                Selection = new PixelRect(nx, ny, _origSelection.Width, _origSelection.Height);
                SelectionChanged?.Invoke();
                break;
            }

            case Drag.Resizing:
                Selection = Resize(_origSelection, _handle, img);
                SelectionChanged?.Invoke();
                break;

            case Drag.Drawing:
                UpdateShape(img, shift, InkFactor(e, e.GetCurrentPoint(this), Annotation.Distance(img, _lastImg)));
                // Удержание на месте → распознать фигуру (таймер перезапускается при заметном движении).
                if (!_snapped && Annotation.Distance(img, _snapAnchor) > 3 * Scale)
                {
                    _snapAnchor = img;
                    _snapTimer.Stop();
                    if (_drawing is FreehandAnnotation) _snapTimer.Start();
                }
                break;

            case Drag.MovingAnnotation:
                SelectedAnnotation?.Offset(img.X - _lastImg.X, img.Y - _lastImg.Y);
                break;

            case Drag.Erasing:
                EraseAt(img);
                break;
        }

        _lastImg = img;
        _lastTimestamp = e.Timestamp;
        InvalidateVisual();
    }

    /// <summary>
    /// Толщина пера: со стилусом — по нажиму; мышью — по скорости (быстрее → тоньше), как у живой ручки.
    /// </summary>
    double InkFactor(PointerEventArgs e, PointerPoint pt, double distanceImg)
    {
        if (e.Pointer.Type == PointerType.Pen && pt.Properties.Pressure > 0)
            return 0.3 + pt.Properties.Pressure * 1.2;

        ulong dt = e.Timestamp > _lastTimestamp ? e.Timestamp - _lastTimestamp : 16;
        double speed = distanceImg / Scale / Math.Max(1, dt); // логических точек за мс
        double target = Math.Clamp(1.25 - speed * 0.45, 0.5, 1.25);
        _inkWidth = _inkWidth * 0.75 + target * 0.25;
        return _inkWidth;
    }

    void EraseAt(Point img)
    {
        var hit = HitAnnotation(img, 8);
        if (hit is null) return;
        Document.Items.Remove(hit);
        if (hit is MarkerAnnotation) Document.RelabelMarkers();
        if (ReferenceEquals(hit, SelectedAnnotation)) SetSelected(null);
        _erasedAny = true;
        AnnotationsChanged?.Invoke();
    }

    void TrySnapShape()
    {
        _snapTimer.Stop();
        if (_drag != Drag.Drawing || _snapped || _drawing is not FreehandAnnotation stroke) return;
        var shape = ShapeRecognizer.Recognize(stroke);
        if (shape is null) return;
        int index = Document.Items.IndexOf(stroke);
        if (index < 0) return;
        Document.Items[index] = shape;
        _drawing = shape;
        _snapped = true;
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_drag == Drag.None) return;
        var drag = _drag;
        _drag = Drag.None;
        e.Pointer.Capture(null);
        Finish(drag);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (_drag == Drag.None) return;
        var drag = _drag;
        _drag = Drag.None;
        Finish(drag);
    }

    void Finish(Drag drag)
    {
        _snapTimer.Stop();
        switch (drag)
        {
            case Drag.Selecting:
                if (Selection is PixelRect s && (s.Width < 5 || s.Height < 5)) Selection = null;
                SelectionChanged?.Invoke();
                break;

            case Drag.MovingSelection:
            case Drag.Resizing:
                if (Selection is PixelRect r && (r.Width < 5 || r.Height < 5)) Selection = _origSelection;
                SelectionChanged?.Invoke();
                break;

            case Drag.Drawing:
                if (_drawing is not null)
                {
                    if (_drawing.IsDegenerate)
                    {
                        Document.Items.Remove(_drawing);
                    }
                    else
                    {
                        if (_snapshot is not null) Document.PushUndo(_snapshot);
                        AnnotationsChanged?.Invoke();
                        if (_drawing is MarkerAnnotation m)
                        {
                            if (m.Tail is Point t && Annotation.Distance(t, m.Center) < m.Radius * 1.3) m.Tail = null;
                            SetSelected(m);
                            MarkerAdded?.Invoke(m);
                        }
                    }
                }
                _drawing = null;
                break;

            case Drag.MovingAnnotation:
                if (_moved && _snapshot is not null)
                {
                    Document.PushUndo(_snapshot);
                    AnnotationsChanged?.Invoke();
                }
                break;

            case Drag.Erasing:
                if (_erasedAny && _snapshot is not null) Document.PushUndo(_snapshot);
                break;
        }
        _snapshot = null;
        InteractionChanged?.Invoke();
        InvalidateVisual();
    }

    // ── Фигуры ────────────────────────────────────────────────────────
    Annotation CreateShape(ToolKind tool, Point p, double inkFactor)
    {
        Annotation a = tool switch
        {
            ToolKind.Pen => new FreehandAnnotation { Kind = StrokeKind.Ink },
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
        if (a is FreehandAnnotation f) f.AddPoint(p, inkFactor);
        return a;
    }

    void UpdateShape(Point p, bool shift, double inkFactor)
    {
        switch (_drawing)
        {
            case FreehandAnnotation f:
                f.AddPoint(p, f.Kind == StrokeKind.Ink ? inkFactor : 1);
                break;
            case LineAnnotation l:
                l.End = shift ? Snap45(l.Start, p) : p;
                break;
            case BoxAnnotation b:
                b.End = shift ? SnapSquare(b.Start, p) : p;
                break;
            case MarkerAnnotation m:
                m.Tail = shift ? Snap45(m.Center, p) : p;
                break;
        }
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

    PixelRect Normalize(Point a, Point b)
    {
        int w = Screenshot!.PixelSize.Width, h = Screenshot.PixelSize.Height;
        int x1 = Math.Clamp((int)Math.Round(Math.Min(a.X, b.X)), 0, w);
        int y1 = Math.Clamp((int)Math.Round(Math.Min(a.Y, b.Y)), 0, h);
        int x2 = Math.Clamp((int)Math.Round(Math.Max(a.X, b.X)), 0, w);
        int y2 = Math.Clamp((int)Math.Round(Math.Max(a.Y, b.Y)), 0, h);
        return new PixelRect(x1, y1, x2 - x1, y2 - y1);
    }

    PixelRect Resize(PixelRect r, int handle, Point p)
    {
        double l = r.X, t = r.Y, rr = r.Right, b = r.Bottom;
        switch (handle)
        {
            case 0: l = p.X; t = p.Y; break;
            case 1: t = p.Y; break;
            case 2: rr = p.X; t = p.Y; break;
            case 3: rr = p.X; break;
            case 4: rr = p.X; b = p.Y; break;
            case 5: b = p.Y; break;
            case 6: l = p.X; b = p.Y; break;
            case 7: l = p.X; break;
        }
        return Normalize(new Point(l, t), new Point(rr, b));
    }

    // ── Курсоры ───────────────────────────────────────────────────────
    void UpdateCursor(Point view)
    {
        _cross ??= new Cursor(StandardCursorType.Cross);
        _arrow ??= new Cursor(StandardCursorType.Arrow);
        _hand ??= new Cursor(StandardCursorType.Hand);
        _sizeAll ??= new Cursor(StandardCursorType.SizeAll);
        _ibeam ??= new Cursor(StandardCursorType.Ibeam);
        _eraser ??= new Cursor(StandardCursorType.No);

        if (Selection is not PixelRect sel)
        {
            Cursor = _cross;
            return;
        }

        int h = HitHandle(view);
        if (h >= 0)
        {
            Cursor = _handleCursors[h] ??= new Cursor(h switch
            {
                0 => StandardCursorType.TopLeftCorner,
                2 => StandardCursorType.TopRightCorner,
                4 => StandardCursorType.BottomRightCorner,
                6 => StandardCursorType.BottomLeftCorner,
                1 or 5 => StandardCursorType.SizeNorthSouth,
                _ => StandardCursorType.SizeWestEast,
            });
            return;
        }

        var img = ToImage(view);
        if (!Contains(sel, img))
        {
            Cursor = Document.Items.Count == 0 ? _cross : _arrow;
            return;
        }

        if (Mode == CaptureMode.VideoRegion) Cursor = _sizeAll;
        else if (Tool == ToolKind.Select) Cursor = HitAnnotation(img) is not null ? _hand : _sizeAll;
        else if (Tool == ToolKind.Eraser) Cursor = HitAnnotation(img, 8) is not null ? _eraser : _cross;
        else if (Tool == ToolKind.Text) Cursor = _ibeam;
        else Cursor = _cross;
    }
}
