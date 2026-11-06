using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using SnapMark.Core;
using SnapMark.Platform;
using SnapMark.Services;
using static SnapMark.Services.TextRecognizer;

namespace SnapMark.UI;

/// <summary>
/// Текст с картинки — как в текстовом редакторе (по образцу «Живого текста» в iOS/macOS):
/// снимок области остаётся на своём месте, распознанные слова чуть подсвечены, выделяешь мышью
/// (протянуть, двойной щелчок — слово, тройной — строка, Shift+щелчок — продлить), Ctrl+C — копировать
/// выделенное, Ctrl+A — всё. Esc или ✕ — закрыть.
/// </summary>
public sealed class OcrWindow : Window
{
    readonly TextLayer _layer;
    readonly Button _copySelected;
    readonly Button _copyAll;
    readonly TextBlock _status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 4, 0) }.Style("callout");
    readonly double _scaling;
    const double Pad = 14;

    public static void Open(byte[] png, PixelRect screenRect)
    {
        var w = new OcrWindow(png, screenRect);
        w.Show();
        w.Activate();
        _ = w.RecognizeAsync(png);
    }

    OcrWindow(byte[] png, PixelRect at)
    {
        SystemDecorations = SystemDecorations.None;
        Topmost = true;
        ShowInTaskbar = false;
        CanResize = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        Background = Brushes.Transparent;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        WindowStartupLocation = WindowStartupLocation.Manual;
        Title = L.T("SnapMark — текст с картинки");
        FontFamily = Annotations.UiFonts.Family;
        Glass.EnableLiveBackdrop(this);

        var screens = App.GetScreens();
        var screen = screens.FirstOrDefault(s => s.Bounds.Contains(at.Center)) ?? screens.FirstOrDefault(s => s.IsPrimary);
        _scaling = screen?.Scaling > 0 ? screen.Scaling : 1;

        Bitmap bitmap;
        using (var ms = new MemoryStream(png)) bitmap = new Bitmap(ms);
        double w = bitmap.PixelSize.Width / _scaling, h = bitmap.PixelSize.Height / _scaling;
        _layer = new TextLayer(bitmap, 1 / _scaling) { Width = w, Height = h };
        _layer.SelectionChanged += SyncButtons;

        var frame = new Border
        {
            Child = _layer,
            CornerRadius = new CornerRadius(8),
            ClipToBounds = true,
            BorderThickness = new Thickness(1.5),
            BorderBrush = new SolidColorBrush(Color.Parse("#0A84FF")),
        };
        var shadow = new Border
        {
            Margin = new Thickness(Pad),
            CornerRadius = new CornerRadius(8),
            BoxShadow = BoxShadows.Parse("0 10 28 0 #59000000, 0 1 4 0 #33000000"),
            Background = Brushes.Black,
            Child = frame,
        };

        _copySelected = UiKit.SoftButton(L.T("Копировать выделенное"), () => _ = CopyAsync(all: false), primary: true);
        _copyAll = UiKit.SoftButton(L.T("Копировать всё"), () => _ = CopyAsync(all: true));
        var close = UiKit.ToolButton("Icon.Close", L.T("Закрыть (Esc)"), Close);
        _status.Text = L.T("Распознаю текст…");
        var bar = new GlassPanel
        {
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 6,
                Children = { _status, _copySelected, _copyAll, close },
            },
            Padding = new Thickness(6),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(Pad, 0, Pad, Pad),
        };
        bar.Classes.Add("pill");
        bar.Classes.Add("bar");

        // Панель — под картинкой; у нижнего края экрана — над ней.
        bool above = screen is not null && at.Bottom + 70 * _scaling > screen.WorkingArea.Bottom;
        var stack = new StackPanel();
        if (above) { stack.Children.Add(bar); bar.Margin = new Thickness(Pad, Pad, Pad, 0); }
        stack.Children.Add(shadow);
        if (!above) stack.Children.Add(bar);
        Content = stack;
        SyncButtons();

        Opened += (_, _) =>
        {
            // Картинка ровно на том месте экрана, где был выделенный фрагмент.
            double barH = above ? bar.Bounds.Height + Pad : 0;
            Position = new PixelPoint(at.X - (int)Math.Round(Pad * _scaling), at.Y - (int)Math.Round((Pad + barH) * _scaling));
            PlatformServices.WindowHelper.ForceForeground(this);
            _layer.Focus();
        };
    }

    async Task RecognizeAsync(byte[] png)
    {
        try
        {
            var result = await TextRecognizer.RecognizeAsync(png);
            _layer.SetResult(result);
            if (result.IsEmpty)
                _status.Text = L.T("Текст не найден");
            else
            {
                _status.Text = L.T("Выдели текст мышью");
                _layer.Cursor = new Cursor(StandardCursorType.Ibeam);
            }
        }
        catch (Exception ex)
        {
            _status.Text = L.T("Не удалось распознать текст");
            Toast.Show(L.T("Не удалось распознать текст"), ex.Message);
        }
        SyncButtons();
    }

    void SyncButtons()
    {
        _copySelected.IsEnabled = _layer.HasSelection;
        _copyAll.IsEnabled = _layer.HasText;
    }

    async Task CopyAsync(bool all)
    {
        var text = all || !_layer.HasSelection ? _layer.AllText : _layer.SelectedText;
        if (string.IsNullOrEmpty(text)) return;
        await SmartPaste.CopyPlainAsync(new ClipboardPayload { Text = text });
        _status.Text = L.T("Скопировано");
        DispatcherTimer.RunOnce(() => _status.Text = L.T("Выдели текст мышью"), TimeSpan.FromSeconds(1.6));
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        bool cmd = (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0;
        if (e.Key == Key.Escape) { Close(); e.Handled = true; }
        else if (cmd && e.Key == Key.C) { _ = CopyAsync(all: false); e.Handled = true; }
        else if (cmd && e.Key == Key.A) { _layer.SelectAll(); e.Handled = true; }
    }

    /// <summary>Картинка + слои выделения. Координаты слов — пиксели картинки × _k = точки.</summary>
    sealed class TextLayer : Control
    {
        readonly Bitmap _bitmap;
        readonly double _k;
        List<(OcrWord Word, int Line)> _words = new();
        OcrResult? _result;
        int _anchor = -1, _focus = -1;
        bool _dragging;
        bool _appear;
        readonly DispatcherTimer _fade;
        double _glow; // 0…1: короткая «вспышка» найденных слов, как в Живом тексте

        public event Action? SelectionChanged;

        public TextLayer(Bitmap bitmap, double k)
        {
            _bitmap = bitmap;
            _k = k;
            Focusable = true;
            ClipToBounds = true;
            _fade = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) =>
            {
                _glow = Math.Max(0, _glow - 0.025);
                if (_glow <= 0) StopFade();
                InvalidateVisual();
            });
            var menu = new ContextMenu();
            void Add(string header, Action act) { var m = new MenuItem { Header = header }; m.Click += (_, _) => act(); menu.Items.Add(m); }
            Add(L.T("Копировать (Ctrl+C)"), () => (VisualRoot as OcrWindow)?.CopyAsync(all: false));
            Add(L.T("Выделить всё (Ctrl+A)"), SelectAll);
            Add(L.T("Копировать всё"), () => (VisualRoot as OcrWindow)?.CopyAsync(all: true));
            ContextMenu = menu;
        }

        void StopFade() => _fade.Stop();

        public bool HasText => _words.Count > 0;
        public bool HasSelection => _anchor >= 0 && _focus >= 0;

        public string AllText => _result?.Text ?? "";

        public string SelectedText
        {
            get
            {
                if (!HasSelection) return "";
                int a = Math.Min(_anchor, _focus), b = Math.Max(_anchor, _focus);
                var lines = _words.Skip(a).Take(b - a + 1).GroupBy(w => w.Line).Select(g => Join(g.Select(x => x.Word)));
                return string.Join(Environment.NewLine, lines);
            }
        }

        public void SetResult(OcrResult r)
        {
            _result = r;
            _words = r.Lines.SelectMany((l, i) => l.Words.Select(w => (w, i))).ToList();
            _appear = true;
            _glow = 1;
            _fade.Start();
            InvalidateVisual();
        }

        public void SelectAll()
        {
            if (_words.Count == 0) return;
            _anchor = 0;
            _focus = _words.Count - 1;
            Changed();
        }

        void Changed()
        {
            InvalidateVisual();
            SelectionChanged?.Invoke();
        }

        Rect BoxOf(OcrWord w) => new Rect(w.X * _k, w.Y * _k, w.W * _k, w.H * _k);

        /// <summary>Слово под курсором или ближайшее: сперва ближайшая по вертикали строка, затем слово в ней.</summary>
        int WordAt(Point p)
        {
            if (_words.Count == 0) return -1;
            for (int i = 0; i < _words.Count; i++)
                if (BoxOf(_words[i].Word).Inflate(3).Contains(p)) return i;
            int bestLine = -1;
            double bestDy = double.MaxValue;
            foreach (var g in _words.Select((w, i) => (w, i)).GroupBy(x => x.w.Line))
            {
                double top = g.Min(x => BoxOf(x.w.Word).Top), bottom = g.Max(x => BoxOf(x.w.Word).Bottom);
                double dy = p.Y < top ? top - p.Y : p.Y > bottom ? p.Y - bottom : 0;
                if (dy < bestDy) { bestDy = dy; bestLine = g.Key; }
            }
            int best = -1;
            double bestDx = double.MaxValue;
            for (int i = 0; i < _words.Count; i++)
            {
                if (_words[i].Line != bestLine) continue;
                var b = BoxOf(_words[i].Word);
                double dx = p.X < b.Left ? b.Left - p.X : p.X > b.Right ? p.X - b.Right : 0;
                if (dx < bestDx) { bestDx = dx; best = i; }
            }
            return best;
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            Focus();
            var pt = e.GetCurrentPoint(this);
            if (!pt.Properties.IsLeftButtonPressed || _words.Count == 0) return;
            int i = WordAt(pt.Position);
            if (i < 0) return;
            if (e.ClickCount >= 3)
            {
                int line = _words[i].Line;
                _anchor = _words.FindIndex(w => w.Line == line);
                _focus = _words.FindLastIndex(w => w.Line == line);
            }
            else if (e.ClickCount == 2)
            {
                _anchor = _focus = i;
            }
            else if ((e.KeyModifiers & KeyModifiers.Shift) != 0 && HasSelection)
            {
                _focus = i;
            }
            else
            {
                // Простой щелчок по пустому месту (не по слову) — снять выделение.
                bool onWord = BoxOf(_words[i].Word).Inflate(3).Contains(pt.Position);
                _anchor = _focus = onWord ? i : -1;
                if (!onWord) _anchor = i; // протяжка от ближайшего слова
                _dragging = true;
                e.Pointer.Capture(this);
            }
            Changed();
            e.Handled = true;
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            if (!_dragging) return;
            int i = WordAt(e.GetPosition(this));
            if (i >= 0 && i != _focus)
            {
                _focus = i;
                Changed();
            }
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            if (!_dragging) return;
            _dragging = false;
            e.Pointer.Capture(null);
            if (_focus < 0) { _anchor = -1; Changed(); }
        }

        static readonly IBrush SelBrush = new SolidColorBrush(Color.FromArgb(105, 10, 132, 255));
        static readonly IBrush HintBrush = new SolidColorBrush(Color.FromArgb(26, 10, 132, 255));

        public override void Render(DrawingContext ctx)
        {
            var size = Bounds.Size;
            ctx.DrawImage(_bitmap, new Rect(_bitmap.Size), new Rect(size));
            if (!_appear) return;

            // Лёгкое затемнение вне текста — слова «приподнимаются», как в Живом тексте.
            ctx.FillRectangle(new SolidColorBrush(Color.FromArgb(28, 0, 0, 0)), new Rect(size));
            foreach (var (w, _) in _words)
            {
                var b = BoxOf(w).Inflate(new Thickness(2, 1));
                ctx.DrawRectangle(HintBrush, null, b, 3, 3);
                if (_glow > 0)
                    ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb((byte)(120 * _glow), 255, 255, 255)), null, b, 3, 3);
            }

            if (!HasSelection) return;
            int a = Math.Min(_anchor, _focus), z = Math.Max(_anchor, _focus);
            // Выделение строками: от первого до последнего выделенного слова строки — сплошной полосой.
            foreach (var g in _words.Skip(a).Take(z - a + 1).GroupBy(x => x.Line))
            {
                var boxes = g.Select(x => BoxOf(x.Word)).ToList();
                var r = new Rect(new Point(boxes.Min(b => b.Left), boxes.Min(b => b.Top)), new Point(boxes.Max(b => b.Right), boxes.Max(b => b.Bottom)))
                    .Inflate(new Thickness(2, 1.5));
                ctx.DrawRectangle(SelBrush, null, r, 3, 3);
            }
        }
    }
}
