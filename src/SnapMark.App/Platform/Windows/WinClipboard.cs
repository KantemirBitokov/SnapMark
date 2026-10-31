using SnapMark.Core;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using static SnapMark.Platform.Windows.Native;

namespace SnapMark.Platform.Windows;

/// <summary>
/// Буфер обмена напрямую через Win32, чтобы положить сразу несколько форматов:
///   CF_UNICODETEXT — текст комментариев (Блокнот, мессенджеры, чаты);
///   "HTML Format"  — картинки + текст вперемешку (Word, Outlook, Google Docs);
///   CF_HDROP       — файлы (Проводник, Telegram, загрузка в веб-формы);
///   "PNG" + CF_DIB — одиночная картинка (Paint, Photoshop, браузеры).
/// Принимающее приложение само выбирает понятный ему формат.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WinClipboard : IRichClipboard
{
    IntPtr _owner;
    readonly uint _cfHtml = RegisterClipboardFormat("HTML Format");
    readonly uint _cfPng = RegisterClipboardFormat("PNG");
    readonly uint _cfRtf = RegisterClipboardFormat("Rich Text Format");

    public Task CopyAsync(ClipboardPayload p)
    {
        EnsureOwnerWindow();

        bool opened = false;
        for (int i = 0; i < 15 && !(opened = OpenClipboard(_owner)); i++)
            Thread.Sleep(25); // буфер мог быть занят другим процессом
        if (!opened) throw new InvalidOperationException(L.T("Буфер обмена занят другим приложением."));

        try
        {
            EmptyClipboard();
            if (p.Text is { Length: > 0 }) SetData(CF_UNICODETEXT, Encoding.Unicode.GetBytes(p.Text + "\0"));
            if (p.Rtf is { Length: > 0 }) SetData(_cfRtf, Encoding.ASCII.GetBytes(p.Rtf + "\0"));
            if (p.Html is { Length: > 0 }) SetData(_cfHtml, BuildCfHtml(p.Html));
            if (p.Files is { Count: > 0 }) SetData(CF_HDROP, BuildDropFiles(p.Files));
            if (p.Png is { Length: > 0 }) SetData(_cfPng, p.Png);
            if (p.Dib is { Length: > 0 }) SetData(CF_DIB, p.Dib);
        }
        finally
        {
            CloseClipboard();
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// OpenClipboard(NULL) + EmptyClipboard делает владельцем NULL, и тогда SetClipboardData может отказать.
    /// Поэтому держим собственное невидимое message-only окно.
    /// </summary>
    void EnsureOwnerWindow()
    {
        if (_owner != IntPtr.Zero) return;
        const string className = "SnapMark.ClipboardOwner";
        var hInstance = GetModuleHandle(null);
        _wndProc ??= new WndProc(DefWindowProc); // держим ссылку на делегат, чтобы его не собрал GC
        var wc = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = hInstance,
            lpszClassName = className,
        };
        RegisterClassEx(ref wc);
        _owner = CreateWindowEx(0, className, "", 0, 0, 0, 0, 0, HWND_MESSAGE, IntPtr.Zero, hInstance, IntPtr.Zero);
    }

    static WndProc? _wndProc;

    static void SetData(uint format, byte[] data)
    {
        var hMem = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)data.Length);
        if (hMem == IntPtr.Zero) return;
        var ptr = GlobalLock(hMem);
        Marshal.Copy(data, 0, ptr, data.Length);
        GlobalUnlock(hMem);
        // При успехе памятью владеет система; при ошибке освобождаем сами.
        if (SetClipboardData(format, hMem) == IntPtr.Zero) GlobalFree(hMem);
    }

    /// <summary>Формат CF_HTML: заголовок со смещениями в байтах UTF-8.</summary>
    static byte[] BuildCfHtml(string fragment)
    {
        const string header =
            "Version:0.9\r\nStartHTML:{0:D10}\r\nEndHTML:{1:D10}\r\nStartFragment:{2:D10}\r\nEndFragment:{3:D10}\r\n";
        const string pre = "<html><head><meta charset=\"utf-8\"></head><body>\r\n<!--StartFragment-->";
        const string post = "<!--EndFragment-->\r\n</body></html>";

        var utf8 = Encoding.UTF8;
        int headerLen = utf8.GetByteCount(string.Format(header, 0, 0, 0, 0));
        int startHtml = headerLen;
        int startFragment = startHtml + utf8.GetByteCount(pre);
        int endFragment = startFragment + utf8.GetByteCount(fragment);
        int endHtml = endFragment + utf8.GetByteCount(post);

        var full = string.Format(header, startHtml, endHtml, startFragment, endFragment) + pre + fragment + post;
        var bytes = utf8.GetBytes(full);
        Array.Resize(ref bytes, bytes.Length + 1); // завершающий \0
        return bytes;
    }

    /// <summary>DROPFILES (20 байт) + пути в UTF-16 через \0 + двойной \0 в конце.</summary>
    static byte[] BuildDropFiles(IReadOnlyList<string> files)
    {
        var list = Encoding.Unicode.GetBytes(string.Join("\0", files) + "\0\0");
        var buf = new byte[20 + list.Length];
        BitConverter.GetBytes(20).CopyTo(buf, 0);  // pFiles — смещение списка
        // pt.x, pt.y, fNC = 0
        BitConverter.GetBytes(1).CopyTo(buf, 16);  // fWide = TRUE (Unicode)
        list.CopyTo(buf, 20);
        return buf;
    }
}
