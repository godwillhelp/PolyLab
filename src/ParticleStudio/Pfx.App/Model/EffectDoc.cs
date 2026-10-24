using Pfx.App.Core;
using Pfx.Core.Editing;
using Pfx.Core.Model;
using Pfx.Core.Text;
using System.Numerics;
using Pfx.Render;

namespace Pfx.App.Model;

// A material file used by the emitters of an open effect. Edits go into the effect's undo history.
public sealed class MaterialDoc
{
    public MaterialDoc(string gamePath, string diskPath)
    {
        GamePath = gamePath;
        DiskPath = diskPath;
        Original = File.ReadAllBytes(diskPath);
        Emat = EmatDocument.From(EnfDocument.Parse(Original));
        OriginalRoot = EnfDocument.Parse(Original).Root;
    }

    // The path as the emitter writes it ("{GUID}Graphics/...") and the file it resolved to.
    public string GamePath { get; }
    public string DiskPath { get; }
    public byte[] Original { get; private set; }
    public EmatDocument Emat { get; }
    // The material as it is on disk (parsed again after each save), for the "changed against the file" marks.
    public EnfBlock? OriginalRoot { get; private set; }

    public bool Dirty => !Emat.ToBytes().AsSpan().SequenceEqual(Original);

    public void Saved(byte[] bytes)
    {
        Original = bytes;
        OriginalRoot = EnfDocument.Parse(bytes).Root;
    }
}

// Script-side source motion drawn with the path tool (spec 3.7 uses 3 and 4): a keyframed path (world positions of
// the source at times from 0, straight between keys, looping) or a spin in degrees per second. The game runs such
// motion from the mod's script; the .ptc has no key for it, so it lives in the document for the session (the project
// file and its export as script data come with P10).
public sealed record ScriptMotion(float[]? Times, Vector3[]? Points, bool YawAlongPath, float SpinRate)
{
    public bool IsPath => Points is { Length: > 0 } && Times != null;
}

// One open .ptc file = one part of the scene: its token tree (lossless), the materials it uses, one undo history with
// labels for the status bar, and the bytes it was opened with (dirty = the bytes differ, so an undo back to the
// original is clean again).
public sealed class EffectDoc : IUndoOwner
{
    private readonly Dictionary<EnfBlock, EnfBlock> _originals = new();
    private readonly List<string> _done = new();
    private readonly List<string> _undone = new();
    private string? _mergeKey;

    private EffectDoc(string path, byte[] bytes, PtcDocument ptc)
    {
        Path = path;
        Original = bytes;
        Ptc = ptc;
        PairOriginals();
    }

    // Pairs the current blocks with a parse of the bytes on disk: the effect root, and emitters by position. Block
    // objects survive edits (edits splice tokens inside them), so renames and value edits keep their pair; an emitter
    // added since has none, so all its keys count as changed.
    private void PairOriginals()
    {
        _originals.Clear();
        if (Original.Length == 0)
            return;
        var original = PtcDocument.From(EnfDocument.Parse(Original));
        _originals[Ptc.Block] = original.Block;
        var now = Ptc.Emitters;
        var then = original.Emitters;
        for (int i = 0; i < Math.Min(now.Count, then.Count); i++)
            _originals[now[i].Block] = then[i].Block;
    }

    public EnfBlock? OriginalOf(EnfBlock block) => _originals.TryGetValue(block, out var b) ? b : null;

    public string Path { get; private set; }
    public string Title => System.IO.Path.GetFileName(Path);
    public byte[] Original { get; private set; }
    public PtcDocument Ptc { get; }
    public UndoStack Undo { get; } = new();
    public Dictionary<string, MaterialDoc> Materials { get; } = new(StringComparer.OrdinalIgnoreCase);
    // Emitters hidden or soloed in the preview: view state only, never written to the file (Workbench check box, W0).
    public HashSet<EnfBlock> Hidden { get; } = new();
    public HashSet<EnfBlock> Solo { get; } = new();
    public HashSet<EnfBlock> Locked { get; } = new();

