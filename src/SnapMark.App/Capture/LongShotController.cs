using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using SkiaSharp;
using SnapMark.Core;
using SnapMark.Export;
using SnapMark.Platform;
using SnapMark.Services;
using SnapMark.UI;

namespace SnapMark.Capture;

/// <summary>
/// Длинный скриншот: SnapMark сам прокручивает страницу или документ под выделенной областью
/// (колесом мыши в её центре), снимает кадры и склеивает их. Остановится сам в конце страницы;
/// «Готово» или Esc — раньше. Если автопрокрутка недоступна — крути сам, кадры снимаются автоматически.
/// </summary>
public static class LongShotController
{
    const int MaxHeight = 30000, MaxFrames = 120;
    static CancellationTokenSource? _cts;
    static LongShotHud? _hud;
    static IDisposable? _keys;

    public static bool IsActive => _cts is not null;

    /// <summary>Отладка (автотесты): шаги съёмки.</summary>
    public static Action<string>? Trace;

    public static void Start(PixelRect rect, double scaling)
    {
        if (IsActive) return;
        _cts = new CancellationTokenSource();
        var cts = _cts;
        bool auto = AutoScroll.IsSupported;
        _hud = new LongShotHud(auto);
        _hud.Done += () => cts.Cancel();
        _hud.Show();
        // Esc где угодно — закончить (клавиши видит слежение за вводом, если оно доступно).
        var mon = InputMonitor.Instance;
        if (mon.IsSupported)
        {
            mon.KeyCombo += OnKey;
            _keys = mon.Acquire();
        }
        _ = RunAsync(rect, scaling, auto, cts.Token);
    }

    static void OnKey(string combo)
    {
        if (combo == "Esc") Dispatcher.UIThread.Post(() => _cts?.Cancel());
    }

