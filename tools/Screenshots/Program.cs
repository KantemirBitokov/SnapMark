using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Avalonia.Platform;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using SnapMark.Annotations;
using SnapMark.Capture;
using SnapMark.Core;
using SnapMark.Export;
using SnapMark.Services;
using SnapMark.UI;

// Скриншоты для README и сайта: настоящие окна SnapMark, отрисованные без экрана (Avalonia Headless + Skia),
// сцены проигрываются «руками» — мышью и клавиатурой Headless. Сырые кадры + позиции окон → out/, композиция — отдельно.
var outDir = args.Length > 0 ? args[0] : "out";
var assets = args.Length > 1 ? args[1] : ".";
var video = args.Length > 2 ? args[2] : null;
Directory.CreateDirectory(outDir);

AppBuilder.Configure<ShotsApp>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();

var meta = new Dictionary<string, object>();
var done = new CancellationTokenSource();
Dispatcher.UIThread.Post(async () =>
{
    try { await RunAll(); }
    catch (Exception ex) { Console.WriteLine("FATAL " + ex); }
    finally
    {
        File.WriteAllText(Path.Combine(outDir, "meta.json"), JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true }));
        done.Cancel();
    }
});
Dispatcher.UIThread.MainLoop(done.Token);
return 0;

async Task RunAll()
{
    var s = AppServices.Settings;
    s.CommentsOnImage = true;
    s.CommentSize = 18;
    s.Language = "en";
    Glass.Transparency = 0.5;
    Glass.Frost = 0.45;
    // Свой снимок для каждой сцены: редактор при закрытии освобождает свой.
    Bitmap Page() => new(Path.Combine(assets, "page.png"));
    var page = Page();
    var screen = new Screen(1.0, new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1040), true);

    await Scene("markers-grid", () => MarkersGrid());
    await Scene("pin", () => PinScene(page));
    if (Environment.GetEnvironmentVariable("SNAPMARK_SHOTS_ONLY") == "markers") return;
    await Scene("rotate-emoji", () => RotateEmojiScene(Page(), screen));
    await Scene("editor", () => EditorScene(Page(), screen));
    await Scene("record", () => RecordScene(Page(), screen));
    await Scene("ocr", () => OcrScene(page));
    await Scene("ocr-small", () => OcrSmallScene(page, screen));
    await Scene("steps", () => StepsScene(page));
    await Scene("longshot", () => LongShotScene());
    await Scene("trim", () => TrimScene());
    await Scene("settings", () => SettingsScene());
    if (Environment.GetEnvironmentVariable("SNAPMARK_SHOTS_LAYOUT") == "1")
        await Scene("layout", () => LayoutScene(Page(), screen));
}

