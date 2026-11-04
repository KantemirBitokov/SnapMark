using System.Diagnostics;
using System.Text.RegularExpressions;
using Avalonia;
using SnapMark.Core;

namespace SnapMark.Platform.Common;

/// <summary>
/// Запись области экрана через внешний ffmpeg — одинаково работает на Windows, macOS и Linux,
/// меняются только устройства ввода. ffmpeg кладётся рядом с SnapMark.exe или указывается в настройках.
///
/// Windows: видео пишется ffmpeg без звука (ровные кадры), звук — параллельно в сырой PCM через WASAPI
/// (микрофон и/или звук компьютера), после остановки всё склеивается в один файл.
/// TODO: захват через ddagrab (Desktop Duplication) для 60 fps без нагрузки на CPU.
/// </summary>
public sealed class FfmpegRecorder
{
    readonly Queue<string> _log = new();
    Process? _process;
    string _outputPath = "";
    VideoQuality _quality = VideoQuality.Standard;
    string? _tempVideo;
    VideoFileFormat _format;
    string _ffmpeg = "";
    readonly List<IDisposable> _audioHandles = new();
    readonly List<(string Path, Func<bool> HasData, Action Stop, Func<string[]> InputArgs)> _audioTracks = new();
    bool _stopping;

    /// <summary>ffmpeg сам завершился во время записи (например, кончилось место на диске).</summary>
    public event Action? UnexpectedExit;
    Action? _armAudio;
    bool _armed;
    PixelRect _macCrop;
    bool _macLiveAudio;

    /// <summary>Звук компьютера не удалось подключить: «blackhole» — нужен драйвер (macOS 11–12), иначе текст ошибки.</summary>
    public string? SystemAudioProblem { get; private set; }

    /// <summary>ffmpeg пишет звук прямо в видео (микрофон на macOS, PulseAudio в Linux).</summary>
    bool _liveAudioInVideo;

    public string OutputPath => _outputPath;
    public bool IsRunning => _process is { HasExited: false };

    // ── Поиск ffmpeg ──────────────────────────────────────────────────
    public static string? Resolve(string configured)
    {
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;

        var exe = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        var local = Path.Combine(AppContext.BaseDirectory, exe);
        if (File.Exists(local)) return local;
        local = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "bin", exe);
        if (File.Exists(local)) return local;

        // Скачанный самим SnapMark (Настройки → Видео → «Скачать ffmpeg»).
        if (File.Exists(FfmpegDownloader.InstalledExe)) return FfmpegDownloader.InstalledExe;