    /// <summary>Снять и склеить (доступно и для автотестов). progress: (кадров, высота); manual — автопрокрутка не работает, крутит человек.</summary>
    /// <remarks>
    /// Сколько пикселей даёт один «щелчок» колеса, заранее не известно: браузеры крутят на 100 px × масштаб экрана,
    /// Firefox — на несколько строк текста, Edge и новый Chrome — на долю высоты окна. Поэтому шаг подбирается
    /// по факту: первый — один щелчок, дальше — примерно на 45 % высоты области, чтобы с прошлым кадром всегда
    /// оставалось перекрытие. Проскочили (перекрытия нет) — откатываемся назад и уменьшаем шаг.
    /// Если в центре области прокрутка не действует (карта, карусель, окно с правами администратора…) —
    /// пробуем другие точки, а если нигде не крутится — переходим в ручной режим: крутит человек, кадры снимаются сами.
    /// </remarks>
    public static async Task<SKBitmap?> CaptureAsync(PixelRect rect, double scaling, bool auto, CancellationToken ct,
        Action<int, int>? progress = null, Action? manual = null)
    {
        var first = await Task.Run(() => Grab(rect));
        if (first is null) return null;
        int fw = first.Width, fh = first.Height;
        var stitcher = new Stitcher(fw);
        // Не больше ~400 МБ на строки результата (широкая область на 4K-экране — это 15 КБ на строку).
        int maxHeight = Math.Clamp(400_000_000 / Math.Max(1, fw * 4), 2000, MaxHeight);
        stitcher.Add(first);
        first.Dispose();
        progress?.Invoke(stitcher.Frames, stitcher.Height);
        int still = 0, misses = 0;
        int notches = 1;               // первый шаг — один щелчок: заодно меряем, сколько он даёт
        double pxPerNotch = 0;         // замер (с запасом в большую сторону)
        double lastPerNotch = 0;       // последний замер — для ожидаемого сдвига
        bool steady = false;           // два последних замера совпали — прокрутке можно верить вслепую
        bool moved = false;
        bool triedOther = false;       // перед тем как решить «конец страницы» — одна попытка в другой точке
        int target = Math.Max(1, (int)(rect.Height * 0.45));
        // Точки, где крутим колесо: центр, затем левее и выше (если в центре — элемент, который сам ловит колесо).
        var points = new[]
        {
            new PixelPoint(rect.X + rect.Width / 2, rect.Y + rect.Height / 2),
            new PixelPoint(rect.X + rect.Width / 5, rect.Y + rect.Height / 2),
            new PixelPoint(rect.X + rect.Width / 2, rect.Y + rect.Height / 5),
            new PixelPoint(rect.X + rect.Width * 4 / 5, rect.Y + rect.Height * 4 / 5),
        };
        int point = 0;
        var wh = PlatformServices.WindowHelper;
        while (!ct.IsCancellationRequested && stitcher.Height < maxHeight && stitcher.Frames < MaxFrames)
        {
            int cx = points[point].X, cy = points[point].Y;
            // Двигаешь мышь (например, к «Готово») — пауза: курсор не перехватываем и не крутим.
            if (auto && wh.GetCursorPosition() is PixelPoint cur && (Math.Abs(cur.X - cx) > 12 || Math.Abs(cur.Y - cy) > 12) && stitcher.Frames > 1)
            {
                var before = cur;
                try { await Task.Delay(400, ct); } catch (OperationCanceledException) { break; }
                if (wh.GetCursorPosition() is PixelPoint after && (after.X != before.X || after.Y != before.Y)) continue;
            }
            int step = notches;
            if (auto) AutoScroll.Scroll(cx, cy, step, scaling);
            SKBitmap? frame;
            try { frame = await SettledFrameAsync(rect, auto ? 160 : 220, ct); }
            catch (OperationCanceledException) { break; }
            if (frame is null) break;
            int expected = auto && lastPerNotch > 0 ? (int)Math.Round(step * lastPerNotch) : 0;
            int added;
            // Размер кадра поменялся (сменили разрешение или масштаб экрана) — заканчиваем с тем, что уже снято.
            if (frame.Width != fw || frame.Height != fh) { frame.Dispose(); break; }
            using (frame) added = stitcher.Add(frame, expected, steady);
            if (added < 0 && auto)
            {
                // Не сошлось — возможно, страница ещё докручивалась или дорисовывалась: ещё один взгляд.
                try { await Task.Delay(500, ct); } catch (OperationCanceledException) { break; }
                SKBitmap? again;
                try { again = await SettledFrameAsync(rect, 0, ct); }
                catch (OperationCanceledException) { break; }
                if (again is null || again.Width != fw || again.Height != fh) { again?.Dispose(); break; }
                using (again) added = stitcher.Add(again, expected, steady);
                Trace?.Invoke($"  recheck: added={added} | {stitcher.LastInfo}");
            }
            if (added == 0 && auto)
            {
                // Firefox и некоторые программы начинают плавную прокрутку с задержкой (особенно самую первую,
                // пока окно становится активным) — посмотрим ещё раз чуть позже.
                try { await Task.Delay(moved ? 600 : 1500, ct); } catch (OperationCanceledException) { break; }
                SKBitmap? again;
                try { again = await SettledFrameAsync(rect, 0, ct); }
                catch (OperationCanceledException) { break; }
                if (again is null || again.Width != fw || again.Height != fh) { again?.Dispose(); break; }
                using (again) added = stitcher.Add(again, expected, steady);
            }
            Trace?.Invoke($"step={step} point={point} added={added} height={stitcher.Height} pxPerNotch={pxPerNotch:0.#} still={still} misses={misses} | {stitcher.LastInfo}");
            progress?.Invoke(stitcher.Frames, stitcher.Height);
            if (!auto) continue; // ручной режим: снимаем, пока не нажмут «Готово»
            if (added > 0)
            {
                still = misses = 0;
                moved = true;
                triedOther = false;
                // Сдвиг на step щелчков = added строк (если не упёрлись в конец страницы — тогда сдвиг меньше).
                double measured = added / (double)step;
                steady = lastPerNotch > 0 && Math.Abs(measured - lastPerNotch) <= lastPerNotch * 0.03;
                lastPerNotch = measured;
                pxPerNotch = measured;
                notches = Math.Clamp((int)(target / pxPerNotch), 1, 20);
            }
            else if (added == 0)
            {
                if (!moved)
                {
                    // Ещё ни разу не сдвинулось: другая точка, а если нигде — ручной режим.
                    if (++point < points.Length) continue;
                    Trace?.Invoke("manual: wheel has no effect");
                    auto = false;
                    point = 0;
                    manual?.Invoke();
                    continue;
                }
                // Конец страницы: несколько раз подряд ничего не сдвинулось. Но сначала — ещё одна точка
                // и мелкий шаг: бывает, что окно перестало принимать колесо в этом месте (всплыло что-то поверх).
                if (++still >= 3)
                {
                    if (triedOther) break;
                    triedOther = true;
                    still = 0;
                    point = (point + 1) % points.Length;
                    notches = 1;
                }
            }
            else if (!moved && misses == 0 && stitcher.Frames == 1)
            {
                // Ещё ничего не склеено, а кадр изменился целиком — окно в момент начала ещё загружалось
                // (список в Проводнике, страница). Возвращаемся назад и начинаем заново с того, что видно сейчас.
                misses++;
                AutoScroll.Scroll(cx, cy, -step, scaling);
                SKBitmap? fresh;
                try { fresh = await SettledFrameAsync(rect, 500, ct); }
                catch (OperationCanceledException) { break; }
                if (fresh is null || fresh.Width != fw || fresh.Height != fh) { fresh?.Dispose(); break; }
                stitcher = new Stitcher(fw);
                using (fresh) stitcher.Add(fresh);
                Trace?.Invoke("restart: content changed before anything was stitched");
                progress?.Invoke(stitcher.Frames, stitcher.Height);
            }
            else
            {
                // Проскочили: возвращаемся на то же число щелчков назад и дальше идём шагом вдвое меньше.
                still = 0;
                if (++misses > 6) break;
                AutoScroll.Scroll(cx, cy, -step, scaling);
                notches = Math.Max(1, step / 2);
                if (step == 1 && misses >= 2) pxPerNotch = Math.Max(pxPerNotch, target * 2.0); // щелчок больше полкадра
                try { await Task.Delay(350, ct); } catch (OperationCanceledException) { break; }
            }
        }
        return stitcher.Result();
    }

