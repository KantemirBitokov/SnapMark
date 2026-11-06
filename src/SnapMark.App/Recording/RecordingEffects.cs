using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using SnapMark.Annotations;
using SnapMark.Core;
using SnapMark.Platform;
using SnapMark.Services;

namespace SnapMark.Recording;

/// <summary>
/// Эффекты для обучающих роликов: круги на месте кликов и плашка с нажатыми сочетаниями клавиш.
/// Прозрачное окно поверх области записи: клики проходят сквозь него, а само оно попадает в видео.
/// На macOS клики рисует сам ffmpeg (-capture_mouse_clicks), здесь — только клавиши.
/// </summary>
public sealed class RecordingEffectsWindow : Window
{
    readonly PixelRect _rect;
    readonly EffectsLayer _layer;
    readonly IDisposable? _input;
    readonly bool _clicks, _keys;

    public RecordingEffectsWindow(PixelRect rect, double scaling, bool clicks, bool keys)
    {
        _rect = rect;
        _clicks = clicks && !OperatingSystem.IsMacOS();
        _keys = keys;
        SystemDecorations = SystemDecorations.None;
        Topmost = true;
        ShowInTaskbar = false;
        CanResize = false;
        ShowActivated = false;
        Focusable = false;
        IsHitTestVisible = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Background = Brushes.Transparent;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        Position = rect.Position;
        Width = rect.Width / scaling;
        Height = rect.Height / scaling;
        MinWidth = MinHeight = 0;
        Title = "SnapMark — эффекты записи";
        _layer = new EffectsLayer();
        Content = _layer;

        var monitor = InputMonitor.Instance;
        if (monitor.IsSupported && (_clicks || _keys))
        {
            if (_clicks) monitor.Click += OnClick;
            if (_keys) monitor.KeyCombo += OnKey;
            _input = monitor.Acquire();
        }

        Opened += (_, _) =>
        {
            PlatformServices.WindowHelper.SetPixelBounds(this, _rect);
            PlatformServices.WindowHelper.SetClickThrough(this, true);
        };
    }

    /// <summary>Слежение за вводом не запустилось (нет разрешения и т. п.).</summary>
    public string? Problem => InputMonitor.Instance.Problem;

    void OnClick(InputClick c) => Dispatcher.UIThread.Post(() =>
    {
        double scaling = RenderScaling > 0 ? RenderScaling : 1;
        double px = InputMonitor.Instance.LogicalCoordinates ? c.X * scaling : c.X;
        double py = InputMonitor.Instance.LogicalCoordinates ? c.Y * scaling : c.Y;
        if (px < _rect.X || py < _rect.Y || px > _rect.Right || py > _rect.Bottom) return;
        _layer.AddRipple(new Point((px - _rect.X) / scaling, (py - _rect.Y) / scaling), c.Button);
    });

    void OnKey(string combo) => Dispatcher.UIThread.Post(() => _layer.ShowKeys(combo));

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        InputMonitor.Instance.Click -= OnClick;
        InputMonitor.Instance.KeyCombo -= OnKey;
        _input?.Dispose();
        _layer.Stop();
    }

    sealed class EffectsLayer : Control
    {
        sealed record Ripple(Point Center, ClickButton Button, long StartMs);

        readonly List<Ripple> _ripples = new();
        readonly Stopwatch _clock = Stopwatch.StartNew();
        readonly DispatcherTimer _timer;
        string? _keys;
        int _repeat = 1;
        long _keysMs = -10_000;

        const int RippleMs = 520, KeysHoldMs = 1500, KeysFadeMs = 350;

        public EffectsLayer()
        {
            IsHitTestVisible = false;
            _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) => Tick());
        }

        public void AddRipple(Point p, ClickButton button)
        {
            _ripples.Add(new Ripple(p, button, _clock.ElapsedMilliseconds));
            _timer.Start();
        }

        public void ShowKeys(string combo)
        {
            long now = _clock.ElapsedMilliseconds;
            // Одно и то же сочетание подряд — «Ctrl + Z ×3», а не мигание.
            if (combo == _keys && now - _keysMs < KeysHoldMs + KeysFadeMs) _repeat++;
            else _repeat = 1;
            _keys = combo;
            _keysMs = now;
            _timer.Start();
        }

        public void Stop() => _timer.Stop();

        void Tick()
        {
            long now = _clock.ElapsedMilliseconds;
            _ripples.RemoveAll(r => now - r.StartMs > RippleMs);
            bool keysVisible = _keys is not null && now - _keysMs < KeysHoldMs + KeysFadeMs;
            if (_ripples.Count == 0 && !keysVisible) _timer.Stop();
            InvalidateVisual();
        }

        public override void Render(DrawingContext ctx)
        {
            long now = _clock.ElapsedMilliseconds;
            foreach (var r in _ripples)
            {
                double t = Math.Clamp((now - r.StartMs) / (double)RippleMs, 0, 1);
                double ease = 1 - Math.Pow(1 - t, 3);
                double radius = 10 + 26 * ease;
                byte a = (byte)(255 * (1 - t));
                var color = r.Button == ClickButton.Right ? Color.FromRgb(255, 159, 10) : Color.FromRgb(10, 132, 255);
                ctx.DrawEllipse(new SolidColorBrush(color, 0.28 * (1 - t)), new Pen(new SolidColorBrush(Color.FromArgb(a, color.R, color.G, color.B)), 3),
                    r.Center, radius, radius);
                // Точка в центре — сразу видно место нажатия.
                if (t < 0.5) ctx.DrawEllipse(new SolidColorBrush(color, 0.9 * (1 - t * 2)), null, r.Center, 5, 5);
            }

            if (_keys is null) return;
            long age = now - _keysMs;
            if (age > KeysHoldMs + KeysFadeMs) return;
            double alpha = age < KeysHoldMs ? 1 : 1 - (age - KeysHoldMs) / (double)KeysFadeMs;
            var text = _repeat > 1 ? $"{_keys}  ×{_repeat}" : _keys;
            double size = Math.Clamp(Bounds.Height * 0.045, 16, 30);
            var ft = new FormattedText(text, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(UiFonts.Display, FontStyle.Normal, FontWeight.SemiBold), size, new SolidColorBrush(Colors.White, alpha));
            double padX = size * 0.8, padY = size * 0.45;
            var box = new Rect((Bounds.Width - ft.Width) / 2 - padX, Bounds.Height - ft.Height - padY * 2 - size * 1.2,
                ft.Width + padX * 2, ft.Height + padY * 2);
            double radiusBox = box.Height / 2;
            ctx.DrawRectangle(new SolidColorBrush(Color.FromRgb(28, 28, 30), 0.82 * alpha),
                new Pen(new SolidColorBrush(Colors.White, 0.22 * alpha), 1), box, radiusBox, radiusBox,
                new BoxShadows(new BoxShadow { OffsetY = 6, Blur = 18, Color = Color.FromArgb((byte)(90 * alpha), 0, 0, 0) }));
            ctx.DrawText(ft, new Point(box.X + padX, box.Y + padY));
        }
    }
}
