using Pfx.Core.Editing;
using Pfx.Core.Schema;
using Pfx.Core.Text;

namespace Pfx.Core.Model;

public sealed class EmitterView : KeyedBlock
{
    internal EmitterView(EnfDocument doc, EnfBlock block, PfxSchema schema) : base(doc, block, SchemaScope.Emitor, schema)
    {
    }

    public string Name => Block.Name ?? "";

    public IEdit Rename(string name) => EnfEdits.Rename(Block, name);
}

// EffectDef { MinFPS .. Emitors { EmitorDef a { .. } .. } }
public sealed class PtcDocument : KeyedBlock
{
    private PtcDocument(EnfDocument doc, EnfBlock root, PfxSchema schema) : base(doc, root, SchemaScope.Effect, schema)
    {
    }

    public static PtcDocument From(EnfDocument doc, PfxSchema? schema = null)
    {
        var root = doc.Root ?? throw new InvalidDataException("No EffectDef block");
        if (!root.Key.Equals("EffectDef", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Root block is '{root.Key}', not EffectDef");
        return new PtcDocument(doc, root, schema ?? PfxSchema.Shared);
    }

    public static PtcDocument Load(string path) => From(EnfDocument.Load(path));

    public EnfBlock? EmitorsBlock => Block.FindBlock("Emitors");

    public IReadOnlyList<EmitterView> Emitters =>
        EmitorsBlock?.Items.OfType<EnfBlock>()
            .Where(b => b.Key.Equals("EmitorDef", StringComparison.OrdinalIgnoreCase))
            .Select(b => new EmitterView(Doc, b, Schema)).ToList() ?? new List<EmitterView>();

    public EmitterView? Emitter(string name) => Emitters.FirstOrDefault(e => e.Name == name);

    // Adds an empty EmitorDef at the end (all keys at engine defaults); creates Emitors if the file has none.
    public IEdit AddEmitter(string name)
    {
        var emitors = EmitorsBlock;
        if (emitors != null)
            return EnfEdits.AddChildBlock(Doc, emitors, "EmitorDef", name, emitors.Items.Count);
        var createEmitors = EnfEdits.AddChildBlock(Doc, Block, "Emitors", null, Block.Items.Count);
        return new DeferredEdit(createEmitors, () =>
            EnfEdits.AddChildBlock(Doc, EmitorsBlock!, "EmitorDef", name, 0));
    }

    public IEdit RemoveEmitter(EmitterView emitter) => EnfEdits.RemoveItem(EmitorsBlock!, emitter.Block);

    // A copy of the emitter's tokens (same keys, spelling and whitespace) under a new name, right after the source,
    // where Workbench puts its clones.
    public IEdit CloneEmitter(EmitterView source, string name)
    {
        var sb = new System.Text.StringBuilder();
        source.Block.Write(sb);
        var copy = EnfDocument.Parse(sb.ToString()).Root!;
        if (copy.Name != null)
            copy.Header[^1] = new EnfToken(EnfTokenKind.Word, copy.Header[^1].Leading, name);
        else
            copy.Header.Add(new EnfToken(EnfTokenKind.Word, " ", name));
        var items = EmitorsBlock!.Items;
        return new SpliceEdit<EnfItem>(items, items.IndexOf(source.Block) + 1, 0, new[] { copy });
    }

    // newIndex counts emitters after the move, like a list drag-and-drop target.
    public IEdit MoveEmitter(EmitterView emitter, int newIndex)
    {
        var items = EmitorsBlock!.Items;
        var others = items.Where(i => i != emitter.Block).ToList();
        var emitterBlocks = others.OfType<EnfBlock>().ToList();
        int target = newIndex >= emitterBlocks.Count ? others.Count : others.IndexOf(emitterBlocks[newIndex]);
        return EnfEdits.MoveItem(EmitorsBlock, emitter.Block, target);
    }

    public byte[] ToBytes() => Doc.ToBytes();

    // The second step needs the block the first one creates, so it is built only when the first has run.
    private sealed class DeferredEdit : IEdit
    {
        private readonly IEdit _first;
        private readonly Func<IEdit> _second;
        private IEdit? _built;

        public DeferredEdit(IEdit first, Func<IEdit> second)
        {
            _first = first;
            _second = second;
        }

        public void Apply()
        {
            _first.Apply();
            _built ??= _second();
            _built.Apply();
        }

        public void Revert()
        {
            _built!.Revert();
            _first.Revert();
        }
    }
}
