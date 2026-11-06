using System.Diagnostics;
using System.Text;
using SkiaSharp;
using SnapMark.Core;

namespace SnapMark.Services;

/// <summary>
/// Распознавание текста на картинке — локально, без интернета:
///   • Windows — встроенный движок Windows.Media.Ocr (языки — из языков Windows; запускается через PowerShell,
///     чтобы программе не требовалась отдельная сборка под конкретную версию Windows SDK);
///   • macOS — Vision (VNRecognizeTextRequest) через JavaScript for Automation;
///   • Linux — tesseract, если установлен.
/// </summary>
public static class TextRecognizer
{
    public sealed class OcrException(string message) : Exception(message);

    /// <summary>Слово и его рамка в пикселях исходной картинки.</summary>
    public sealed record OcrWord(string Text, double X, double Y, double W, double H);

    public sealed class OcrLine
    {
        public List<OcrWord> Words { get; } = new();
        public string Text => Join(Words);
    }

    public sealed class OcrResult
    {
        public List<OcrLine> Lines { get; } = new();
        public string Text => string.Join(Environment.NewLine, Lines.Select(l => l.Text).Where(t => t.Length > 0));
        public bool IsEmpty => Lines.All(l => l.Words.Count == 0);
    }

    /// <summary>Слова через пробел; для китайского, японского и тайского — без пробелов.</summary>
    public static string Join(IEnumerable<OcrWord> words)
    {
        var sb = new StringBuilder();
        OcrWord? prev = null;
        foreach (var w in words)
        {
            if (prev is not null && !(IsCjk(prev.Text) && IsCjk(w.Text))) sb.Append(' ');
            sb.Append(w.Text);
            prev = w;
        }
        return sb.ToString();
    }

    static bool IsCjk(string s) => s.Length > 0 && s.All(c => c is >= '\u3040' and <= '\u30FF' or >= '\u3400' and <= '\u9FFF' or >= '\uAC00' and <= '\uD7AF' or >= '\u0E00' and <= '\u0E7F');

    public static async Task<OcrResult> RecognizeAsync(byte[] png)
    {
        var dir = Path.Combine(Path.GetTempPath(), "SnapMark");
        Directory.CreateDirectory(dir);
        var image = Path.Combine(dir, $"ocr_{Guid.NewGuid():N}.png");
        var (prepared, k, w, h) = Prepare(png);
        await File.WriteAllBytesAsync(image, prepared);
        try
        {
            OcrResult result;
            if (OperatingSystem.IsWindows()) result = ParseWords(await WindowsAsync(image, dir));
            else if (OperatingSystem.IsMacOS()) result = ParseMac(await MacAsync(image, dir), w * k, h * k);
            else result = ParseTesseract(await TesseractAsync(image));
            // Рамки — обратно в пиксели исходной картинки.
            if (Math.Abs(k - 1) > 0.001)
                foreach (var line in result.Lines)
                    for (int i = 0; i < line.Words.Count; i++)
                    {
                        var wd = line.Words[i];
                        line.Words[i] = wd with { X = wd.X / k, Y = wd.Y / k, W = wd.W / k, H = wd.H / k };
                    }
            result.Lines.RemoveAll(l => l.Words.Count == 0);
            return result;
        }
        finally
        {
            try { File.Delete(image); } catch { }
        }
    }

    /// <summary>Мелкий текст движки читают хуже — небольшие области увеличиваем в 2 раза; огромные — уменьшаем.</summary>
    static (byte[] Png, double Scale, int Width, int Height) Prepare(byte[] png)
    {
        try
        {
            using var bmp = SKBitmap.Decode(png);
            if (bmp is null) return (png, 1, 0, 0);
            double k = 1;
            if (Math.Max(bmp.Width, bmp.Height) < 1200) k = 2;
            if (Math.Max(bmp.Width, bmp.Height) * k > 8000) k = 8000.0 / Math.Max(bmp.Width, bmp.Height);
            if (Math.Abs(k - 1) < 0.01) return (png, 1, bmp.Width, bmp.Height);
            var info = new SKImageInfo((int)(bmp.Width * k), (int)(bmp.Height * k), SKColorType.Bgra8888, SKAlphaType.Premul);
            using var scaled = bmp.Resize(info, SKFilterQuality.High);
            if (scaled is null) return (png, 1, bmp.Width, bmp.Height);
            using var img = SKImage.FromBitmap(scaled);
            using var data = img.Encode(SKEncodedImageFormat.Png, 100);
            // Точный масштаб по ширине (размер округлён до целых пикселей).
            return (data.ToArray(), info.Width / (double)bmp.Width, bmp.Width, bmp.Height);
        }
        catch
        {
            return (png, 1, 0, 0);
        }
    }