// Все формы меток: стекло и плоские, на светлом и тёмном фоне, два размера.
Task MarkersGrid()
{
    const int W = 1500, H = 1180;
    var bg = new RenderTargetBitmap(new PixelSize(W, H));
    using (var dc = bg.CreateDrawingContext())
    {
        dc.FillRectangle(new SolidColorBrush(Color.Parse("#F2F2F7")), new Rect(0, 0, W, H / 2));
        dc.FillRectangle(new SolidColorBrush(Color.Parse("#1C1C1E")), new Rect(0, H / 2, W, H / 2));
    }
    var items = new List<Annotation>();
    var shapes = new[] { MarkerShape.Circle, MarkerShape.RoundedSquare, MarkerShape.Diamond, MarkerShape.Pin };
    var colors = new[] { "#FF3B30", "#0A84FF", "#34C759", "#FFCC00", "#FFFFFF", "#AF52DE" };
    for (int half = 0; half < 2; half++)
        for (int g = 0; g < 2; g++)
        {
            bool glass = g == 0;
            double y0 = half * H / 2 + 70 + g * 270;
            // Ряд 1: обычный размер, все формы × цвета.
            int i = 0;
            foreach (var shape in shapes)
                for (int c = 0; c < 3; c++, i++)
                    items.Add(new MarkerAnnotation
                    {
                        Label = i % 4 == 3 ? "12" : AnnotationDocument.IndexToLabel(i), Shape = shape, Glass = glass,
                        Color = Color.Parse(colors[(i + half * 3) % colors.Length]), FontSize = 28,
                        Center = new Point(60 + i * 62, y0),
                    });
            // Ряд 2: крупно.
            for (int k = 0; k < 4; k++)
                items.Add(new MarkerAnnotation
                {
                    Label = k == 3 ? "7" : AnnotationDocument.IndexToLabel(k), Shape = shapes[k], Glass = glass,
                    Color = Color.Parse(colors[k == 0 ? 0 : k == 1 ? 1 : k == 2 ? 5 : 2]), FontSize = 64,
                    Center = new Point(110 + k * 170, y0 + 140),
                    Tail = k == 0 ? new Point(110 + k * 170 + 90, y0 + 200) : null,
                });
            items.Add(new MarkerAnnotation
            {
                Label = "B", Shape = MarkerShape.Circle, Glass = glass, Color = Color.Parse("#FF9500"), FontSize = 40,
                Center = new Point(900, y0 + 120), Tail = new Point(1050, y0 + 60),
            });
        }
    using var r = ImageRenderer.Render(bg, new PixelRect(0, 0, W, H), items);
    File.WriteAllBytes(Path.Combine(outDir, "markers-grid.png"), ImageEncoder.ToPng(r));
    return Task.CompletedTask;
}

async Task Scene(string name, Func<Task> run)
{
    try
    {
        await run();
        Console.WriteLine("scene ok: " + name);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"scene FAILED: {name}: {ex}");
    }
}

// ── Помощники ───────────────────────────────────────────────────────────────
async Task Pump(int ms)
{
    var until = DateTime.UtcNow.AddMilliseconds(ms);
    while (DateTime.UtcNow < until)
    {
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        await Task.Delay(16);
    }
    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
    await Task.Delay(30);
}

async Task Save(TopLevel w, string name)
{
    await Pump(250);
    var frame = w.CaptureRenderedFrame();
    frame?.Save(Path.Combine(outDir, name + ".png"));
    var pos = w is Window win ? win.Position : default;
    meta[name] = new { x = pos.X, y = pos.Y, w = w.ClientSize.Width, h = w.ClientSize.Height };
    Console.WriteLine($"saved {name} {w.ClientSize.Width}x{w.ClientSize.Height} at {pos}");
}

// Окна с прозрачным фоном: снимок на чёрном и на белом — по разнице потом восстанавливается альфа.
async Task SaveMatte(Window w, string name)
{
    var old = w.Background;
    foreach (var (brush, suffix) in new[] { (Brushes.Black, ".k"), (Brushes.White, ".w") })
    {
        w.Background = brush;
        await Pump(150);
        w.CaptureRenderedFrame()?.Save(Path.Combine(outDir, name + suffix + ".png"));
    }
    w.Background = old;
    await Save(w, name);
}

void Click(Control c) => c.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

Button ToolButton(OverlayWindow ov, string tag) =>
    ov.ToolButtons.Children.Concat(ov.ToolButtons2.Children).OfType<Button>().First(b => (b.Tag as string) == tag);

async Task Drag(TopLevel w, Point a, Point b)
{
    w.MouseMove(a);
    w.MouseDown(a, MouseButton.Left);
    for (int i = 1; i <= 8; i++)
    {
        w.MouseMove(new Point(a.X + (b.X - a.X) * i / 8, a.Y + (b.Y - a.Y) * i / 8));
        await Pump(16);
    }
    w.MouseUp(b, MouseButton.Left);
    await Pump(120);
}

async Task ClickAt(TopLevel w, Point p)
{
    w.MouseMove(p);
    w.MouseDown(p, MouseButton.Left);
    w.MouseUp(p, MouseButton.Left);
    await Pump(150);
}

