using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace SnapMark.Platform.Mac;

/// <summary>Минимальные вызовы системных библиотек macOS (Objective-C runtime, CoreGraphics, Carbon).</summary>
[SupportedOSPlatform("macos")]
internal static class MacNative
{
    const string ObjC = "/usr/lib/libobjc.A.dylib";
    const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    public const string Carbon = "/System/Library/Frameworks/Carbon.framework/Carbon";

    [DllImport(ObjC)] public static extern IntPtr objc_getClass(string name);
    [DllImport(ObjC)] public static extern IntPtr sel_registerName(string name);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")] public static extern IntPtr Send(IntPtr receiver, IntPtr selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] public static extern byte SendPtrRetBool(IntPtr receiver, IntPtr selector, IntPtr arg);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] public static extern void SendBool(IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.I1)] bool value);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] public static extern void SendLong(IntPtr receiver, IntPtr selector, long value);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] public static extern void SendULong(IntPtr receiver, IntPtr selector, ulong value);

    /// <summary>Есть ли у программы разрешение «Запись экрана» (macOS 10.15+).</summary>
    [DllImport(CoreGraphics)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool CGPreflightScreenCaptureAccess();

    /// <summary>Показать системный запрос разрешения «Запись экрана» (один раз).</summary>
    [DllImport(CoreGraphics)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool CGRequestScreenCaptureAccess();

    public static IntPtr Sel(string name) => sel_registerName(name);

    /// <summary>[obj respondsToSelector:] — защита от «unrecognized selector», который роняет процесс.</summary>
    public static bool Responds(IntPtr obj, string selector) =>
        obj != IntPtr.Zero && SendPtrRetBool(obj, Sel("respondsToSelector:"), Sel(selector)) != 0;

    /// <summary>[NSApp activateIgnoringOtherApps:YES] — программа из строки меню получает фокус клавиатуры.</summary>
    public static void ActivateApp()
    {
        try
        {
            var app = Send(objc_getClass("NSApplication"), Sel("sharedApplication"));
            if (app != IntPtr.Zero) SendBool(app, Sel("activateIgnoringOtherApps:"), true);
        }
        catch { }
    }

    /// <summary>NSWindow по дескриптору Avalonia (на macOS это NSView окна).</summary>
    public static IntPtr NSWindowOf(Avalonia.Controls.Window window)
    {
        try
        {
            var handle = window.TryGetPlatformHandle();
            if (handle is null || handle.Handle == IntPtr.Zero) return IntPtr.Zero;
            var h = handle.Handle;
            if (Responds(h, "setSharingType:")) return h;                       // уже NSWindow
            if (Responds(h, "window")) return Send(h, Sel("window"));           // NSView → NSWindow
            return IntPtr.Zero;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }
}
