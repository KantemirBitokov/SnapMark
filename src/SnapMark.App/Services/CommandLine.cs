using System.IO.Pipes;
using Avalonia.Threading;
using SnapMark.Platform;

namespace SnapMark.Services;

/// <summary>
/// Команды из командной строки: snapmark --region | --full | --record | --series | --settings.
///
/// Зачем: на macOS и в Linux (особенно под Wayland) не всегда можно повесить глобальную клавишу
/// изнутри программы. Тогда в системных настройках назначается сочетание, которое запускает
/// «snapmark --region», — второй процесс передаёт команду уже запущенному SnapMark и сразу выходит.
/// </summary>
public static class CommandLine
{
    // Своя «труба» у каждого пользователя: на Linux/macOS это сокет во временной папке.
    static readonly string PipeName = "SnapMark.Command.v1." +
        new string(Environment.UserName.Where(char.IsLetterOrDigit).ToArray());

    /// <summary>Команда, с которой запустили первый экземпляр (выполняется после старта).</summary>
    public static string? StartupCommand { get; set; }

    public static readonly string[] Commands = { "--region", "--full", "--record", "--series", "--settings" };

    public static string? Find(string[] args) =>
        args.Select(a => a.Trim().ToLowerInvariant()).FirstOrDefault(a => Commands.Contains(a));

    /// <summary>Второй экземпляр: передать команду первому. true — передано.</summary>
    public static bool SendToRunning(string command)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(2000);
            using var writer = new StreamWriter(client) { AutoFlush = true };
            writer.WriteLine(command);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Первый экземпляр: слушать команды от следующих запусков.</summary>
    public static void StartServer()
    {
        var thread = new Thread(ServerLoop) { IsBackground = true, Name = "SnapMark commands" };
        thread.Start();
    }

    static void ServerLoop()
    {
        while (true)
        {
            try
            {
                using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.None);
                server.WaitForConnection();
                using var reader = new StreamReader(server);
                var line = reader.ReadLine()?.Trim().ToLowerInvariant();
                if (!string.IsNullOrEmpty(line)) Dispatcher.UIThread.Post(() => Execute(line));
            }
            catch
            {
                Thread.Sleep(500);
            }
        }
    }

    public static void Execute(string command)
    {
        switch (command)
        {
            case "--region": App.RunHotkeyAction(HotkeyAction.CaptureRegion); break;
            case "--full": App.RunHotkeyAction(HotkeyAction.CaptureFull); break;
            case "--record": App.RunHotkeyAction(HotkeyAction.RecordVideo); break;
            case "--steps": App.RunHotkeyAction(HotkeyAction.StepGuide); break;
            case "--series": UI.SeriesWindow.ShowSingle(); break;
            case "--settings": Settings.SettingsWindow.ShowSingle(); break;
        }
    }
}