    /// <summary>
    /// Кадр после прокрутки, когда страница успокоилась: плавная прокрутка и подгрузка ещё идут — ждём, пока два
    /// снимка подряд не станут почти одинаковыми (анимации на странице — мерцание, карусели — не в счёт), но не дольше ~1 с.
    /// </summary>
    static async Task<SKBitmap?> SettledFrameAsync(PixelRect rect, int delay, CancellationToken ct)
    {
        await Task.Delay(delay, ct);
        var prev = await Task.Run(() => Grab(rect));
        try
        {
            for (int i = 0; i < 8 && prev is not null; i++)
            {
                await Task.Delay(90, ct);
                var next = await Task.Run(() => Grab(rect));
                if (next is null) return prev;
                bool same = NearlySame(prev, next);
                prev.Dispose();
                prev = next;
                if (same) break;
            }
            return prev;
        }
        catch
        {
            prev?.Dispose(); // отменили посреди ожидания — кадр больше не нужен
            throw;
        }
    }

    /// <summary>Отличается меньше 1 % строк (по выборке пикселей) — страница стоит, мелкая анимация не в счёт.</summary>
    static bool NearlySame(SKBitmap a, SKBitmap b)
    {
        var pa = a.GetPixelSpan();
        var pb = b.GetPixelSpan();
        if (pa.Length != pb.Length) return false;
        int rowBytes = a.RowBytes, changed = 0;
        for (int y = 0; y < a.Height; y++)
        {
            int o = y * rowBytes;
            for (int x = 0; x < a.Width; x += 3)
            {
                int p = o + x * 4;
                if (Math.Abs(pa[p] - pb[p]) + Math.Abs(pa[p + 1] - pb[p + 1]) + Math.Abs(pa[p + 2] - pb[p + 2]) > 24) { changed++; break; }
            }
        }
        return changed <= a.Height / 100;
    }

    static SKBitmap? Grab(PixelRect rect)
    {
        using var bmp = PlatformServices.ScreenCapture.Capture(rect);
        return bmp is null ? null : Stitcher.ToSk(bmp);
    }

    static async Task RunAsync(PixelRect rect, double scaling, bool auto, CancellationToken ct)
    {
        await Task.Delay(250); // редактор успел закрыться
        SKBitmap? result = null;
        try
        {
            result = await CaptureAsync(rect, scaling, auto, ct, (f, h) => Dispatcher.UIThread.Post(() => _hud?.Progress(f, h)),
                () => Dispatcher.UIThread.Post(() => _hud?.SetManual()));
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            Toast.Show(L.T("Длинный скриншот не удался"), ex.Message);
        }
        finally
        {
            InputMonitor.Instance.KeyCombo -= OnKey;
            _keys?.Dispose();
            _keys = null;
            _hud?.Close();
            _hud = null;
            _cts = null;
        }
        if (result is null) return;
        byte[] png;
        using (result)
        using (var img = SKImage.FromBitmap(result))
        using (var data = img.Encode(SKEncodedImageFormat.Png, 100))
            png = data.ToArray();
        LongShotWindow.Open(png, scaling);
    }
}