/// <summary>Фон стекла плавающего окна — кусок картинки под ним (на экране это делает «живой» снимок).</summary>
void Backdrop(Window w, Bitmap under, PixelPoint? at = null)
{
    var p = at ?? w.Position;
    int cw = (int)w.ClientSize.Width, ch = (int)w.ClientSize.Height;
    var rtb = new RenderTargetBitmap(new PixelSize(Math.Max(1, cw), Math.Max(1, ch)), new Vector(96, 96));
    using (var ctx = rtb.CreateDrawingContext())
    {
        ctx.FillRectangle(Brushes.White, new Rect(0, 0, cw, ch));
        ctx.DrawImage(under, new Rect(p.X, p.Y, cw, ch), new Rect(0, 0, cw, ch));
    }
    Glass.SetStaticBackdrop(w, rtb);
}

static T Create<T>(params object?[] args) where T : class =>
    (T)Activator.CreateInstance(typeof(T), BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null, args, null)!;

static object? CallPrivate(object target, string method, params object?[] args) =>
    target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.Invoke(target, args);

// ── Сцены ───────────────────────────────────────────────────────────────────
async Task EditorScene(Bitmap page, Screen screen)
{
    var ov = new OverlayWindow(screen, page, CaptureMode.Region) { Width = 1920, Height = 1080 };
    ov.Show();
    await Pump(400);
    ov.Surface.SetSelection(new PixelRect(404, 82, 1396, 838));
    await Pump(300);

    // Метки со стрелками и комментарии — как пользователь: инструмент «Метка со стрелкой», тянем к цели, печатаем.
    var marks = new (string Color, Point Center, Point Tail, string Comment)[]
    {
        ("#FF3B30", new Point(690, 118), new Point(586, 118), "Typo in the heading"),
        ("#FF9500", new Point(1190, 842), new Point(1236, 742), "Pay button is barely visible"),
        ("#0A84FF", new Point(1548, 660), new Point(1478, 660), "Total should be $30.50"),
        ("#34C759", new Point(1612, 523), new Point(1570, 523), "Overflows the card"),
    };
    foreach (var (color, center, tail, comment) in marks)
    {
        ov.Surface.DrawStyle.Color = Color.Parse(color);
        Click(ToolButton(ov, "MarkerArrow"));
        await Pump(80);
        await Drag(ov, center, tail);
        await Pump(200);
        ov.KeyTextInput(comment);
        await Pump(150);
    }

    // Скрыть номер карты: инструмент «Скрыть», режим «пиксели».
    ov.Surface.RedactMode = RedactMode.Pixelate;
    Click(ToolButton(ov, "Redact"));
    await Pump(80);
    await Drag(ov, new Point(452, 722), new Point(716, 766));

    // Чистый вид: инструмент «Выбор», без выделенного объекта.
    Click(ToolButton(ov, "Select"));
    ov.Surface.ClearSelectedAnnotation();
    await Pump(300);
    await Save(ov, "editor");

    // Панель стиля выбранной метки.
    await ClickAt(ov, marks[0].Center);
    await Pump(400);
    await Save(ov, "editor-style");
    ov.Close();
}

