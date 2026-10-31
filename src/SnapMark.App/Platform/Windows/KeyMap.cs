using Avalonia.Input;

namespace SnapMark.Platform.Windows;

/// <summary>Имя клавиши Avalonia → виртуальный код Win32 (VK_*).</summary>
internal static class KeyMap
{
    public static uint ToVirtualKey(string keyName)
    {
        if (!Enum.TryParse<Key>(keyName, true, out var key)) return 0;

        if (key >= Key.A && key <= Key.Z) return (uint)(0x41 + (key - Key.A));
        if (key >= Key.D0 && key <= Key.D9) return (uint)(0x30 + (key - Key.D0));
        if (key >= Key.F1 && key <= Key.F24) return (uint)(0x70 + (key - Key.F1));
        if (key >= Key.NumPad0 && key <= Key.NumPad9) return (uint)(0x60 + (key - Key.NumPad0));

        return key switch
        {
            Key.PrintScreen => 0x2C,
            Key.Pause => 0x13,
            Key.Scroll => 0x91,
            Key.Insert => 0x2D,
            Key.Delete => 0x2E,
            Key.Home => 0x24,
            Key.End => 0x23,
            Key.PageUp => 0x21,
            Key.PageDown => 0x22,
            Key.Space => 0x20,
            Key.Enter => 0x0D,
            Key.Tab => 0x09,
            Key.Up => 0x26,
            Key.Down => 0x28,
            Key.Left => 0x25,
            Key.Right => 0x27,
            Key.OemTilde => 0xC0,
            Key.OemMinus => 0xBD,
            Key.OemPlus => 0xBB,
            Key.Multiply => 0x6A,
            Key.Add => 0x6B,
            Key.Subtract => 0x6D,
            Key.Divide => 0x6F,
            _ => 0,
        };
    }
}

/// <summary>Нормализация имени клавиши (у Avalonia есть синонимы: Snapshot = PrintScreen и т.п.).</summary>
public static class KeyNames
{
    public static string Normalize(Key key)
    {
        if (key == Key.PrintScreen) return "PrintScreen";
        if (key == Key.PageUp) return "PageUp";
        if (key == Key.PageDown) return "PageDown";
        if (key == Key.Enter) return "Enter";
        if (key == Key.CapsLock) return "CapsLock";
        return key.ToString();
    }

    public static bool IsModifier(Key key) => key is
        Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or
        Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin;
}