        // Установленный через winget (winget install Gyan.FFmpeg) — PATH у уже запущенного процесса может быть старым.
        var winget = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WinGet", "Links", exe);
        if (File.Exists(winget)) return winget;

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim('"'), exe);
                if (File.Exists(candidate)) return candidate;
            }
            catch { }
        }

        foreach (var c in new[] { "/opt/homebrew/bin/ffmpeg", "/usr/local/bin/ffmpeg", "/usr/bin/ffmpeg" })
            if (File.Exists(c)) return c;
        return null;
    }

    /// <summary>Список микрофонов, которые видит ffmpeg.</summary>
    public static async Task<List<string>> ListMicrophonesAsync(string ffmpeg)
    {
        var result = new List<string>();
        try
        {
            var psi = new ProcessStartInfo(ffmpeg)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            psi.ArgumentList.Add("-hide_banner");
            if (OperatingSystem.IsWindows())
            {
                foreach (var a in new[] { "-list_devices", "true", "-f", "dshow", "-i", "dummy" }) psi.ArgumentList.Add(a);
            }
            else if (OperatingSystem.IsMacOS())
            {
                foreach (var a in new[] { "-f", "avfoundation", "-list_devices", "true", "-i", "" }) psi.ArgumentList.Add(a);
            }
            else
            {
                result.Add("default");
                return result;
            }

            using var p = Process.Start(psi)!;
            var stderr = await p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();

            if (OperatingSystem.IsWindows())
            {
                // [dshow @ 000001] "Микрофон (Realtek(R) Audio)" (audio)
                foreach (Match m in Regex.Matches(stderr, "\"([^\"]+)\"\\s+\\(audio\\)"))
                    result.Add(m.Groups[1].Value);
            }
            else
            {
                bool audio = false;
                foreach (var line in stderr.Split('\n'))
                {
                    if (line.Contains("audio devices", StringComparison.OrdinalIgnoreCase)) { audio = true; continue; }
                    if (!audio) continue;
                    var m = Regex.Match(line, @"\[(\d+)\]\s+(.+)$");
                    if (m.Success) result.Add(m.Groups[2].Value.Trim());
                }
            }
        }
        catch { }
        return result;
    }

    // ── Запись ────────────────────────────────────────────────────────
    /// <param name="rect">Область в физических пикселях виртуального рабочего стола.</param>
    /// <param name="withMic">Записывать микрофон.</param>
    /// <param name="withSystem">Записывать звук компьютера (то, что играет в колонках/наушниках).</param>
    public async Task StartAsync(string ffmpeg, PixelRect rect, AppSettings s, bool withMic, bool withSystem, string outputPath)
    {
        _ffmpeg = ffmpeg;
        _format = s.VideoFormat;
        _quality = s.VideoQuality;
        _outputPath = outputPath;

        // H.264/VP9 с yuv420p требуют чётных размеров.
        int w = Math.Max(2, rect.Width & ~1);
        int h = Math.Max(2, rect.Height & ~1);
        int fps = Math.Clamp(s.VideoFps, 5, 60);
        bool wantAudio = _format != VideoFileFormat.Gif && (withMic || withSystem);

        // ── Звук (Windows): отдельные WAV-файлы через WASAPI ──────────────
        if (wantAudio && OperatingSystem.IsWindows())
        {
            try
            {
                if (withSystem) AddWindowsTrack(loopback: true, s.SystemAudioDevice, "system");
                if (withMic) AddWindowsTrack(loopback: false, s.MicrophoneDevice, "mic");
            }
            catch (Exception ex)
            {
                DisposeAudio();
                throw new InvalidOperationException(L.T("Не удалось открыть звуковое устройство: ") + ex.Message, ex);
            }
        }
        // ── Звук компьютера (macOS 13+): встроенный помощник на ScreenCaptureKit ──
        if (wantAudio && withSystem && OperatingSystem.IsMacOS())
        {
            if (Mac.MacSystemAudio.IsAvailable)
            {
                try
                {
                    var path = Path.Combine(Path.GetTempPath(), $"snapmark_system_{Guid.NewGuid():N}.pcm");
                    var rec = await Mac.MacSystemAudio.StartAsync(path);
                    _audioHandles.Add(rec);
                    _audioTracks.Add((path, () => rec.HasData, rec.Stop, () =>
                    {
                        var (fmt, rate, ch) = rec.RawInfo;
                        return new[] { "-f", fmt, "-ar", rate.ToString(), "-ac", ch.ToString(), "-i", path };
                    }));
                    _armAudio += rec.Arm;
                }
                catch (Exception ex)
                {
                    SystemAudioProblem = ex.Message;
                }
            }
            else
            {
                SystemAudioProblem = "blackhole";
            }
        }
        bool separateAudio = _audioTracks.Count > 0;

        // Куда пишет ffmpeg. MP4, отдельный звук и GIF — сначала во временный MKV, потом склейка/перепаковка.
        // MKV не боится обрыва: даже если программа или ПК упадут посреди многочасовой записи,
        // временный файл останется читаемым (у MP4 без корректного завершения теряется всё).
        var target = outputPath;
        if (_format is VideoFileFormat.Gif or VideoFileFormat.Mp4 || separateAudio)
        {
            var tempExt = _format == VideoFileFormat.WebM ? "webm" : "mkv";
            _tempVideo = Path.Combine(Path.GetTempPath(), $"snapmark_rec_{Guid.NewGuid():N}.{tempExt}");
            target = _tempVideo;
        }

        var args = new List<string> { "-hide_banner", "-y" };

        // Видео-вход.
        if (OperatingSystem.IsWindows())
        {
            args.AddRange(new[]
            {
                "-thread_queue_size", "1024",
                "-f", "gdigrab", "-framerate", fps.ToString(), "-draw_mouse", s.VideoShowCursor ? "1" : "0",
                "-offset_x", rect.X.ToString(), "-offset_y", rect.Y.ToString(),
                "-video_size", $"{w}x{h}", "-i", "desktop",
            });
        }
        else if (OperatingSystem.IsMacOS())
        {
            // Экран и микрофон — одним входом avfoundation («экран:микрофон»), так они синхронны.
            // Системный звук macOS программам не отдаёт (нужен драйвер вроде BlackHole) — только микрофон.
            var (device, screen) = await MacScreenDeviceAsync(ffmpeg, rect);
            _macCrop = new PixelRect(rect.X - screen.X, rect.Y - screen.Y, w, h);
            var mic = wantAudio && withMic
                ? (string.IsNullOrWhiteSpace(s.MicrophoneDevice) ? "0" : s.MicrophoneDevice)
                : "none";
            args.AddRange(new[]
            {
                "-f", "avfoundation", "-capture_cursor", s.VideoShowCursor ? "1" : "0", "-capture_mouse_clicks", s.VideoShowCursor ? "1" : "0",
                "-framerate", fps.ToString(), "-i", $"{device}:{mic}",
            });
            if (mic != "none") _macLiveAudio = true;
        }
        else
        {
            if (Linux.Xlib.IsWayland)
                throw new InvalidOperationException(L.T("Запись видео в Linux пока работает только в сеансе X11 (Xorg). На экране входа выбери «Ubuntu на Xorg» / «Plasma (X11)»."));
            var display = Environment.GetEnvironmentVariable("DISPLAY") ?? ":0.0";
            args.AddRange(new[] { "-f", "x11grab", "-framerate", fps.ToString(), "-draw_mouse", s.VideoShowCursor ? "1" : "0", "-video_size", $"{w}x{h}", "-i", $"{display}+{rect.X},{rect.Y}" });
        }

        // Звук на macOS/Linux — настоящие устройства ffmpeg с нормальными метками времени.
        int liveAudio = 0;
        if (wantAudio && !OperatingSystem.IsWindows())
        {
            if (OperatingSystem.IsMacOS())
            {
                if (_macLiveAudio) liveAudio = 1; // микрофон уже во входе экрана
            }
            else
            {
                if (withSystem) { args.AddRange(new[] { "-f", "pulse", "-i", "@DEFAULT_MONITOR@" }); liveAudio++; }
                if (withMic) { args.AddRange(new[] { "-f", "pulse", "-i", "default" }); liveAudio++; }
            }
            if (liveAudio == 2)
                args.AddRange(new[] { "-filter_complex", "[1:a][2:a]amix=inputs=2:duration=longest:normalize=0[aout]", "-map", "0:v", "-map", "[aout]" });
        }
        _liveAudioInVideo = liveAudio > 0;

        if (OperatingSystem.IsMacOS())
            args.AddRange(new[] { "-vf", $"crop={_macCrop.Width}:{_macCrop.Height}:{_macCrop.X}:{_macCrop.Y}" });

        // Кодеки видео (звук на Windows добавится при склейке).
        if (_format == VideoFileFormat.WebM)
        {
            var vp9Crf = s.VideoQuality switch { VideoQuality.Low => "40", VideoQuality.High => "26", VideoQuality.Best => "20", _ => "32" };
            args.AddRange(new[] { "-c:v", "libvpx-vp9", "-deadline", "realtime", "-cpu-used", "8", "-row-mt", "1", "-b:v", "0", "-crf", vp9Crf, "-pix_fmt", "yuv420p" });
            if (liveAudio > 0) args.AddRange(new[] { "-c:a", "libopus", "-b:a", "128k" });
        }
        else
        {
            // CRF: меньше — чётче и больше файл. «Максимальное» к тому же без цветовой субдискретизации
            // не делаем: yuv444 не открывается во многих плеерах и браузерах.
            var x264Crf = s.VideoQuality switch { VideoQuality.Low => "30", VideoQuality.High => "19", VideoQuality.Best => "15", _ => "23" };
            args.AddRange(new[] { "-c:v", "libx264", "-preset", "veryfast", "-crf", x264Crf, "-pix_fmt", "yuv420p" });
            if (liveAudio > 0) args.AddRange(new[] { "-c:a", "aac", "-b:a", "160k" });
        }
        args.Add(target);

        var psi = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        // Звуковые устройства открываем заранее, а писать в файл начинаем ровно тогда,
        // когда ffmpeg реально начал снимать (строка «Press [q] to stop»).
        foreach (var t in _audioHandles.OfType<Windows.WinAudioRecorder>()) t.Start();

        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _process.ErrorDataReceived += (_, e) =>
        {
            AppendLog(e.Data);
            if (!_armed && e.Data is not null && e.Data.Contains("Press [q]", StringComparison.Ordinal)) ArmAudio();
        };
        _process.OutputDataReceived += (_, e) => AppendLog(e.Data);
        _process.Exited += (_, _) =>
        {
            if (!_stopping) UnexpectedExit?.Invoke();
        };
        _process.Start();
        _process.BeginErrorReadLine();
        _process.BeginOutputReadLine();

        // Если ffmpeg сразу упал (неверная область, нет кодека) — сообщаем об ошибке.
        await Task.Delay(800);
        if (_process.HasExited)
        {
            DisposeAudio();
            throw new InvalidOperationException(L.T("ffmpeg завершился с ошибкой:\n") + LastLog());
        }

        // Подстраховка: если строку старта не поймали — начинаем писать звук сейчас.
        _ = Task.Delay(2500).ContinueWith(_ => ArmAudio());
    }

    /// <summary>
    /// macOS: номер устройства avfoundation «Capture screen N» для монитора с областью записи
    /// и начало этого монитора в координатах Avalonia (для обрезки).
    /// </summary>
    static async Task<(int Device, PixelPoint ScreenOrigin)> MacScreenDeviceAsync(string ffmpeg, PixelRect rect)
    {
        var screens = App.GetScreens().ToList();
        int screenIndex = Math.Max(0, screens.FindIndex(sc => sc.Bounds.Contains(rect.Center)));
        var origin = screens.Count > 0 ? screens[screenIndex].Bounds.Position : new PixelPoint(0, 0);

        int device = screenIndex + 1; // запасной вариант: обычно камера — 0, экраны — дальше
        try
        {
            var psi = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            foreach (var a in new[] { "-hide_banner", "-f", "avfoundation", "-list_devices", "true", "-i", "" }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var text = await p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            var m = Regex.Match(text, $@"\[(\d+)\]\s+Capture screen {screenIndex}\b");
            if (m.Success) device = int.Parse(m.Groups[1].Value);
        }
        catch { }
        return (device, origin);
    }

    void AddWindowsTrack(bool loopback, string deviceId, string name)
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = Path.Combine(Path.GetTempPath(), $"snapmark_{name}_{Guid.NewGuid():N}.pcm");
        var rec = new Windows.WinAudioRecorder(loopback, deviceId, path);
        _audioHandles.Add(rec);
        _audioTracks.Add((path, () => rec.HasData, rec.Stop, () =>
        {
            var (fmt, rate, ch) = rec.RawInfo;
            return new[] { "-f", fmt, "-ar", rate.ToString(), "-ac", ch.ToString(), "-i", path };
        }));
        _armAudio += rec.Arm;
    }

    void ArmAudio()
    {
        if (_armed) return;
        _armed = true;
        try { _armAudio?.Invoke(); } catch { }
    }

    void DisposeAudio()
    {
        foreach (var h in _audioHandles)
        {
            try { h.Dispose(); } catch { }
        }
        _audioHandles.Clear();
        foreach (var t in _audioTracks)
        {
            try { File.Delete(t.Path); } catch { }
        }
        _audioTracks.Clear();
        _armAudio = null;
    }

    public async Task<string> StopAsync()
    {
        var p = _process;
        if (p is null) return _outputPath;
        _stopping = true;

        // Звук останавливаем одновременно с видео — длины совпадут.
        foreach (var t in _audioTracks) t.Stop();

        if (!p.HasExited)
        {
            try
            {
                await p.StandardInput.WriteAsync('q'); // штатная остановка — ffmpeg корректно допишет файл
                await p.StandardInput.FlushAsync();
            }
            catch { }

            // После долгой записи кодеку может понадобиться время, чтобы дописать хвост.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException) { try { p.Kill(true); } catch { } }
        }
        p.Dispose();
        _process = null;

        try
        {
            if (_tempVideo is not null && File.Exists(_tempVideo))
            {
                if (_format == VideoFileFormat.Gif)
                {
                    await ConvertToGifAsync(_tempVideo, _outputPath);
                }
                else
                {
                    var tracks = _audioTracks.Where(t => t.HasData() && File.Exists(t.Path)).Select(t => t.InputArgs()).ToList();
                    if (tracks.Count > 0 || _format == VideoFileFormat.Mp4) await MuxAsync(_tempVideo, tracks, _outputPath);
                    else File.Move(_tempVideo, _outputPath, true);
                }
            }
        }
        finally
        {
            if (_tempVideo is not null) { try { File.Delete(_tempVideo); } catch { } }
            DisposeAudio();
        }
        return _outputPath;
    }

    /// <summary>
    /// Склейка: видео без перекодирования + звук (одна дорожка или смесь двух).
    /// Без звука — просто перепаковка временного MKV в итоговый MP4.
    /// </summary>
    async Task MuxAsync(string video, List<string[]> audio, string output)
    {
        var args = new List<string> { "-hide_banner", "-y", "-i", video };
        foreach (var a in audio) args.AddRange(a);

        // Звук, который уже есть во временном видео (микрофон на macOS/Linux пишется вместе с картинкой).
        bool videoHasAudio = _liveAudioInVideo;
        if (audio.Count == 0)
        {
            args.AddRange(new[] { "-map", "0" }); // просто перепаковка: все дорожки как есть
            args.AddRange(new[] { "-c", "copy" });
        }
        else
        {
            var labels = new List<string>();
            if (videoHasAudio) labels.Add("[0:a]");
            labels.AddRange(Enumerable.Range(1, audio.Count).Select(i => $"[{i}:a]"));
            if (labels.Count == 1)
                args.AddRange(new[] { "-map", "0:v", "-map", "1:a" });
            else
                args.AddRange(new[]
                {
                    "-filter_complex", $"{string.Concat(labels)}amix=inputs={labels.Count}:duration=longest:normalize=0[aout]",
                    "-map", "0:v", "-map", "[aout]",
                });
            args.AddRange(new[] { "-c:v", "copy" });
        }

        if (audio.Count > 0)
            args.AddRange(_format == VideoFileFormat.WebM
            ? new[] { "-c:a", "libopus", "-b:a", "160k" }
            : new[] { "-c:a", "aac", "-b:a", "192k" });
        if (_format == VideoFileFormat.Mp4) args.AddRange(new[] { "-movflags", "+faststart" });
        if (audio.Count > 0) args.Add("-shortest");
        args.Add(output);

        var exit = await RunFfmpegAsync(args);
        if (exit != 0 || !File.Exists(output))
        {
            if (audio.Count == 0)
            {
                // Перепаковать не вышло — отдаём запись как есть, в MKV рядом с задуманным файлом.
                var fallback = Path.ChangeExtension(output, ".mkv");
                File.Copy(video, fallback, true);
                _outputPath = fallback;
                return;
            }
            // Склейка не удалась — сохраняем хотя бы видео.
            File.Copy(video, output, true);
            throw new InvalidOperationException(L.T("Видео сохранено без звука: не удалось добавить звуковую дорожку.\n") + LastLog());
        }
    }

    async Task ConvertToGifAsync(string input, string output)
    {
        await RunFfmpegAsync(new List<string>
        {
            "-hide_banner", "-y", "-i", input,
            "-vf", _quality switch
            {
                // GIF: качество — это частота кадров, размер и число цветов.
                VideoQuality.Low => "fps=10,scale=trunc(iw/2/2)*2:-2,split[a][b];[a]palettegen=max_colors=128:stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=4",
                VideoQuality.High => "fps=20,split[a][b];[a]palettegen=stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=3",
                VideoQuality.Best => "fps=25,split[a][b];[a]palettegen=stats_mode=full[p];[b][p]paletteuse=dither=sierra2_4a",
                _ => "fps=15,split[a][b];[a]palettegen=stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=4",
            },
            output,
        });
    }

    async Task<int> RunFfmpegAsync(List<string> args)
    {
        var psi = new ProcessStartInfo(_ffmpeg)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = new Process { StartInfo = psi };
        p.ErrorDataReceived += (_, e) => AppendLog(e.Data);
        p.OutputDataReceived += (_, e) => AppendLog(e.Data);
        p.Start();
        p.BeginErrorReadLine();
        p.BeginOutputReadLine();
        await p.WaitForExitAsync();
        return p.ExitCode;
    }

    void AppendLog(string? line)
    {
        if (string.IsNullOrEmpty(line)) return;
        lock (_log)
        {
            _log.Enqueue(line);
            while (_log.Count > 20) _log.Dequeue();
        }
    }

    public string LastLog()
    {
        lock (_log) return string.Join(Environment.NewLine, _log.TakeLast(6));
    }
}
