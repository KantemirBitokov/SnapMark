using System.Diagnostics;
using System.Runtime.Versioning;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace SnapMark.Platform.Windows;

/// <summary>
/// Запись звука в WAV-файл через WASAPI параллельно с видео.
///   • loopback = true  — звук компьютера (всё, что играет в выбранных колонках/наушниках);
///   • loopback = false — микрофон.
///
/// Почему отдельный файл, а не «живой» поток в ffmpeg: у сырого звука из канала нет нормальных меток
/// времени, из-за этого плееры показывали сотни часов длины, а звук обрывался. Теперь звук пишется
/// с привязкой к реальному времени: если устройство на время замолчало (так делает WASAPI, когда
/// ничего не играет), пропуск заполняется тишиной — длина звука всегда совпадает с длиной видео.
/// После остановки видео и звук склеиваются в один файл.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WinAudioRecorder : IDisposable
{
    readonly IWaveIn _capture;
    readonly WasapiOut? _silence;
    readonly object _lock = new();
    FileStream? _writer;
    Stopwatch? _clock;
    long _written;
    bool _armed;

    public string FilePath { get; }
    public WaveFormat Format => _capture.WaveFormat;
    public bool HasData => _written > 0;

    /// <summary>
    /// Параметры сырого звука для ffmpeg (-f … -ar … -ac …). Пишем «сырой» PCM, а не WAV:
    /// у WAV предел 4 ГБ (≈3 часа звука), у сырого файла предела нет — запись может идти сколько угодно.
    /// </summary>
    public (string Format, int Rate, int Channels) RawInfo
    {
        get
        {
            var f = _capture.WaveFormat;
            bool isFloat = f.Encoding == WaveFormatEncoding.IeeeFloat
                           || (f.Encoding == WaveFormatEncoding.Extensible && f.BitsPerSample == 32);
            var fmt = isFloat ? "f32le" : f.BitsPerSample switch { 16 => "s16le", 24 => "s24le", 32 => "s32le", _ => "s16le" };
            return (fmt, f.SampleRate, f.Channels);
        }
    }

    public WinAudioRecorder(bool loopback, string? deviceId, string filePath)
    {
        FilePath = filePath;
        var device = WinAudioDevices.Find(loopback ? DataFlow.Render : DataFlow.Capture, deviceId);
        if (loopback)
        {
            _capture = new WasapiLoopbackCapture(device);
            // Неслышная тишина в то же устройство — чтобы Windows не прекращала присылать данные.
            _silence = new WasapiOut(device, AudioClientShareMode.Shared, true, 100);
            _silence.Init(new SilenceProvider(_capture.WaveFormat));
        }
        else
        {
            _capture = new WasapiCapture(device);
        }
        _capture.DataAvailable += OnData;
    }

    /// <summary>Открыть устройство заранее (данные пока отбрасываются).</summary>
    public void Start()
    {
        _silence?.Play();
        _capture.StartRecording();
    }

    /// <summary>Начать писать в файл — в момент, когда ffmpeg начал снимать видео.</summary>
    public void Arm()
    {
        lock (_lock)
        {
            if (_armed) return;
            _writer = new FileStream(FilePath, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16);
            _clock = Stopwatch.StartNew();
            _written = 0;
            _armed = true;
        }
    }

    void OnData(object? sender, WaveInEventArgs e)
    {
        lock (_lock)
        {
            if (!_armed || _writer is null || e.BytesRecorded <= 0) return;
            PadTo(ExpectedBytes() - e.BytesRecorded);
            _writer.Write(e.Buffer, 0, e.BytesRecorded);
            _written += e.BytesRecorded;
        }
    }

    long ExpectedBytes()
    {
        var f = _capture.WaveFormat;
        long bytes = (long)((_clock?.Elapsed.TotalSeconds ?? 0) * f.AverageBytesPerSecond);
        return bytes - bytes % f.BlockAlign;
    }

    /// <summary>Если отстаём от реального времени больше чем на 150 мс — дописываем тишину.</summary>
    void PadTo(long targetBytes)
    {
        if (_writer is null) return;
        var f = _capture.WaveFormat;
        long lag = targetBytes - _written;
        if (lag < f.AverageBytesPerSecond * 15 / 100) return;
        lag -= lag % f.BlockAlign;
        var zeros = new byte[Math.Min(lag, 65536)];
        while (lag > 0)
        {
            int n = (int)Math.Min(lag, zeros.Length);
            n -= n % f.BlockAlign;
            if (n <= 0) break;
            _writer.Write(zeros, 0, n);
            _written += n;
            lag -= n;
        }
    }

    /// <summary>Остановить запись: дописать тишину до текущего момента и закрыть файл.</summary>
    public void Stop()
    {
        lock (_lock)
        {
            if (_armed)
            {
                PadTo(ExpectedBytes());
                _armed = false;
            }
            _writer?.Dispose();
            _writer = null;
        }
        try { _capture.StopRecording(); } catch { }
        try { _silence?.Stop(); } catch { }
    }

    public void Dispose()
    {
        Stop();
        try { _capture.Dispose(); } catch { }
        try { _silence?.Dispose(); } catch { }
    }
}

/// <summary>Индикатор уровня микрофона для теста в настройках.</summary>
[SupportedOSPlatform("windows")]
public sealed class WinMicMeter : IDisposable
{
    readonly WasapiCapture _capture;

    /// <summary>Пиковый уровень 0…1 (вызывается в потоке захвата).</summary>
    public event Action<double>? Level;

    public WinMicMeter(string? deviceId)
    {
        _capture = new WasapiCapture(WinAudioDevices.Find(DataFlow.Capture, deviceId));
        _capture.DataAvailable += (_, e) =>
        {
            var f = _capture.WaveFormat;
            double peak = 0;
            if (f.BitsPerSample == 32 && f.Encoding is WaveFormatEncoding.IeeeFloat or WaveFormatEncoding.Extensible)
            {
                for (int i = 0; i + 4 <= e.BytesRecorded; i += 4)
                    peak = Math.Max(peak, Math.Abs(BitConverter.ToSingle(e.Buffer, i)));
            }
            else if (f.BitsPerSample == 16)
            {
                for (int i = 0; i + 2 <= e.BytesRecorded; i += 2)
                    peak = Math.Max(peak, Math.Abs(BitConverter.ToInt16(e.Buffer, i) / 32768.0));
            }
            Level?.Invoke(Math.Min(1, peak));
        };
        _capture.StartRecording();
    }

    public void Dispose()
    {
        try { _capture.StopRecording(); } catch { }
        try { _capture.Dispose(); } catch { }
    }
}

[SupportedOSPlatform("windows")]
public static class WinAudioDevices
{
    public static MMDevice Find(DataFlow flow, string? id)
    {
        var enumerator = new MMDeviceEnumerator();
        if (!string.IsNullOrEmpty(id))
        {
            try
            {
                return enumerator.GetDevice(id);
            }
            catch
            {
                // устройство отключили — берём устройство по умолчанию
            }
        }
        return enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia);
    }

    /// <summary>Активные устройства: render = true — колонки/наушники, false — микрофоны.</summary>
    public static List<(string Id, string Name)> List(bool render)
    {
        var list = new List<(string, string)>();
        try
        {
            var enumerator = new MMDeviceEnumerator();
            foreach (var d in enumerator.EnumerateAudioEndPoints(render ? DataFlow.Render : DataFlow.Capture, DeviceState.Active))
                list.Add((d.ID, d.FriendlyName));
        }
        catch
        {
            // нет звуковой подсистемы — пустой список
        }
        return list;
    }
}