    static double Num(string s) => double.Parse(s, System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Формат Windows-скрипта: «L» — новая строка, «W x y w h текст» — слово.</summary>
    static OcrResult ParseWords(string output)
    {
        var r = new OcrResult();
        OcrLine? line = null;
        foreach (var raw in output.Replace("\r", "").Split('\n'))
        {
            if (raw == "L") { line = new OcrLine(); r.Lines.Add(line); continue; }
            var p = raw.Split('\t');
            if (p.Length < 6 || p[0] != "W") continue;
            if (line is null) { line = new OcrLine(); r.Lines.Add(line); }
            var text = string.Join("\t", p.Skip(5));
            if (text.Length > 0) line.Words.Add(new OcrWord(text, Num(p[1]), Num(p[2]), Num(p[3]), Num(p[4])));
        }
        return r;
    }

    /// <summary>
    /// macOS Vision отдаёт строки с рамками в долях картинки (начало координат внизу слева).
    /// Слова внутри строки — по пробелам, ширина пропорциональна числу символов.
    /// </summary>
    static OcrResult ParseMac(string output, double imgW, double imgH)
    {
        var r = new OcrResult();
        foreach (var raw in output.Replace("\r", "").Split('\n'))
        {
            var p = raw.Split('\t');
            if (p.Length < 6 || p[0] != "N") continue;
            double x = Num(p[1]) * imgW, w = Num(p[3]) * imgW, h = Num(p[4]) * imgH;
            double y = (1 - Num(p[2]) - Num(p[4])) * imgH;
            var text = string.Join("\t", p.Skip(5)).Trim();
            if (text.Length == 0) continue;
            var line = new OcrLine();
            var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            double perChar = w / Math.Max(1, text.Length), cx = x;
            foreach (var part in parts)
            {
                double pw = perChar * part.Length;
                line.Words.Add(new OcrWord(part, cx, y, pw, h));
                cx += pw + perChar;
            }
            r.Lines.Add(line);
        }
        return r;
    }

    /// <summary>tesseract … tsv: уровень 5 — слова; строки — по (блок, абзац, строка).</summary>
    static OcrResult ParseTesseract(string tsv)
    {
        var r = new OcrResult();
        string? key = null;
        OcrLine? line = null;
        foreach (var raw in tsv.Replace("\r", "").Split('\n').Skip(1))
        {
            var p = raw.Split('\t');
            if (p.Length < 12 || p[0] != "5") continue;
            var text = p[11].Trim();
            if (text.Length == 0) continue;
            var k = $"{p[2]}.{p[3]}.{p[4]}";
            if (k != key) { key = k; line = new OcrLine(); r.Lines.Add(line); }
            line!.Words.Add(new OcrWord(text, Num(p[6]), Num(p[7]), Num(p[8]), Num(p[9])));
        }
        return r;
    }

    // ── Windows: Windows.Media.Ocr ─────────────────────────────────────────────
    const string PsScript = """
        param([string]$Path)
        $ErrorActionPreference = 'Stop'
        [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
        Add-Type -AssemblyName System.Runtime.WindowsRuntime
        $null = [Windows.Storage.StorageFile, Windows.Storage, ContentType = WindowsRuntime]
        $null = [Windows.Media.Ocr.OcrEngine, Windows.Foundation, ContentType = WindowsRuntime]
        $null = [Windows.Graphics.Imaging.BitmapDecoder, Windows.Graphics, ContentType = WindowsRuntime]
        $asTask = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
            $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' })[0]
        function Await($op, [Type]$type) {
            $task = $asTask.MakeGenericMethod($type).Invoke($null, @($op))
            $null = $task.Wait(-1)
            $task.Result
        }
        $engine = [Windows.Media.Ocr.OcrEngine]::TryCreateFromUserProfileLanguages()
        if ($engine -eq $null) {
            $lang = [Windows.Media.Ocr.OcrEngine]::AvailableRecognizerLanguages | Select-Object -First 1
            if ($lang -ne $null) { $engine = [Windows.Media.Ocr.OcrEngine]::TryCreateFromLanguage($lang) }
        }
        if ($engine -eq $null) { [Console]::Error.WriteLine('NO_LANGUAGES'); exit 3 }
        $file = Await ([Windows.Storage.StorageFile]::GetFileFromPathAsync($Path)) ([Windows.Storage.StorageFile])
        $stream = Await ($file.OpenAsync([Windows.Storage.FileAccessMode]::Read)) ([Windows.Storage.Streams.IRandomAccessStream])
        $decoder = Await ([Windows.Graphics.Imaging.BitmapDecoder]::CreateAsync($stream)) ([Windows.Graphics.Imaging.BitmapDecoder])
        $bitmap = Await ($decoder.GetSoftwareBitmapAsync()) ([Windows.Graphics.Imaging.SoftwareBitmap])
        $result = Await ($engine.RecognizeAsync($bitmap)) ([Windows.Media.Ocr.OcrResult])
        $inv = [System.Globalization.CultureInfo]::InvariantCulture
        foreach ($line in $result.Lines) {
            [Console]::Out.WriteLine('L')
            foreach ($w in $line.Words) {
                $r = $w.BoundingRect
                [Console]::Out.WriteLine(('W' + "`t" + $r.X.ToString($inv) + "`t" + $r.Y.ToString($inv) + "`t" + $r.Width.ToString($inv) + "`t" + $r.Height.ToString($inv) + "`t" + $w.Text))
            }
        }
        $stream.Dispose()
        """;

    static async Task<string> WindowsAsync(string image, string dir)
    {
        var script = Path.Combine(dir, "ocr.ps1");
        await File.WriteAllTextAsync(script, PsScript, new UTF8Encoding(true));
        var (code, stdout, stderr) = await RunAsync("powershell.exe",
            new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, image });
        if (code == 3 || stderr.Contains("NO_LANGUAGES"))
            throw new OcrException(L.T("В Windows не установлен ни один язык распознавания. Параметры → Время и язык → Язык → добавь язык (вместе с ним ставится распознавание текста)."));
        if (code != 0) throw new OcrException(FirstLine(stderr) ?? L.T("Распознавание текста не сработало."));
        return stdout;
    }