// Поворот (ручкой над рамкой, Shift — шаг 15°) и эмодзи: библиотека, стикер на картинке, эмодзи внутри текста.
async Task RotateEmojiScene(Bitmap page, Screen screen)
{
    var ov = new OverlayWindow(screen, page, CaptureMode.Region) { Width = 1920, Height = 1080 };
    ov.Show();
    await Pump(400);
    ov.Surface.SetSelection(new PixelRect(404, 82, 1396, 838));
    await Pump(300);
    var log = new List<string>();

    // 1. Текст с эмодзи, набранный с клавиатуры.
    ov.Surface.DrawStyle.Color = Color.Parse("#FF3B30");
    Click(ToolButton(ov, "Text"));
    await Pump(80);
    await ClickAt(ov, new Point(1000, 300));
    await Pump(200);
    ov.KeyTextInput("Ready to ship 🚀");
    await Pump(150);
    // 2. Эмодзи из библиотеки — в позицию курсора (в конец).
    Click(ov.FindControl<Button>("EmojiButton")!);
    await Pump(300);
    await Save(ov, "emoji-picker");
    var picker = ov.FindControl<EmojiPicker>("EmojiPickerControl")!;
    var firstEmoji = picker.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("emoji"));
    Click(firstEmoji);
    await Pump(300);
    var text = ov.Surface.Document.Items.OfType<TextAnnotation>().FirstOrDefault();
    log.Add($"text: '{text?.Text}'");

    // 3. Повернуть текст ручкой с Shift: тянем к ~37° — должно встать ровно на 30° или 45°.
    ov.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
    await Pump(150);
    Click(ToolButton(ov, "Select"));
    await Pump(80);
    if (text is not null)
    {
        await ClickAt(ov, text.Bounds.Center);
        await Pump(200);
        var c = text.Corners(5);
        var top = new Point((c[0].X + c[1].X) / 2, (c[0].Y + c[1].Y) / 2 - 24);
        var pivot = text.Pivot;
        double r = 140, a = 37 * Math.PI / 180;
        var target = new Point(pivot.X + Math.Sin(a) * r, pivot.Y - Math.Cos(a) * r);
        ov.MouseMove(top);
        ov.MouseDown(top, MouseButton.Left, RawInputModifiers.Shift);
        for (int i = 1; i <= 10; i++)
        {
            ov.MouseMove(new Point(top.X + (target.X - top.X) * i / 10, top.Y + (target.Y - top.Y) * i / 10), RawInputModifiers.Shift);
            await Pump(16);
        }
        await Save(ov, "rotate-dragging");
        ov.MouseUp(target, MouseButton.Left, RawInputModifiers.Shift);
        await Pump(200);
        log.Add($"rotation with Shift: {text.Rotation}");
    }

    // 4. Повёрнутые фигуры и стикеры.
    ov.Surface.AddAnnotation(new RectAnnotation { Start = new Point(560, 520), End = new Point(820, 640), Color = Color.Parse("#0A84FF"), Thickness = 5, Rotation = -12 });
    ov.Surface.AddAnnotation(new LineAnnotation { Start = new Point(1250, 560), End = new Point(1450, 560), ArrowEnd = true, Color = Color.Parse("#34C759"), Thickness = 6, Rotation = 30 });
    ov.Surface.ClearSelectedAnnotation();
    Click(ov.FindControl<Button>("EmojiButton")!); // закрыть
    await Pump(100);
    Click(ov.FindControl<Button>("EmojiButton")!); // открыть заново: теперь сверху «Недавние»
    await Pump(200);
    var recent = picker.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("emoji")).Select(b => b.Tag as string).ToList();
    log.Add($"recent: {string.Join(" ", recent)}");
    // Поиск по-русски.
    var search = picker.GetVisualDescendants().OfType<TextBox>().First();
    search.Text = "огонь";
    await Pump(200);
    var found = picker.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("emoji")).Select(b => b.Tag as string).ToList();
    log.Add($"search 'огонь': {string.Join(" ", found)}");
    if (found.Count > 0) Click(picker.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("emoji")));
    await Pump(200);
    var sticker = ov.Surface.SelectedAnnotation as TextAnnotation;
    log.Add($"sticker: '{sticker?.Text}' size {sticker?.FontSize}");
    if (sticker is not null) sticker.Rotation = -15;
    await Pump(200);
    await Save(ov, "rotate-emoji");

    // Экспорт: повороты и цветные эмодзи должны попасть в картинку.
    var sel = ov.Surface.Selection!.Value;
    using var rendered = ImageRenderer.Render(page, sel, ov.Surface.Document.Items);
    File.WriteAllBytes(Path.Combine(outDir, "rotate-emoji-export.png"), ImageEncoder.ToPng(rendered));
    foreach (var l in log) Console.WriteLine("rotate-emoji: " + l);
    ov.Close();
}

