using Avalonia;

namespace SnapMark;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Один экземпляр: второй запуск просто выходит (приложение живёт в трее).
        var command = Services.CommandLine.Find(args);
        using var mutex = new Mutex(true, "SnapMark.SingleInstance.v1", out bool createdNew);
        if (!createdNew)
        {
            // snapmark --region и т. п.: передаём команду уже запущенному SnapMark.
            if (command is not null)
            {
                Services.CommandLine.SendToRunning(command);
                return;
            }

            // Перезапуск после смены языка: ждём, пока старый процесс закроется.
            if (!args.Contains("--restart")) return;
            try
            {
                if (!mutex.WaitOne(TimeSpan.FromSeconds(15))) return;
            }
            catch (AbandonedMutexException)
            {
                // старый процесс завершился, не освободив мьютекс, — он теперь наш
            }
        }

        Services.CommandLine.StartupCommand = command;
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
    }

    // Используется и дизайнером Avalonia.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