    // ── macOS: Vision ─────────────────────────────────────────────────────────
    const string JxaScript = """
        ObjC.import('Foundation');
        ObjC.import('Vision');
        function run(argv) {
          var url = $.NSURL.fileURLWithPath(argv[0]);
          var req = $.VNRecognizeTextRequest.alloc.init;
          req.recognitionLevel = 0;
          req.usesLanguageCorrection = true;
          try { req.automaticallyDetectsLanguage = true; } catch (e) {}
          var handler = $.VNImageRequestHandler.alloc.initWithURLOptions(url, $.NSDictionary.dictionary);
          handler.performRequestsError($.NSArray.arrayWithObject(req), null);
          var results = req.results;
          var out = [];
          for (var i = 0; i < results.count; i++) {
            var o = results.objectAtIndex(i);
            var c = o.topCandidates(1);
            if (c.count == 0) continue;
            var b = o.boundingBox;
            out.push(['N', b.origin.x, b.origin.y, b.size.width, b.size.height, c.objectAtIndex(0).string.js].join('\t'));
          }
          return out.join('\n');
        }
        """;

    static async Task<string> MacAsync(string image, string dir)
    {
        var script = Path.Combine(dir, "ocr.js");
        await File.WriteAllTextAsync(script, JxaScript);
        var (code, stdout, stderr) = await RunAsync("osascript", new[] { "-l", "JavaScript", script, image });
        if (code != 0) throw new OcrException(FirstLine(stderr) ?? L.T("Распознавание текста не сработало."));
        return stdout;
    }

    // ── Linux: tesseract ──────────────────────────────────────────────────────
    static async Task<string> TesseractAsync(string image)
    {
        string? langs = null;
        try
        {
            var (_, list, _) = await RunAsync("tesseract", new[] { "--list-langs" });
            var have = list.Split('\n').Select(l => l.Trim()).ToHashSet();
            var wanted = new[] { "eng", "rus", "deu", "fra", "ita", "spa" }.Where(have.Contains).ToList();
            if (wanted.Count > 0) langs = string.Join("+", wanted);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new OcrException(L.T("Для распознавания текста установи tesseract: sudo apt install tesseract-ocr tesseract-ocr-rus"));
        }
        var args = new List<string> { image, "stdout" };
        if (langs is not null) { args.Add("-l"); args.Add(langs); }
        args.Add("tsv");
        var (code, stdout, stderr) = await RunAsync("tesseract", args);
        if (code != 0) throw new OcrException(FirstLine(stderr) ?? L.T("Распознавание текста не сработало."));
        return stdout;
    }

    static string? FirstLine(string s) =>
        s.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);

    static async Task<(int Code, string Out, string Err)> RunAsync(string exe, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new OcrException(L.T("Распознавание текста не сработало."));
        var outTask = p.StandardOutput.ReadToEndAsync();
        var errTask = p.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch { }
            throw new OcrException(L.T("Распознавание текста заняло слишком много времени."));
        }
        return (p.ExitCode, await outTask, await errTask);
    }
}