async Task RecordScene(Bitmap page, Screen screen)
{
    var ov = new OverlayWindow(screen, page, CaptureMode.VideoRegion) { Width = 1920, Height = 1080 };
    ov.Show();
    await Pump(400);
    ov.Surface.SetSelection(new PixelRect(404, 82, 1186, 748));
    await Pump(300);
    Click(ov.VideoOptionsButton);
    await Pump(1500);
    await Save(ov, "record");
    ov.Close();
}

// Где в окне распознавания начинается сама картинка (у маленького фрагмента окно шире картинки).
static Point ImageOrigin(Window w)
{
    var layer = w.GetVisualDescendants().OfType<Control>().First(c => c.GetType().Name == "TextLayer");
    return layer.TranslatePoint(new Point(0, 0), w) ?? new Point(14, 14);
}

// Маленький фрагмент у правого края экрана: картинка должна остаться на своём месте, без чёрных полей,
// а панель с кнопками — сдвинуться влево, чтобы влезть на экран.
async Task OcrSmallScene(Bitmap page, Screen screen)
{
    typeof(global::SnapMark.App).GetField("TestScreens", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, new[] { screen });
    var wa = screen.WorkingArea;
    var at = new PixelRect(wa.Right - 190, 300, 180, 110);
    var rtb = new RenderTargetBitmap(new PixelSize(at.Width, at.Height), new Vector(96, 96));
    using (var ctx = rtb.CreateDrawingContext())
        ctx.DrawImage(page, new Rect(1100, 200, at.Width, at.Height), new Rect(0, 0, at.Width, at.Height));
    var png = ImageEncoder.ToPng(rtb);
    var w = Create<OcrWindow>(png, at);
    w.Show();
    await Pump(500);
    await (Task)CallPrivate(w, "RecognizeAsync", png)!;
    await Pump(400);
    var o = ImageOrigin(w);
    var imageScreen = new PixelPoint(w.Position.X + (int)Math.Round(o.X * w.RenderScaling), w.Position.Y + (int)Math.Round(o.Y * w.RenderScaling));
    var right = w.Position.X + (int)Math.Round(w.Bounds.Width * w.RenderScaling);
    Console.WriteLine($"ocr-small: image at {imageScreen} (expected {at.Position}), window right {right} (screen right {wa.Right}) -> " +
        (Math.Abs(imageScreen.X - at.X) <= 1 && Math.Abs(imageScreen.Y - at.Y) <= 1 && right <= wa.Right + 14 ? "ok" : "FAIL")); // справа за краем — только прозрачное поле под тень
    await SaveMatte(w, "ocr-small");
    w.Close();
    typeof(global::SnapMark.App).GetField("TestScreens", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, null);
}

async Task OcrScene(Bitmap page)
{
    var at = new PixelRect(1062, 186, 430, 300);
    var rtb = new RenderTargetBitmap(new PixelSize(at.Width, at.Height), new Vector(96, 96));
    using (var ctx = rtb.CreateDrawingContext())
        ctx.DrawImage(page, new Rect(at.X, at.Y, at.Width, at.Height), new Rect(0, 0, at.Width, at.Height));
    var png = ImageEncoder.ToPng(rtb);
    var w = Create<OcrWindow>(png, at);
    w.Show();
    await Pump(500);
    Backdrop(w, page);
    await (Task)CallPrivate(w, "RecognizeAsync", png)!;
    await Pump(300);
    // Выделить «Ethiopia Yirgacheffe … Oat milk» протяжкой (картинка внутри окна сдвинута на поле 14 + рамку).
    var layerPos = ImageOrigin(w);
    double ox = layerPos.X + 1.5, oy = layerPos.Y + 1.5;
    await Drag(w, new Point(ox + 88, oy + 84), new Point(ox + 150, oy + 165));
    await SaveMatte(w, "ocr");
    w.Close();
}