/// <summary>Панель во время съёмки: кадры, высота, «Готово». В снимки не попадает.</summary>
public sealed class LongShotHud : Window
{
    readonly TextBlock _text = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 4, 0) }.Style("headline");
    readonly TextBlock _hint;
    public event Action? Done;

    /// <summary>Автопрокрутка не действует — просим крутить самому.</summary>
    public void SetManual() => _hint.Text = L.T("Прокручивай страницу — кадры снимаются сами");

    public LongShotHud(bool auto)
    {
        UiKit.MakeFloating(this);
        ShowActivated = false;
        Title = L.T("SnapMark — длинный скриншот");
        var done = UiKit.SoftButton(L.T("Готово"), () => Done?.Invoke(), primary: true);
        _hint = UiKit.Caption(auto
            ? L.T("Прокручиваю сам · двинь мышь — пауза · Esc — закончить")
            : L.T("Прокручивай страницу — кадры снимаются сами"));
        _hint.VerticalAlignment = VerticalAlignment.Center;
        _hint.TextWrapping = TextWrapping.NoWrap;
        var stack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { _text, _hint, done } };
        var card = new GlassPanel { Margin = new Thickness(16), Padding = new Thickness(6), Child = stack };
        card.Classes.Add("pill");
        card.Classes.Add("bar");
        Content = card;
        Progress(0, 0);
        Opened += (_, _) =>
        {
            PlatformServices.WindowHelper.ExcludeFromCapture(this);
            var screen = Screens.Primary;
            if (screen is null) return;
            double sc = screen.Scaling > 0 ? screen.Scaling : 1;
            Position = new PixelPoint(screen.WorkingArea.X + (screen.WorkingArea.Width - (int)(ClientSize.Width * sc)) / 2, screen.WorkingArea.Y + (int)(4 * sc));
        };
    }

    public void Progress(int frames, int height) =>
        _text.Text = frames == 0 ? L.T("Длинный скриншот…") : L.F("Кадров: {0} · {1} px", frames, height);
}

/// <summary>Результат: длинная картинка с прокруткой и действия — копировать, сохранить, закрепить.</summary>
public sealed class LongShotWindow : Window
{
    readonly byte[] _png;

    public static void Open(byte[] png, double scaling) => new LongShotWindow(png, scaling).Show();

    LongShotWindow(byte[] png, double scaling)
    {
        _png = png;
        Bitmap bmp;
        using (var ms = new MemoryStream(png)) bmp = new Bitmap(ms);
        Title = L.T("Длинный скриншот — SnapMark");
        if (App.Logo is not null) Icon = new WindowIcon(App.Logo);
        Classes.Add("sm");
        ExtendClientAreaToDecorationsHint = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        double w = bmp.PixelSize.Width / scaling, h = bmp.PixelSize.Height / scaling;
        Width = Math.Clamp(w + 48, 480, 1100);
        Height = 760;

        var title = new TextBlock { Text = L.T("Длинный скриншот") }.Style("pagetitle");
        var sub = UiKit.Caption($"{bmp.PixelSize.Width} × {bmp.PixelSize.Height} px");
        var header = new StackPanel { Margin = new Thickness(24, 40, 24, 10), Spacing = 2, Children = { title, sub } };

        var image = new Image { Source = bmp, Width = Math.Min(w, 1052), Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center };
        RenderOptions.SetBitmapInterpolationMode(image, BitmapInterpolationMode.HighQuality);
        var scroll = new ScrollViewer
        {
            Content = new Border
            {
                Child = image, CornerRadius = new CornerRadius(10), ClipToBounds = true, HorizontalAlignment = HorizontalAlignment.Center,
                BoxShadow = BoxShadows.Parse("0 6 20 0 #33000000"), Margin = new Thickness(0, 4, 0, 16),
            },
            Padding = new Thickness(24, 0),
        };

        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(24, 12),
            Children =
            {
                UiKit.SoftButton(L.T("Закрепить поверх окон"), () => { PinWindow.Pin(_png); }),
                UiKit.SoftButton(L.T("Сохранить как…"), () => _ = SaveAsync()),
                UiKit.SoftButton(L.T("Копировать"), () => _ = CopyAsync(), primary: true),
            },
        };
        var root = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(header);
        root.Children.Add(footer);
        root.Children.Add(scroll);
        Content = root;
    }

    async Task CopyAsync()
    {
        await SmartPaste.CopyPlainAsync(new ClipboardPayload { Png = _png, Dib = ImageEncoder.ToDib(_png) });
        Toast.Show(L.T("Скопировано"), L.T("Длинный скриншот в буфере обмена."));
    }

    async Task SaveAsync()
    {
        var s = AppServices.Settings;
        var ext = ImageEncoder.Extension(s.ImageFormat);
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = L.T("Сохранить скриншот"),
            SuggestedFileName = FileNaming.Build(s.FileNameTemplate, DateTime.Now, AppServices.PeekShotNumber()) + "_long." + ext,
            DefaultExtension = ext,
        });
        if (file is null) return;
        var path = file.TryGetLocalPath();
        var format = ImageEncoder.FromExtension(Path.GetExtension(path ?? file.Name)) ?? s.ImageFormat;
        await using (var stream = await file.OpenWriteAsync())
        {
            stream.SetLength(0);
            await stream.WriteAsync(ImageEncoder.Encode(_png, format, s.ImageQuality));
        }
        AppServices.NextShotNumber();
        if (path is not null) Toast.Show(L.T("Сохранено"), Path.GetFileName(path), L.T("Показать"), () => ShellOpen.ShowInFolder(path));
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape) Close();
        else if (e.Key == Key.C && (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0) _ = CopyAsync();
    }
}
