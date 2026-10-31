using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using Avalonia.Input;
using SnapMark.Core;

namespace SnapMark.Platform.Linux;

/// <summary>
/// Буфер обмена Linux: картинка (image/png), текст и список файлов (text/uri-list) одновременно —
/// через буфер Avalonia (X11/XWayland). Если он не справился — запасной путь через wl-copy / xclip.
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed class LinuxClipboard : IRichClipboard
{
    public async Task CopyAsync(ClipboardPayload payload)
    {
        var clipboard = App.ClipboardHost?.Clipboard;
        try
        {
            if (clipboard is null) throw new InvalidOperationException("clipboard");
            var data = new DataObject();
            if (!string.IsNullOrEmpty(payload.Text)) data.Set(DataFormats.Text, payload.Text);
            if (payload.Png is { Length: > 0 }) data.Set("image/png", payload.Png);
            if (payload.Files is { Count: > 0 } files)
            {
                var uris = string.Join("\r\n", files.Select(f => new Uri(f).AbsoluteUri)) + "\r\n";
                data.Set("text/uri-list", Encoding.UTF8.GetBytes(uris));
                data.Set("x-special/gnome-copied-files", Encoding.UTF8.GetBytes("copy\n" + uris.Replace("\r\n", "\n").TrimEnd('\n')));
            }
            await clipboard.SetDataObjectAsync(data);
        }
        catch
        {
            // Запасной путь: внешние утилиты умеют только один формат за раз — картинка важнее.
            if (payload.Png is { Length: > 0 }) await PipeTo(Xlib.IsWayland ? "wl-copy" : "xclip",
                Xlib.IsWayland ? new[] { "--type", "image/png" } : new[] { "-selection", "clipboard", "-t", "image/png", "-i" },
                payload.Png);
            else if (!string.IsNullOrEmpty(payload.Text) && clipboard is not null)
                await clipboard.SetTextAsync(payload.Text);
        }
    }

    static async Task PipeTo(string tool, string[] args, byte[] data)
    {
        var psi = new ProcessStartInfo(tool) { UseShellExecute = false, RedirectStandardInput = true, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException(tool);
        await p.StandardInput.BaseStream.WriteAsync(data);
        p.StandardInput.Close();
        // wl-copy/xclip остаются в фоне, обслуживая буфер, — не ждём их завершения.
    }
}

/// <summary>Автозапуск по стандарту freedesktop: ~/.config/autostart/snapmark.desktop.</summary>
[SupportedOSPlatform("linux")]
internal sealed class LinuxAutostart : IAutostart
{
    static string DesktopFile
    {
        get
        {
            var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            if (string.IsNullOrEmpty(config))
                config = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            return Path.Combine(config, "autostart", "snapmark.desktop");
        }
    }

    public bool IsSupported => true;

    public void Set(bool enabled)
    {
        if (!enabled)
        {
            if (File.Exists(DesktopFile)) File.Delete(DesktopFile);
            return;
        }
        // AppImage запускается через свой файл (переменная APPIMAGE), а не через распакованный exe.
        var exe = Environment.GetEnvironmentVariable("APPIMAGE");
        if (string.IsNullOrEmpty(exe)) exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(DesktopFile)!);
        File.WriteAllText(DesktopFile, $"""
            [Desktop Entry]
            Type=Application
            Name=SnapMark
            Exec="{exe}" --tray
            Icon=snapmark
            X-GNOME-Autostart-enabled=true
            NoDisplay=false
            """ + "\n");
    }
}
