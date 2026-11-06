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

    public static async Task<string> RecognizeAsync(byte[] png)
    {
        var dir = Path.Combine(Path.GetTempPath(), "SnapMark");
        Directory.CreateDirectory(dir);
        var image = Path.Combine(dir, $"ocr_{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(image, Prepare(png));
        try
        {
            string text;
            if (OperatingSystem.IsWindows()) text = await WindowsAsync(image, dir);
            else if (OperatingSystem.IsMacOS()) text = await MacAsync(image, dir);
            else text = await TesseractAsync(image);
            return Clean(text);
        }
        finally
        {
            try { File.Delete(image); } catch { }
        }
    }

    /// <summary>Мелкий текст движки читают хуже — небольшие области увеличиваем в 2 раза; огромные — уменьшаем.</summary>
    static byte[] Prepare(byte[] png)
    {
        try
        {
            using var bmp = SKBitmap.Decode(png);
            if (bmp is null) return png;
            double k = 1;
            if (Math.Max(bmp.Width, bmp.Height) < 1200) k = 2;
            if (Math.Max(bmp.Width, bmp.Height) * k > 8000) k = 8000.0 / Math.Max(bmp.Width, bmp.Height);
            if (Math.Abs(k - 1) < 0.01) return png;
            var info = new SKImageInfo((int)(bmp.Width * k), (int)(bmp.Height * k), SKColorType.Bgra8888, SKAlphaType.Premul);
            using var scaled = bmp.Resize(info, SKFilterQuality.High);
            if (scaled is null) return png;
            using var img = SKImage.FromBitmap(scaled);
            using var data = img.Encode(SKEncodedImageFormat.Png, 100);
            return data.ToArray();
        }
        catch
        {
            return png;
        }
    }

    static string Clean(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n').Select(l => l.TrimEnd()).ToList();
        while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        while (lines.Count > 0 && lines[0].Length == 0) lines.RemoveAt(0);
        return string.Join(Environment.NewLine, lines);
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
        foreach ($line in $result.Lines) { [Console]::Out.WriteLine($line.Text) }
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
            var c = results.objectAtIndex(i).topCandidates(1);
            if (c.count > 0) out.push(c.objectAtIndex(0).string.js);
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
