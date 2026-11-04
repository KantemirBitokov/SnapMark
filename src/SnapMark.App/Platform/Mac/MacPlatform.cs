using System.Diagnostics;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using SnapMark.Core;
using SnapMark.Services;

namespace SnapMark.Platform.Mac;

/// <summary>
/// Буфер обмена macOS (NSPasteboard) через встроенный JavaScript for Automation (osascript).
/// Кладём сразу несколько вариантов: PNG + текст в одном элементе, плюс ссылки на файлы серии.
/// Каждое приложение берёт что умеет: Заметки/Pages/Telegram — картинку, TextEdit/терминал — текст,
/// Finder/Slack/Почта — файлы.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed class MacClipboard : IRichClipboard
{
    const string Script = """
        ObjC.import('AppKit');
        function run(argv) {
          var png = argv[0], txt = argv[1], files = argv.slice(2);
          var pb = $.NSPasteboard.generalPasteboard;
          pb.clearContents;
          var objs = [];
          if (png.length || txt.length) {
            var item = $.NSPasteboardItem.alloc.init;
            if (png.length) item.setDataForType($.NSData.dataWithContentsOfFile(png), $.NSPasteboardTypePNG);
            if (txt.length) {
              var s = $.NSString.stringWithContentsOfFileEncodingError(txt, $.NSUTF8StringEncoding, null);
              item.setStringForType(s, $.NSPasteboardTypeString);
            }
            objs.push(item);
          }
          for (var i = 0; i < files.length; i++) objs.push($.NSURL.fileURLWithPath(files[i]));
          pb.writeObjects($(objs));
          return 'ok';
        }
        """;

    public async Task CopyAsync(ClipboardPayload payload)
    {
        var dir = Path.Combine(TempFiles.Root, "clip");
        Directory.CreateDirectory(dir);
        var png = "";
        var txt = "";
        if (payload.Png is { Length: > 0 })
        {
            png = Path.Combine(dir, $"clip_{Guid.NewGuid():N}.png");
            await File.WriteAllBytesAsync(png, payload.Png);
        }
        if (!string.IsNullOrEmpty(payload.Text))
        {
            txt = Path.Combine(dir, $"clip_{Guid.NewGuid():N}.txt");
            await File.WriteAllTextAsync(txt, payload.Text);
        }

        var psi = new ProcessStartInfo("/usr/bin/osascript")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        foreach (var a in new[] { "-l", "JavaScript", "-e", Script, png, txt }) psi.ArgumentList.Add(a);
        foreach (var f in payload.Files ?? Array.Empty<string>()) psi.ArgumentList.Add(f);

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("osascript");
        var err = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        if (p.ExitCode != 0) throw new InvalidOperationException(L.T("Не удалось записать в буфер обмена: ") + err.Trim());
    }
}

/// <summary>Автозапуск: LaunchAgent в ~/Library/LaunchAgents.</summary>
[SupportedOSPlatform("macos")]
internal sealed class MacAutostart : IAutostart
{
    static string PlistPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Library", "LaunchAgents", "pro.sadlybear.snapmark.plist");

    public bool IsSupported => true;

    public void Set(bool enabled)
    {
        if (!enabled)
        {
            if (File.Exists(PlistPath)) File.Delete(PlistPath);
            return;
        }
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(PlistPath)!);
        File.WriteAllText(PlistPath, $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
              <key>Label</key><string>pro.sadlybear.snapmark</string>
              <key>ProgramArguments</key>
              <array><string>{System.Security.SecurityElement.Escape(exe)}</string><string>--tray</string></array>
              <key>RunAtLoad</key><true/>
              <key>ProcessType</key><string>Interactive</string>
            </dict>
            </plist>
            """);
    }
}

[SupportedOSPlatform("macos")]
internal sealed class MacWindowHelper : IWindowHelper
{
    public void ForceForeground(Window window)
    {
        // Программа живёт в строке меню (без иконки в Dock) — без явной активации окно не получит клавиатуру.
        MacNative.ActivateApp();
        window.Activate();
    }

    public void ExcludeFromCapture(TopLevel window)
    {
        // NSWindowSharingNone: панель записи и рамка не попадают в видео и скриншоты.
        var w = MacNative.NSWindowOf(window);
        if (w != IntPtr.Zero && MacNative.Responds(w, "setSharingType:"))
            MacNative.SendULong(w, MacNative.Sel("setSharingType:"), 0);
    }

    public void MakeOverlay(Window window)
    {
        // Поверх строки меню и Dock, на любом рабочем столе (Spaces) и поверх полноэкранных приложений.
        var w = MacNative.NSWindowOf(window);
        if (w == IntPtr.Zero) return;
        if (MacNative.Responds(w, "setLevel:"))
            MacNative.SendLong(w, MacNative.Sel("setLevel:"), 1000); // NSScreenSaverWindowLevel
        if (MacNative.Responds(w, "setCollectionBehavior:"))
            MacNative.SendULong(w, MacNative.Sel("setCollectionBehavior:"), (1UL << 0) | (1UL << 8)); // CanJoinAllSpaces | FullScreenAuxiliary
    }

    public bool SetClickThrough(Window window, bool enabled)
    {
        // Явное NO тоже важно: иначе macOS сама пропускает клики по полностью прозрачным пикселям.
        var w = MacNative.NSWindowOf(window);
        if (w == IntPtr.Zero || !MacNative.Responds(w, "setIgnoresMouseEvents:")) return false;
        MacNative.SendBool(w, MacNative.Sel("setIgnoresMouseEvents:"), enabled);
        return true;
    }

    public void BringToFront(Window window)
    {
        var w = MacNative.NSWindowOf(window);
        if (w != IntPtr.Zero && MacNative.Responds(w, "orderFrontRegardless")) MacNative.Send(w, MacNative.Sel("orderFrontRegardless"));
    }

    public PixelPoint? GetCursorPosition() => null;

    public void SetPixelBounds(Window window, PixelRect bounds)
    {
        var scaling = window.RenderScaling > 0 ? window.RenderScaling : 1;
        window.Position = bounds.Position;
        window.Width = bounds.Width / scaling;
        window.Height = bounds.Height / scaling;
    }
}

/// <summary>Разрешение «Запись экрана»: без него screencapture возвращает только обои рабочего стола.</summary>
[SupportedOSPlatform("macos")]
internal static class MacScreenPermission
{
    public static bool IsGranted()
    {
        try { return MacNative.CGPreflightScreenCaptureAccess(); }
        catch { return true; } // старая macOS без этого API — разрешение не требуется
    }

    public static void Request()
    {
        try { MacNative.CGRequestScreenCaptureAccess(); } catch { }
    }

    public static void OpenSettings() =>
        ShellOpen.Open("x-apple.systempreferences:com.apple.preference.security?Privacy_ScreenCapture");
}
