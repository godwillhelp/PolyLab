using System.Numerics;
using Pfx.App.Core;
using Pfx.Core.Editing;
using Pfx.Core.Model;

namespace Pfx.App.Model;

// The open project (.plpfx): the composition data and its undo history. Every edit replaces the whole ProjectFile
// object with an edited copy (a snapshot edit), so undo and redo swap complete objects and views find items again by
// id. Script motion of a part (spin, drawn path) lives in its EffectDoc while the part is open (the path tool edits it
// there, in the part's own history); Snapshot() takes it from the open parts, Open hands it to them.
public sealed class CompositionDoc : IUndoOwner
{
    private readonly List<Func<string>> _done = new();
    private readonly List<Func<string>> _undone = new();
    private string? _mergeKey;
    private byte[] _saved;

    public CompositionDoc(string? path, ProjectFile file)
    {
        Path = path;
        File = file;
        _saved = path == null ? Array.Empty<byte>() : Composition.Bytes(file);
    }

    // Null until the project is saved somewhere (O7: wherever the user chooses).
    public string? Path { get; private set; }
    public ProjectFile File { get; private set; }
    public UndoStack Undo { get; } = new();
    public string Title => Path != null ? System.IO.Path.GetFileName(Path) : (File.Name.Length > 0 ? File.Name : "—") + ".plpfx";
    public string? Folder => Path != null ? System.IO.Path.GetDirectoryName(Path) : null;
    public int UndoCount => _done.Count;
    public int RedoCount => _undone.Count;
    public string? UndoLabel => _done.Count > 0 ? _done[^1]() : null;

    // The parts' open documents, by part id (set by the shell).
    public Func<PartEntry, EffectDoc?> DocOf { get; set; } = _ => null;

    public event Action? Changed;

    public static CompositionDoc Open(string path) => new(path, Composition.Parse(System.IO.File.ReadAllBytes(path)));

    public string Resolve(string relative) =>
        System.IO.Path.GetFullPath(System.IO.Path.Combine(Folder ?? Environment.CurrentDirectory, relative.Replace('/', System.IO.Path.DirectorySeparatorChar)));

    // A path stored in the project: relative to the project folder when it has one (spec 3.13), else as it is.
    public string Relative(string full) =>
        Folder == null ? full : System.IO.Path.GetRelativePath(Folder, full).Replace(System.IO.Path.DirectorySeparatorChar, '/');

    public bool Dirty => Path == null || !Composition.Bytes(Snapshot()).AsSpan().SequenceEqual(_saved);

    // The project as it would be saved: a copy with each open part's script motion.
    public ProjectFile Snapshot()
    {
        var copy = Composition.Parse(Composition.Bytes(File));
        foreach (var part in copy.Parts)
        {
            if (DocOf(File.Parts.First(p => p.Id == part.Id)) is not { } doc)
                continue;
            var m = doc.Motion;
            part.Spin = m is { IsPath: false } ? m.SpinRate : 0;
            part.PathKeys = m is { IsPath: true }
                ? new PathKeys { Times = m.Times!.ToArray(), Points = m.Points!.Select(p => new[] { p.X, p.Y, p.Z }).ToArray(), YawAlong = m.YawAlongPath }
                : null;
        }
        return copy;
    }

    public static ScriptMotion? MotionOf(PartEntry part) => part.PathKeys is { Times.Length: > 0 } k
        ? new ScriptMotion(k.Times.ToArray(), k.Points.Select(p => new Vector3(p[0], p[1], p[2])).ToArray(), k.YawAlong, 0)
        : part.Spin != 0 ? new ScriptMotion(null, null, false, part.Spin) : null;

    // One undoable change: `edit` changes a copy of the project. Steps with the same merge key in a row (one drag in a
    // field) become one undo step.
    public void Do(Action<ProjectFile> edit, Func<string> label, string? mergeKey = null)
    {
        var before = File;
        var after = Composition.Parse(Composition.Bytes(File));
        edit(after);
        Undo.Do(new SwapEdit(this, before, after), mergeKey);
        if (mergeKey != null && mergeKey == _mergeKey && _done.Count > 0)
            _done[^1] = label;
        else
            _done.Add(label);
        _mergeKey = mergeKey;
        _undone.Clear();
        Changed?.Invoke();
    }

    // One undoable change that replaces the whole project with a finished copy (a recipe run built it).
    public void Replace(ProjectFile after, Func<string> label)
    {
        Undo.Do(new SwapEdit(this, File, after));
        _done.Add(label);
        _mergeKey = null;
        _undone.Clear();
        Changed?.Invoke();
    }

    public void Seal()
    {
        Undo.Seal();
        _mergeKey = null;
    }

    public void UndoStep()
    {
        if (!Undo.CanUndo)
            return;
        Undo.Undo();
        Undo.Seal();
        _mergeKey = null;
        _undone.Add(_done[^1]);
        _done.RemoveAt(_done.Count - 1);
        Changed?.Invoke();
    }

    public void RedoStep()
    {
        if (!Undo.CanRedo)
            return;
        Undo.Redo();
        Undo.Seal();
        _mergeKey = null;
        _done.Add(_undone[^1]);
        _undone.RemoveAt(_undone.Count - 1);
        Changed?.Invoke();
    }

    public void Save(string? newPath = null)
    {
        if (newPath != null)
        {
            // Relative paths keep pointing at the same files from the new folder.
            var snap = Snapshot();
            string? oldFolder = Folder;
            Path = System.IO.Path.GetFullPath(newPath);
            Rebase(snap, oldFolder, Folder!);
            File = snap;
        }
        if (Path == null)
            throw new InvalidOperationException("The project has no path yet.");
        var bytes = Composition.Bytes(Snapshot());
        AtomicFile.Write(Path, bytes);
        _saved = bytes;
        Changed?.Invoke();
    }

    // Paths of a project saved into a folder: absolute ones (a project not saved before) and ones relative to the old
    // folder become relative to the new one, so they keep pointing at the same files.
    private static void Rebase(ProjectFile p, string? from, string to)
    {
        string Move(string rel) => System.IO.Path.IsPathRooted(rel) ? System.IO.Path.GetRelativePath(to, rel).Replace('\\', '/')
            : from == null ? rel
            : System.IO.Path.GetRelativePath(to, System.IO.Path.GetFullPath(System.IO.Path.Combine(from, rel))).Replace('\\', '/');
        foreach (var part in p.Parts)
            part.Path = Move(part.Path);
        for (int i = 0; i < p.Materials.Count; i++)
            p.Materials[i] = Move(p.Materials[i]);
        foreach (var s in p.Sounds)
            if (s.File != null)
                s.File = Move(s.File);
    }

    private sealed class SwapEdit : IEdit
    {
        private readonly CompositionDoc _doc;
        private readonly ProjectFile _before;
        private readonly ProjectFile _after;

        public SwapEdit(CompositionDoc doc, ProjectFile before, ProjectFile after)
        {
            _doc = doc;
            _before = before;
            _after = after;
        }

        public void Apply() => _doc.File = _after;

        public void Revert() => _doc.File = _before;
    }
}
