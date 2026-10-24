namespace Pfx.Core.Editing;

public interface IEdit
{
    void Apply();
    void Revert();
}

// Replaces Count elements at Index with Insert; remembers what it removed so Revert puts back the same objects.
public sealed class SpliceEdit<T> : IEdit
{
    private readonly List<T> _list;
    private readonly int _index;
    private readonly int _count;
    private readonly List<T> _insert;
    private List<T>? _removed;

    public SpliceEdit(List<T> list, int index, int count, IEnumerable<T> insert)
    {
        _list = list;
        _index = index;
        _count = count;
        _insert = insert.ToList();
    }

    public void Apply()
    {
        _removed = _list.GetRange(_index, _count);
        _list.RemoveRange(_index, _count);
        _list.InsertRange(_index, _insert);
    }

    public void Revert()
    {
        _list.RemoveRange(_index, _insert.Count);
        _list.InsertRange(_index, _removed!);
    }
}

public sealed class CompositeEdit : IEdit
{
    private readonly List<IEdit> _edits;

    public CompositeEdit(IEnumerable<IEdit> edits)
    {
        _edits = edits.ToList();
    }

    public void Apply()
    {
        foreach (var e in _edits)
            e.Apply();
    }

    public void Revert()
    {
        for (int i = _edits.Count - 1; i >= 0; i--)
            _edits[i].Revert();
    }
}

public sealed class UndoStack
{
    private readonly List<(IEdit Edit, string? MergeKey)> _done = new();
    private readonly List<(IEdit Edit, string? MergeKey)> _undone = new();

    public bool CanUndo => _done.Count > 0;
    public bool CanRedo => _undone.Count > 0;

    // Edits with the same merge key in a row (the steps of one drag) undo as one.
    public void Do(IEdit edit, string? mergeKey = null)
    {
        edit.Apply();
        _undone.Clear();
        if (mergeKey != null && _done.Count > 0 && _done[^1].MergeKey == mergeKey)
            _done[^1] = (new CompositeEdit(new[] { _done[^1].Edit, edit }), mergeKey);
        else
            _done.Add((edit, mergeKey));
    }

    // Ends the current merge run, so the next edit with the same key starts a new undo step.
    public void Seal()
    {
        if (_done.Count > 0)
            _done[^1] = (_done[^1].Edit, null);
    }

    public void Undo()
    {
        if (!CanUndo)
            return;
        var top = _done[^1];
        _done.RemoveAt(_done.Count - 1);
        top.Edit.Revert();
        _undone.Add((top.Edit, null));
    }

    public void Redo()
    {
        if (!CanRedo)
            return;
        var top = _undone[^1];
        _undone.RemoveAt(_undone.Count - 1);
        top.Edit.Apply();
        _done.Add(top);
    }
}
