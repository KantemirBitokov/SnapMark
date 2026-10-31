using System.Diagnostics;
using System.Runtime.Versioning;
using SnapMark.Core;

namespace SnapMark.Platform.Mac;

/// <summary>
/// Звук компьютера на macOS 13+ — через встроенный помощник snapmark-audio (ScreenCaptureKit),
/// который лежит рядом с программой в SnapMark.app/Contents/MacOS. Драйверы не нужны.
/// На macOS 11–12 ScreenCaptureKit не умеет звук — там предлагаем BlackHole.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed class MacSystemAudio : IDisposable
{
    public const string BlackHoleUrl = "https://existential.audio/blackhole/";

    readonly Process _process;
    string _format = "f32le";
    int _rate = 48000;
    int _channels = 2;

    public string FilePath { get; }
    public bool HasData => File.Exists(FilePath) && new FileInfo(FilePath).Length > 0;
    public (string Format, int Rate, int Channels) RawInfo => (_format, _rate, _channels);

    public static string HelperPath => Path.Combine(AppContext.BaseDirectory, "snapmark-audio");

    /// <summary>Можно ли писать звук компьютера встроенными средствами.</summary>
    public static bool IsAvailable => Environment.OSVersion.Version.Major >= 13 && File.Exists(HelperPath);

    MacSystemAudio(string filePath, Process process)
    {
        FilePath = filePath;
        _process = process;
    }

    /// <summary>Запустить помощник и дождаться, пока захват действительно начнётся.</summary>
    public static async Task<MacSystemAudio> StartAsync(string filePath)
    {
        var psi = new ProcessStartInfo(HelperPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add(filePath);
        var p = Process.Start(psi) ?? throw new InvalidOperationException("snapmark-audio");
        var rec = new MacSystemAudio(filePath, p);

        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var errors = new List<string>();
        p.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            if (e.Data.StartsWith("STARTED", StringComparison.Ordinal)) started.TrySetResult(true);
            else if (e.Data.StartsWith("FORMAT ", StringComparison.Ordinal))
            {
                var parts = e.Data.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 4 && int.TryParse(parts[2], out var rate) && int.TryParse(parts[3], out var ch))
                {
                    rec._format = parts[1];
                    rec._rate = rate;
                    rec._channels = ch;
                }
            }
        };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (errors) errors.Add(e.Data); };
        p.EnableRaisingEvents = true;
        p.Exited += (_, _) => started.TrySetResult(false);
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        var finished = await Task.WhenAny(started.Task, Task.Delay(TimeSpan.FromSeconds(6)));
        if (finished != started.Task || !started.Task.Result)
        {
            rec.Dispose();
            string details;
            lock (errors) details = string.Join(" ", errors);
            throw new InvalidOperationException(L.T("Не удалось начать запись звука компьютера. ") + details);
        }
        return rec;
    }

    public void Arm() => Send("arm");

    public void Stop()
    {
        Send("stop");
        try
        {
            if (!_process.WaitForExit(4000)) _process.Kill();
        }
        catch { }
    }

    void Send(string command)
    {
        try
        {
            if (_process.HasExited) return;
            _process.StandardInput.WriteLine(command);
            _process.StandardInput.Flush();
        }
        catch { }
    }

    public void Dispose()
    {
        Stop();
        _process.Dispose();
    }
}