// «Закрепить поверх окон»: окно с картинкой (для проверки окантовки по углам — кадр на чёрном фоне).
async Task PinScene(Bitmap page)
{
    var at = new PixelRect(420, 90, 600, 360);
    var rtb = new RenderTargetBitmap(new PixelSize(at.Width, at.Height), new Vector(96, 96));
    using (var ctx = rtb.CreateDrawingContext())
    {
        ctx.DrawImage(page, new Rect(at.X, at.Y, at.Width, at.Height), new Rect(0, 0, at.Width, at.Height));
        // Тёмная картинка — на ней видна светлая окантовка, как на скриншоте тёмного сайта.
        ctx.FillRectangle(new SolidColorBrush(Color.FromArgb(235, 0, 0, 0)), new Rect(0, 0, at.Width, at.Height));
    }
    PinWindow.Pin(ImageEncoder.ToPng(rtb), at);
    await Pump(500);
    var open = (System.Collections.IList)typeof(PinWindow).GetField("Open", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!.GetValue(null)!;
    var w = (Window)open[open.Count - 1]!;
    await SaveMatte(w, "pin");
    w.Close();
}

async Task StepsScene(Bitmap page)
{
    // Три шага — так же, как их делает пошаговая инструкция (кольцо на месте клика и метка со стрелкой).
    var clicks = new (Point P, string Comment)[]
    {
        (new Point(583, 290), "Click: Brewly — Checkout · Full name"),
        (new Point(866, 596), "Click: Brewly — Checkout · Pickup"),
        (new Point(1272, 725), "Click: Brewly — Checkout · Pay"),
    };
    AppServices.Series.Clear();
    for (int i = 0; i < clicks.Length; i++)
    {
        var (p, comment) = clicks[i];
        var color = Color.Parse("#FF3B30");
        var ring = new EllipseAnnotation { Start = new Point(p.X - 17, p.Y - 17), End = new Point(p.X + 17, p.Y + 17), Color = color, Thickness = 3.5 };
        var label = AnnotationDocument.IndexToLabel(i);
        var m = new MarkerAnnotation { Label = label, Center = new Point(p.X - 78, p.Y - 78), Tail = new Point(p.X - 13, p.Y - 13), Color = color, FontSize = 28 };
        using var r = ImageRenderer.Render(page, new PixelRect(0, 0, 1920, 1080), new Annotation[] { ring, m });
        AppServices.Series.Add(ImageEncoder.ToPng(r), 1920, 1080, new List<SeriesMarker> { new() { Label = label, Comment = comment } });
    }
    SeriesWindow.ShowSingle();
    var sw = (Window)typeof(SeriesWindow).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
    sw.Width = 900;
    sw.Height = 760;
    await Pump(800);
    await Save(sw, "series");
    sw.Close();

    // Панель режима «Шаги».
    typeof(SnapMark.Steps.StepRecorder).GetField("_steps", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, 3);
    var hud = new SnapMark.Steps.StepHud();
    hud.Show();
    await Pump(300);
    hud.Sync();
    hud.Position = new PixelPoint((1920 - (int)hud.ClientSize.Width) / 2, 4);
    Backdrop(hud, page);
    await Pump(300);
    await SaveMatte(hud, "stephud");
    hud.Close();
}

async Task LongShotScene()
{
    var png = File.ReadAllBytes(Path.Combine(assets, "page-long.png"));
    var w = Create<LongShotWindow>(png, 1.0);
    w.Show();
    await Pump(800);
    await Save(w, "longshot");
    w.Close();
}

async Task TrimScene()
{
    if (video is null || !File.Exists(video)) { Console.WriteLine("no video"); return; }
    var w = Create<SnapMark.Recording.TrimWindow>();
    w.Show();
    await (Task)CallPrivate(w, "LoadAsync", video)!;
    await Pump(6000);
    // Отрезок примерно с 1 по 4 секунду — как будто ручки уже подвинули.
    var tl = w.GetType().GetField("_timeline", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(w)!;
    tl.GetType().GetMethod("SetRange")!.Invoke(tl, new object[] { 1.2, 4.1 });
    CallPrivate(w, "OnRangeChanged");
    CallPrivate(w, "ShowFrame", 2.4);
    await Pump(1500);
    await Save(w, "trim");
    w.Close();
}

// Раскладка панелей редактора при разных выделениях: панель комментариев не должна закрывать панели действий,
// инструментов и стиля. Пишет «layout <имя>: ok|OVERLAP …» и снимок для каждого случая.
async Task LayoutScene(Bitmap page, Screen screen)
{
    var cases = new (string Name, PixelRect Sel)[]
    {
        ("full", new PixelRect(0, 0, 1920, 1080)),
        ("right-edge", new PixelRect(900, 60, 1020, 900)),
        ("left-edge", new PixelRect(0, 60, 1000, 900)),
        ("center-small", new PixelRect(860, 480, 200, 120)),
        ("top-wide", new PixelRect(0, 0, 1920, 400)),
        ("bottom-wide", new PixelRect(0, 680, 1920, 400)),
        ("tall-right", new PixelRect(1500, 0, 420, 1080)),
    };
    foreach (var (name, sel) in cases)
    {
        var ov = new OverlayWindow(screen, page, CaptureMode.Region) { Width = 1920, Height = 1080 };
        ov.Show();
        await Pump(300);
        ov.Surface.SetSelection(sel);
        await Pump(200);
        var c = new Point(sel.X + sel.Width / 2.0, sel.Y + sel.Height / 2.0);
        Click(ToolButton(ov, "MarkerArrow"));
        await Pump(80);
        await Drag(ov, c, new Point(c.X - 40, c.Y + 20));
        await Pump(200);
        ov.KeyTextInput("Comment");
        await Pump(300);
        Rect B(string n) { var x = ov.FindControl<Control>(n); return x is { IsVisible: true } ? x.Bounds : default; }
        var comments = B("CommentsPanel");
        var others = new[] { ("actions", B("ActionsBar")), ("tools", B("ToolsBar")), ("style", B("StylePanel")) };
        var hits = others.Where(o => o.Item2.Width > 0 && comments.Width > 0 && comments.Intersects(o.Item2)).Select(o => o.Item1).ToList();
        bool inside = comments.Width > 0 && comments.X >= 0 && comments.Y >= 0 && comments.Right <= 1920 && comments.Bottom <= 1080;
        Console.WriteLine($"layout {name}: {(hits.Count == 0 && inside ? "ok" : "OVERLAP " + string.Join(",", hits) + (inside ? "" : " offscreen"))} comments={comments}");
        await Save(ov, "layout-" + name);
        ov.Close();
        page = new Bitmap(Path.Combine(assets, "page.png"));
    }
}

async Task SettingsScene()
{
    SnapMark.Settings.SettingsWindow.ShowSingle(0);
    var w = (Window)typeof(SnapMark.Settings.SettingsWindow).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
    await Pump(800);
    await Save(w, "settings");
    SnapMark.Settings.SettingsWindow.ShowSingle(4);
    await Pump(800);
    await Save(w, "settings-video");
    // Для проверки сборок — все страницы настроек.
    if (Environment.GetEnvironmentVariable("SNAPMARK_SHOTS_ALLPAGES") == "1")
        for (int pageIndex = 0; pageIndex < 6; pageIndex++)
        {
            SnapMark.Settings.SettingsWindow.ShowSingle(pageIndex);
            await Pump(800);
            await Save(w, $"settings-p{pageIndex}");
        }
    w.Close();
}

class ShotsApp : Application
{
    public override void Initialize()
    {
        RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;
        Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://SnapMark/")) { Source = new Uri("avares://SnapMark/Styles/Colors.axaml") });
        Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://SnapMark/")) { Source = new Uri("avares://SnapMark/Styles/Icons.axaml") });
        Styles.Add(new FluentTheme());
        Styles.Add(new StyleInclude(new Uri("avares://SnapMark/")) { Source = new Uri("avares://SnapMark/Styles/MacStyles.axaml") });
    }
}
