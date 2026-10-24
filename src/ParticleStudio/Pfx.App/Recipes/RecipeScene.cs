using System.Text;
using Pfx.Core.Editing;
using Pfx.Core.Model;
using Pfx.Core.Text;

namespace Pfx.App.Recipes;

// What a recipe run works on: a copy of the project (committed as one project edit) and the live token trees of the
// parts. Part edits are applied at once, so later steps of the run see them, and kept in order per document; the shell
// takes them back and replays them through each document's own history, so one undo step covers the whole run.
public sealed class RecipeScene
{
    private readonly Func<PartEntry, PtcDocument?> _open;
    private readonly Func<PartEntry, PtcDocument> _create;
    private readonly List<(PtcDocument Doc, IEdit Edit)> _edits = new();

    // open: the document of a project part (null = not open); create: a document for a new part (an empty effect, or
    // the open document at that path when one is there). folder: where new files go (the project folder or, for an
    // unsaved project, the folder of the part the run started from).
    // partDisk: the file of a part on disk (absolute); gamePath: the game path of a folder (relative to the data root
    // that holds it, null when none does).
    public RecipeScene(ProjectFile project, Func<PartEntry, PtcDocument?> open, Func<PartEntry, PtcDocument> create,
        Func<string, string?> resolve, Func<string, (int U, int V)?> tiles, Func<PartEntry, string> partDisk, Func<string, string?> gamePath, string? folder)
    {
        Project = project;
        _open = open;
        _create = create;
        Resolve = resolve;
        Tiles = tiles;
        PartDisk = partDisk;
        GamePath = gamePath;
        Folder = folder;
    }

    public ProjectFile Project { get; }
    public Func<string, string?> Resolve { get; }
    public Func<string, (int U, int V)?> Tiles { get; }
    public Func<PartEntry, string> PartDisk { get; }
    public Func<string, string?> GamePath { get; }
    public string? Folder { get; }
    // Messages of the run: language keys with arguments (a change it could not take back, a missing part).
    public List<(string Key, object[] Args)> Notes { get; } = new();
    // Files the run wrote on disk (material copies); the shell lists them, nothing else writes outside documents.
    public List<string> Written { get; } = new();

    public IEnumerable<PtcDocument> Touched => _edits.Select(e => e.Doc).Distinct();

    public IReadOnlyList<IEdit> EditsOf(PtcDocument doc) => _edits.Where(e => e.Doc == doc).Select(e => e.Edit).ToList();

    public PartEntry? Part(string id) => Project.Parts.FirstOrDefault(p => p.Id == id);

    public PtcDocument? Doc(string partId) => Part(partId) is { } p ? _open(p) : null;

    public PtcDocument Create(PartEntry entry) => _create(entry);

    public void Run(PtcDocument doc, IEdit? edit)
    {
        if (edit == null)
            return;
        edit.Apply();
        _edits.Add((doc, edit));
    }

    // Takes every part edit back, newest first (before the shell replays them through the documents, or when a run fails).
    public void Unwind()
    {
        for (int i = _edits.Count - 1; i >= 0; i--)
            _edits[i].Edit.Revert();
    }

    public static string Text(EnfItem item)
    {
        var sb = new StringBuilder();
        item.Write(sb);
        return sb.ToString();
    }

    // One item (a key line, a graph block or an emitter) parsed from its written text, leading whitespace included.
    public static EnfItem Item(string text) => EnfDocument.Parse(text).Items.Single();

    public static int IndexOfKey(EnfBlock block, string key) =>
        block.Items.FindIndex(i => EnfEdits.KeyOf(i).Equals(key, StringComparison.OrdinalIgnoreCase));
}
