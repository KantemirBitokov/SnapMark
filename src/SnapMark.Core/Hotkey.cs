namespace SnapMark.Core;

/// <summary>
/// Сочетание клавиш. Key хранится как имя клавиши Avalonia (Key enum), например "PrintScreen", "F9", "S".
/// Перевод в коды конкретной ОС делает платформенный слой.
/// </summary>
public sealed class Hotkey
{
    public string Key { get; set; } = "";
    public bool Ctrl { get; set; }
    public bool Shift { get; set; }
    public bool Alt { get; set; }
    public bool Win { get; set; }

    public bool IsEmpty => string.IsNullOrWhiteSpace(Key);

    public Hotkey Clone() => new() { Key = Key, Ctrl = Ctrl, Shift = Shift, Alt = Alt, Win = Win };

    public bool SameAs(Hotkey? other) =>
        other is not null && !IsEmpty &&
        string.Equals(Key, other.Key, StringComparison.OrdinalIgnoreCase) &&
        Ctrl == other.Ctrl && Shift == other.Shift && Alt == other.Alt && Win == other.Win;

    public override string ToString()
    {
        if (IsEmpty) return L.T("Не задано");
        var parts = new List<string>(5);
        if (OperatingSystem.IsMacOS())
        {
            // Привычная запись для Mac: ⌃⌥⇧⌘1
            var mac = (Ctrl ? "⌃" : "") + (Alt ? "⌥" : "") + (Shift ? "⇧" : "") + (Win ? "⌘" : "");
            return mac + DisplayKey(Key);
        }
        if (Ctrl) parts.Add("Ctrl");
        if (Alt) parts.Add("Alt");
        if (Shift) parts.Add("Shift");
        if (Win) parts.Add(OperatingSystem.IsLinux() ? "Super" : "Win");
        parts.Add(DisplayKey(Key));
        return string.Join(" + ", parts);
    }

    public static string DisplayKey(string key) => key switch
    {
        "PrintScreen" or "Snapshot" => "PrtSc",
        "Scroll" => "ScrollLock",
        "Insert" => "Ins",
        "Delete" => "Del",
        "PageUp" or "Prior" => "PgUp",
        "PageDown" or "Next" => "PgDn",
        "OemTilde" => "`",
        "OemMinus" => "-",
        "OemPlus" => "=",
        _ when key.Length == 2 && key[0] == 'D' && char.IsDigit(key[1]) => key[1].ToString(),
        _ when key.StartsWith("NumPad", StringComparison.Ordinal) => "Num " + key[6..],
        _ => key,
    };
}