    public event Action? Changed;

    // Undoable like a key edit (MotionEdit), never written to the .ptc.
    public ScriptMotion? Motion { get; private set; }

    public IEdit SetMotion(ScriptMotion? motion) => new MotionEdit(this, motion);

    // The motion a project saved for this part, handed over when the project opens it (not an edit).
    public void InitMotion(ScriptMotion? motion) => Motion = motion;

    private sealed class MotionEdit : IEdit
    {
        private readonly EffectDoc _doc;
        private readonly ScriptMotion? _value;
        private ScriptMotion? _old;

        public MotionEdit(EffectDoc doc, ScriptMotion? value)
        {
            _doc = doc;
            _value = value;
        }

        public void Apply()
        {
            _old = _doc.Motion;
            _doc.Motion = _value;
        }

        public void Revert() => _doc.Motion = _old;
    }

    // Undo entries; a tool that applied the top one can take it back when its own result is replaced.
    public int UndoCount => _done.Count;
    public int RedoCount => _undone.Count;

    public static EffectDoc Open(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        return new EffectDoc(path, bytes, PtcDocument.From(EnfDocument.Parse(bytes)));
    }

    // A part that exists only here until it is saved (a recipe made it): an empty effect with nothing on disk, so it is
    // dirty from the start and every key counts as changed.
    public static EffectDoc Create(string path)
    {
        byte[] bytes = EnfLexer.FileEncoding.GetBytes("EffectDef {\r\n Emitors {\r\n }\r\n}\r\n");
        return new EffectDoc(path, Array.Empty<byte>(), PtcDocument.From(EnfDocument.Parse(bytes)));
    }

    public bool Dirty => !Ptc.ToBytes().AsSpan().SequenceEqual(Original) || Materials.Values.Any(m => m.Dirty);

    public string? UndoLabel => _done.Count > 0 ? _done[^1] : null;
    public string? RedoLabel => _undone.Count > 0 ? _undone[^1] : null;

    // Materials are looked up again after every structural change (an added emitter may use a new one).
    public void LoadMaterials(DataRoots roots)
    {
        foreach (var e in Ptc.Emitters)
        {
            if (e.Text("Material") is not { } game || Materials.ContainsKey(game))
                continue;
            roots.AddRootFor(Path, game);
            if (roots.Resolve(game) is { } disk)
            {
                try
                {
                    Materials[game] = new MaterialDoc(game, disk);
                }
                catch (Exception ex) when (ex is IOException or EnfSyntaxException or InvalidDataException or UnauthorizedAccessException)
                {
                }
            }
        }
    }

    // Steps with the same merge key in a row (one drag) become one undo step with one label.
    public void Do(IEdit edit, string label, string? mergeKey = null)
    {
        Undo.Do(edit, mergeKey);
        if (mergeKey != null && mergeKey == _mergeKey && _done.Count > 0)
            _done[^1] = label;
        else
            _done.Add(label);
        _mergeKey = mergeKey;
        _undone.Clear();
        Changed?.Invoke();
    }

    // Ends a drag: the next step starts a new undo entry.
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
        // UndoStack keeps the merge key of the step below; sealing stops a later drag from merging into it.
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

    // Writes only what changed: an untouched file is not rewritten at all (stays byte for byte as opened).
    public int Save(string? newPath = null)
    {
        int written = 0;
        if (newPath != null)
            Path = newPath;
        byte[] bytes = Ptc.ToBytes();
        if (newPath != null || !bytes.AsSpan().SequenceEqual(Original))
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!);
            AtomicFile.Write(Path, bytes);
            Original = bytes;
            PairOriginals();
            written++;
        }
        foreach (var m in Materials.Values.Where(m => m.Dirty))
        {
            byte[] mb = m.Emat.ToBytes();
            AtomicFile.Write(m.DiskPath, mb);
            m.Saved(mb);
            written++;
        }
        Changed?.Invoke();
        return written;
    }

}
