using Avalonia.Markup.Xaml;
using SnapMark.Core;

namespace SnapMark.Localization;

/// <summary>
/// Перевод в XAML: Text="{l:Tr 'Начать запись'}". Ключ — исходная русская строка (см. Strings.tsv).
/// Язык применяется при создании окна; после смены языка приложение перезапускается.
/// </summary>
public sealed class TrExtension : MarkupExtension
{
    public TrExtension() { }
    public TrExtension(string key) => Key = key;

    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) => L.T(Key);
}
