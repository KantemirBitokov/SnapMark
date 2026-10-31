using SnapMark.Core;

namespace SnapMark.Annotations;

/// <summary>
/// Список аннотаций одного скриншота + история отмены.
/// Комментарии к меткам хранятся отдельно, по постоянному Id метки, и не участвуют в undo —
/// иначе отмена рисования стирала бы набранный текст, а перенумерация путала бы комментарии.
/// </summary>
public sealed class AnnotationDocument
{
    readonly Stack<List<Annotation>> _undo = new();
    readonly Stack<List<Annotation>> _redo = new();
    long _sequence;

    public List<Annotation> Items { get; } = new();
    public Dictionary<Guid, string> Comments { get; } = new();

    /// <summary>A, B, C… или 1, 2, 3… Смена перенумеровывает все метки скриншота.</summary>
    public MarkerNumbering Numbering { get; set; } = MarkerNumbering.Letters;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public List<Annotation> Snapshot() => Items.Select(a => a.Clone()).ToList();

    /// <summary>Запомнить состояние ДО изменения (снимок сделан заранее через Snapshot()).</summary>
    public void PushUndo(List<Annotation> before)
    {
        _undo.Push(before);
        _redo.Clear();
    }

    public bool Undo()
    {
        if (_undo.Count == 0) return false;
        _redo.Push(Snapshot());
        Restore(_undo.Pop());
        return true;
    }

    public bool Redo()
    {
        if (_redo.Count == 0) return false;
        _undo.Push(Snapshot());
        Restore(_redo.Pop());
        return true;
    }

    void Restore(List<Annotation> state)
    {
        Items.Clear();
        Items.AddRange(state);
    }

    public IEnumerable<MarkerAnnotation> MarkersInOrder() =>
        Items.OfType<MarkerAnnotation>().OrderBy(m => m.Sequence);

    public string GetComment(MarkerAnnotation m) => Comments.TryGetValue(m.Id, out var c) ? c : "";

    /// <summary>Новая метка: следующая буква и номер по порядку.</summary>
    public MarkerAnnotation NewMarker()
    {
        var count = Items.OfType<MarkerAnnotation>().Count();
        return new MarkerAnnotation { Label = LabelFor(count), Sequence = ++_sequence };
    }

    /// <summary>
    /// После удаления метки буквы идут подряд: было A, B, C → удалили B → стало A, B.
    /// Комментарии привязаны к Id, поэтому «переезжают» вместе с меткой.
    /// </summary>
    public void RelabelMarkers()
    {
        int i = 0;
        foreach (var m in MarkersInOrder().ToList()) m.Label = LabelFor(i++);
    }

    string LabelFor(int index) =>
        Numbering == MarkerNumbering.Numbers ? (index + 1).ToString() : IndexToLabel(index);

    public static string IndexToLabel(int index)
    {
        var s = "";
        index++;
        while (index > 0)
        {
            index--;
            s = (char)('A' + index % 26) + s;
            index /= 26;
        }
        return s;
    }
}
