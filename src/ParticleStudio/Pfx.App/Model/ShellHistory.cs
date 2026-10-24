namespace Pfx.App.Model;

// A document with its own undo history: an open part (EffectDoc) or the project (CompositionDoc).
public interface IUndoOwner
{
    int UndoCount { get; }
    int RedoCount { get; }
    string? UndoLabel { get; }
    void UndoStep();
    void RedoStep();
}

// One undo history over every open document (spec 3.8: "one undo history"): each document keeps its own steps, this
// keeps the order in which they were made, so Ctrl+Z takes back the newest step wherever it was made (a light in the
// project, then an emitter value in a part). Sync is called after every change of a document; it adds that
// document's new steps, or drops the ones it took back itself (a tool replacing its own result). A group (a recipe run
// that changes the project and several parts) is one entry: undo takes back the steps of all its documents.
public sealed class ShellHistory
{
    private readonly List<List<IUndoOwner>> _done = new();
    private readonly List<List<IUndoOwner>> _undone = new();
    private List<IUndoOwner>? _group;

    public IUndoOwner? Top => _done.Count > 0 ? _done[^1][^1] : null;
    public bool CanUndo => _done.Count > 0;
    public bool CanRedo => _undone.Count > 0;

    private static int Count(List<List<IUndoOwner>> entries, IUndoOwner owner) => entries.Sum(e => e.Count(o => o == owner));

    public void Sync(IUndoOwner owner)
    {
        int done = Count(_done, owner);
        if (owner.UndoCount > done)
        {
            for (int i = done; i < owner.UndoCount; i++)
            {
                if (_group != null)
                {
                    if (_group.Count == 0)
                        _done.Add(_group);
                    _group.Add(owner);
                }
                else
                {
                    _done.Add(new List<IUndoOwner> { owner });
                }
            }
            _undone.Clear();
        }
        for (; done > owner.UndoCount; done--)
            RemoveLast(_done, owner);
        int undone = Count(_undone, owner);
        for (; undone > owner.RedoCount; undone--)
            RemoveLast(_undone, owner);
        for (; undone < owner.RedoCount; undone++)
            _undone.Add(new List<IUndoOwner> { owner });
    }

    private static void RemoveLast(List<List<IUndoOwner>> entries, IUndoOwner owner)
    {
        for (int i = entries.Count - 1; i >= 0; i--)
        {
            int k = entries[i].LastIndexOf(owner);
            if (k < 0)
                continue;
            entries[i].RemoveAt(k);
            if (entries[i].Count == 0)
                entries.RemoveAt(i);
            return;
        }
    }

    // Every step synced until the returned scope is disposed joins one entry.
    public IDisposable Group()
    {
        _group = new List<IUndoOwner>();
        return new GroupScope(this);
    }

    private sealed class GroupScope : IDisposable
    {
        private readonly ShellHistory _history;

        public GroupScope(ShellHistory history) => _history = history;

        public void Dispose() => _history._group = null;
    }

    public void Forget(IUndoOwner owner)
    {
        foreach (var list in new[] { _done, _undone })
        {
            foreach (var e in list)
                e.RemoveAll(o => o == owner);
            list.RemoveAll(e => e.Count == 0);
        }
    }

    // The entry moves first, then each document steps (newest first): a document's Sync then finds its counts equal.
    public void Undo()
    {
        if (_done.Count == 0)
            return;
        var entry = _done[^1];
        _done.RemoveAt(_done.Count - 1);
        _undone.Add(entry);
        for (int i = entry.Count - 1; i >= 0; i--)
            entry[i].UndoStep();
    }

    public void Redo()
    {
        if (_undone.Count == 0)
            return;
        var entry = _undone[^1];
        _undone.RemoveAt(_undone.Count - 1);
        _done.Add(entry);
        foreach (var owner in entry)
            owner.RedoStep();
    }
}
