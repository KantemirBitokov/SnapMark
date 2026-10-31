using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using SnapMark.Core;
using SnapMark.Platform;
using SnapMark.Platform.Windows;

namespace SnapMark.UI;

/// <summary>
/// Кнопка записи сочетания клавиш: клик → «Нажми сочетание…» → нажать нужное.
/// Esc — отмена, Backspace/Delete — очистить. Пока идёт запись, глобальные хоткеи приостановлены,
/// иначе наш же хук «съел» бы PrtSc.
/// </summary>
public sealed class HotkeyBox : Button
{
    Hotkey _value = new();
    bool _listening;

    protected override Type StyleKeyOverride => typeof(Button);

    public event Action<Hotkey>? ValueChanged;

    public Hotkey Value
    {
        get => _value;
        set
        {
            _value = value;
            UpdateText();
        }
    }

    public HotkeyBox()
    {
        Classes.Add("hotkey");
        HorizontalAlignment = HorizontalAlignment.Right;
        Click += (_, _) =>
        {
            if (!_listening) StartListening();
        };
        LostFocus += (_, _) => StopListening();
        UpdateText();
    }

    void StartListening()
    {
        _listening = true;
        Classes.Set("listening", true);
        Content = L.T("Нажми сочетание…");
        PlatformServices.Hotkeys.Suspended = true;
    }

    void StopListening()
    {
        if (!_listening) return;
        _listening = false;
        Classes.Set("listening", false);
        PlatformServices.Hotkeys.Suspended = false;
        UpdateText();
    }

    void UpdateText() => Content = _value.ToString();

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (!_listening)
        {
            base.OnKeyDown(e);
            return;
        }
        e.Handled = true;
        Accept(e.Key, e.KeyModifiers);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        // Windows присылает для PrtSc только отпускание клавиши.
        if (_listening && e.Key == Key.PrintScreen)
        {
            e.Handled = true;
            Accept(e.Key, e.KeyModifiers);
            return;
        }
        base.OnKeyUp(e);
    }

    void Accept(Key key, KeyModifiers mods)
    {
        if (KeyNames.IsModifier(key)) return; // ждём основную клавишу

        if (key == Key.Escape)
        {
            StopListening();
            return;
        }

        if (key is Key.Back or Key.Delete && mods == KeyModifiers.None)
        {
            _value = new Hotkey();
            StopListening();
            ValueChanged?.Invoke(_value);
            return;
        }

        _value = new Hotkey
        {
            Key = KeyNames.Normalize(key),
            Ctrl = mods.HasFlag(KeyModifiers.Control),
            Shift = mods.HasFlag(KeyModifiers.Shift),
            Alt = mods.HasFlag(KeyModifiers.Alt),
            Win = mods.HasFlag(KeyModifiers.Meta),
        };
        StopListening();
        ValueChanged?.Invoke(_value);
    }
}
