using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using SnapMark.Core;
using SnapMark.Platform.Common;
using SnapMark.Services;

namespace SnapMark.Recording;

/// <summary>Обрезка видео и экспорт в GIF через ffmpeg (тот же, что пишет видео).</summary>
public static class VideoTools
{
    public static string? Ffmpeg => FfmpegRecorder.Resolve(AppServices.Settings.FfmpegPath);

    /// <summary>Длительность в секундах (из «Duration: 00:00:12.34» в выводе ffmpeg) и размер кадра.</summary>
    public static async Task<(double Seconds, int Width, int Height)> ProbeAsync(string file)
    {
        var ffmpeg = Ffmpeg ?? throw new InvalidOperationException(L.T("Для этого нужен ffmpeg (Настройки → Видео)."));
        var (_, _, err) = await RunAsync(ffmpeg, new[] { "-hide_banner", "-i", file }, CancellationToken.None);
        double seconds = 0;
        var m = Regex.Match(err, @"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)");
        if (m.Success)
            seconds = int.Parse(m.Groups[1].Value) * 3600 + int.Parse(m.Groups[2].Value) * 60
                      + double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
        int w = 0, h = 0;
        var v = Regex.Match(err, @"Video:.*?(\d{2,5})x(\d{2,5})");
        if (v.Success) { w = int.Parse(v.Groups[1].Value); h = int.Parse(v.Groups[2].Value); }
        return (seconds, w, h);
    }

    /// <summary>Кадр в момент t (PNG), шириной width.</summary>
    public static async Task<byte[]?> FrameAsync(string file, double t, int width, CancellationToken ct)
    {
        var ffmpeg = Ffmpeg;
        if (ffmpeg is null) return null;
        var (code, bytes, _) = await RunBytesAsync(ffmpeg, new[]
        {
            "-hide_banner", "-loglevel", "error", "-ss", T(t), "-i", file, "-frames:v", "1",
            "-vf", $"scale={width}:-2", "-f", "image2pipe", "-vcodec", "png", "pipe:1",
        }, ct);
        return code == 0 && bytes.Length > 0 ? bytes : null;
    }

    /// <summary>Сохранить отрезок [start, end] в MP4 (H.264 + AAC; точный рез по кадрам).</summary>
    public static async Task TrimAsync(string file, double start, double end, string output, CancellationToken ct)
    {
        var ffmpeg = Ffmpeg ?? throw new InvalidOperationException(L.T("Для этого нужен ffmpeg (Настройки → Видео)."));
        var args = new List<string>
        {
            "-hide_banner", "-y", "-ss", T(start), "-i", file, "-t", T(end - start),
            // H.264 требует чётные ширину и высоту (у GIF и некоторых видео они бывают нечётными).
            "-vf", "scale=trunc(iw/2)*2:trunc(ih/2)*2",
            "-c:v", "libx264", "-preset", "veryfast", "-crf", "18", "-pix_fmt", "yuv420p",
            "-c:a", "aac", "-b:a", "160k", "-movflags", "+faststart", output,
        };
        var (code, _, err) = await RunAsync(ffmpeg, args, ct);
        if (code != 0) throw new InvalidOperationException(LastLine(err));
    }

    /// <summary>Отрезок в GIF: своя палитра для кадра (чёткий текст), бесконечный повтор.</summary>
    public static async Task GifAsync(string file, double start, double end, int fps, int width, string output, CancellationToken ct)
    {
        var ffmpeg = Ffmpeg ?? throw new InvalidOperationException(L.T("Для этого нужен ffmpeg (Настройки → Видео)."));
        var scale = width > 0 ? $",scale={width}:-2:flags=lanczos" : "";
        var args = new List<string>
        {
            "-hide_banner", "-y", "-ss", T(start), "-t", T(end - start), "-i", file,
            "-vf", $"fps={fps}{scale},split[a][b];[a]palettegen=stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=5:diff_mode=rectangle",
            "-loop", "0", output,
        };
        var (code, _, err) = await RunAsync(ffmpeg, args, ct);
        if (code != 0) throw new InvalidOperationException(LastLine(err));
    }

    static string T(double seconds) => Math.Max(0, seconds).ToString("0.###", CultureInfo.InvariantCulture);

    static string LastLine(string s) =>
        s.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? L.T("ffmpeg завершился с ошибкой.");

    static ProcessStartInfo Psi(string exe, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        return psi;
    }

    static async Task<(int, string, string)> RunAsync(string exe, IEnumerable<string> args, CancellationToken ct)
    {
        using var p = Process.Start(Psi(exe, args))!;
        var o = p.StandardOutput.ReadToEndAsync();
        var e = p.StandardError.ReadToEndAsync();
        try { await p.WaitForExitAsync(ct); }
        catch (OperationCanceledException) { try { p.Kill(true); } catch { } throw; }
        return (p.ExitCode, await o, await e);
    }

    static async Task<(int, byte[], string)> RunBytesAsync(string exe, IEnumerable<string> args, CancellationToken ct)
    {
        using var p = Process.Start(Psi(exe, args))!;
        using var ms = new MemoryStream();
        var copy = p.StandardOutput.BaseStream.CopyToAsync(ms, ct);
        var e = p.StandardError.ReadToEndAsync();
        try
        {
            await copy;
            await p.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException) { try { p.Kill(true); } catch { } throw; }
        return (p.ExitCode, ms.ToArray(), await e);
    }
}
