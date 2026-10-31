using System.Diagnostics;

namespace SnapMark.Services;

/// <summary>Открыть файл/папку/ссылку средствами ОС.</summary>
public static class ShellOpen
{
    public static void Open(string target)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            else if (OperatingSystem.IsMacOS())
                Process.Start(new ProcessStartInfo("open") { ArgumentList = { target } });
            else
                Process.Start(new ProcessStartInfo("xdg-open") { ArgumentList = { target } });
        }
        catch
        {
            // Нечем открыть — молча игнорируем, это не критично.
        }
    }

    public static void ShowInFolder(string file)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{file}\"") { UseShellExecute = false });
            else if (OperatingSystem.IsMacOS())
                Process.Start(new ProcessStartInfo("open") { ArgumentList = { "-R", file } });
            else
                Open(Path.GetDirectoryName(file) ?? file);
        }
        catch { }
    }

    public static void OpenFolder(string folder)
    {
        try { Directory.CreateDirectory(folder); } catch { }
        Open(folder);
    }
}
