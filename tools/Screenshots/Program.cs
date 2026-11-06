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

    await Scene("editor", () => EditorScene(Page(), screen));
    await Scene("record", () => RecordScene(Page(), screen));
    await Scene("ocr", () => OcrScene(page));
    await Scene("steps", () => StepsScene(page));
    await Scene("longshot", () => LongShotScene());
    await Scene("trim", () => TrimScene());
    await Scene("settings", () => SettingsScene());
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
        ("#FF9500", new Point(1190, 842), new Point(1226, 758), "Pay button is barely visible"),
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
    double ox = 15.5, oy = 15.5;
    await Drag(w, new Point(ox + 88, oy + 84), new Point(ox + 150, oy + 165));
    await Save(w, "ocr");
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
    await Save(hud, "stephud");
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

async Task SettingsScene()
{
    SnapMark.Settings.SettingsWindow.ShowSingle(0);
    var w = (Window)typeof(SnapMark.Settings.SettingsWindow).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
    await Pump(800);
    await Save(w, "settings");
    SnapMark.Settings.SettingsWindow.ShowSingle(4);
    await Pump(800);
    await Save(w, "settings-video");
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
