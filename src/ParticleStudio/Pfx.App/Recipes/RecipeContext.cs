using System.Globalization;
using System.Text.Json;
using Pfx.App.Core;
using Pfx.Core.Editing;
using Pfx.Core.Model;
using Pfx.Core.Text;

namespace Pfx.App.Recipes;

// A target as the run sees it: the part entry, its open document and the emitter (null = the whole part).
public sealed record Target(PartEntry Part, PtcDocument Doc, EmitterView? Emitter);

// One run of a recipe for one node: its parameter values, its targets and the changes it makes. Every change goes
// through here and is written into the node's log with what it takes to undo it (RecipeUndo replays the log backwards).
public sealed class RecipeContext
{
    public RecipeContext(Recipe recipe, RecipeNode node, RecipeScene scene)
    {
        Recipe = recipe;
        Node = node;
        Scene = scene;
    }

    public Recipe Recipe { get; }
    public RecipeNode Node { get; }
    // The log of the node's previous run (a re-apply): the files it wrote are its own to write again.
    public IReadOnlyList<RecipeOp> Previous { get; init; } = Array.Empty<RecipeOp>();
    public RecipeScene Scene { get; }
    public ProjectFile Project => Scene.Project;

    // ---- parameters ----

    public string Text(string key) => Node.Params.TryGetValue(key, out var v) ? v : Recipe.Param(key).Default;

    public double Number(string key) =>
        double.TryParse(Text(key), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? Math.Clamp(d, Recipe.Param(key).Min, Recipe.Param(key).Max)
            : double.Parse(Recipe.Param(key).Default, CultureInfo.InvariantCulture);

    public bool Flag(string key) => Text(key) == "1";

    // The state named by the "state" parameter, else the first state; null when the project has none.
    public string? State()
    {
        string wanted = Text("state");
        return Project.States.FirstOrDefault(s => s.Name == wanted)?.Name ?? Project.States.FirstOrDefault()?.Name;
    }

    // ---- targets ----

    // Emitter targets as named in the node; a part target means all of its emitters. Missing ones are left out.
    public IReadOnlyList<Target> Emitters()
    {
        var list = new List<Target>();
        foreach (var t in Node.Targets)
        {
            if (Scene.Part(t.Part) is not { } part || Scene.Doc(t.Part) is not { } doc)
                continue;
            if (t.Emitter == null)
                list.AddRange(doc.Emitters.Select(e => new Target(part, doc, e)));
            else if (doc.Emitter(t.Emitter) is { } e)
                list.Add(new Target(part, doc, e));
        }
        return list;
    }

    public IReadOnlyList<Target> Parts()
    {
        var list = new List<Target>();
        foreach (var id in Node.Targets.Select(t => t.Part).Distinct())
            if (Scene.Part(id) is { } part && Scene.Doc(id) is { } doc)
                list.Add(new Target(part, doc, null));
        return list;
    }

    // ---- part changes ----

    public string AddEmitter(string partId, string text, string? name = null)
    {
        var doc = Scene.Doc(partId) ?? throw new InvalidOperationException("Part " + partId + " is not open");
        var block = (EnfBlock)RecipeScene.Item(text);
        string wanted = name ?? block.Name ?? "emitor1";
        string unique = Names.Unique(wanted, doc.Emitters.Select(e => e.Name));
        if (block.Name == null)
            block.Header.Add(new EnfToken(EnfTokenKind.Word, " ", unique));
        else if (unique != block.Name)
            block.Header[^1] = new EnfToken(EnfTokenKind.Word, block.Header[^1].Leading, unique);
        var emitors = doc.EmitorsBlock;
        if (emitors == null)
        {
            Scene.Run(doc, EnfEdits.AddChildBlock(doc.Doc, doc.Block, "Emitors", null, doc.Block.Items.Count));
            emitors = doc.EmitorsBlock!;
        }
        Relead(block, doc.Doc, emitors);
        int index = emitors.Items.Count;
        Scene.Run(doc, new SpliceEdit<EnfItem>(emitors.Items, index, 0, new EnfItem[] { block }));
        Log(new RecipeOp { Op = RecipeOp.EmitterAdd, Part = partId, Emitter = unique, Index = index });
        return unique;
    }

    // Removes an emitter and keeps its text, so taking the node back puts it where it was.
    public string RemoveEmitter(string partId, string name)
    {
        var doc = Scene.Doc(partId)!;
        var e = doc.Emitter(name) ?? throw new InvalidOperationException("No emitter " + name);
        int index = doc.EmitorsBlock!.Items.IndexOf(e.Block);
        string text = RecipeScene.Text(e.Block);
        Scene.Run(doc, doc.RemoveEmitter(e));
        Log(new RecipeOp { Op = RecipeOp.EmitterRemove, Part = partId, Emitter = name, Index = index, Before = text });
        return text;
    }

    // Moves an emitter into another part (its text unchanged, its name made unique there); returns the new name.
    public string MoveEmitter(string fromPart, string name, string toPart)
    {
        string text = RemoveEmitter(fromPart, name);
        return AddEmitter(toPart, text, name);
    }

    // One key of an emitter (null = the effect block): the edit is built by `make` on the live block.
    public void SetKey(string partId, string? emitter, string key, Func<KeyedBlock, IEdit?> make)
    {
        var doc = Scene.Doc(partId)!;
        KeyedBlock block = emitter == null ? doc : doc.Emitter(emitter) ?? throw new InvalidOperationException("No emitter " + emitter);
        int at = RecipeScene.IndexOfKey(block.Block, key);
        string? before = at >= 0 ? RecipeScene.Text(block.Block.Items[at]) : null;
        Scene.Run(doc, make(block));
        int now = RecipeScene.IndexOfKey(block.Block, key);
        string? after = now >= 0 ? RecipeScene.Text(block.Block.Items[now]) : null;
        if (before == after)
            return;
        Log(new RecipeOp { Op = RecipeOp.Key, Part = partId, Emitter = emitter, KeyName = key, Index = at, Before = before, After = after });
    }

    public void Set(Target t, string key, params double[] values) => SetKey(t.Part.Id, t.Emitter?.Name, key, b => b.Set(key, values));

    public void SetWord(Target t, string key, string word) => SetKey(t.Part.Id, t.Emitter?.Name, key, b => b.SetWord(key, word));

    public void SetString(Target t, string key, string value) => SetKey(t.Part.Id, t.Emitter?.Name, key, b => b.SetString(key, value));

    public void SetGraph(Target t, string key, IReadOnlyList<double> numbers) => SetKey(t.Part.Id, t.Emitter?.Name, key, b => b.SetGraph(key, numbers));

    public void RemoveKey(Target t, string key) => SetKey(t.Part.Id, t.Emitter?.Name, key, b => b.Reset(key));

    // The whole text of a part (generators write their parts as files): the old text is kept when there was one.
    public void WritePart(string partId, byte[] bytes)
    {
        var doc = Scene.Doc(partId)!;
        string before = doc.Doc.Write();
        Scene.Run(doc, new RecipeUndo.ItemsEdit(doc.Doc, EnfDocument.Parse(bytes)));
        Log(new RecipeOp { Op = RecipeOp.PartText, Part = partId, Before = before, After = Hash(doc.Doc.Write()) });
    }

    public static string Hash(string text) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(EnfLexer.FileEncoding.GetBytes(text)));

    // ---- project changes ----

    public string NewId(string stem) => ProjectNames.Unique(ProjectNames.Id(stem), Project.Items().Select(i => i.Id));

    public T AddItem<T>(T item) where T : IProjectItem
    {
        RecipeUndo.Add(Project, item);
        Log(new RecipeOp { Op = RecipeOp.ItemAdd, Item = item.Id, After = (item as PartEntry)?.Path });
        return item;
    }

    // A part at a given file (a generator's output): its entry and its document (the open one at that path, else the
    // file, else a new empty effect).
    public PartEntry AddPart(PartEntry entry)
    {
        AddItem(entry);
        Scene.Create(entry);
        return entry;
    }

    // A new empty state at the end (a generator in a project without states).
    public string NewState(string name)
    {
        string unique = ProjectNames.Unique(name, Project.States.Select(s => s.Name));
        Project.States.Add(new StateEntry { Name = unique });
        Log(new RecipeOp { Op = RecipeOp.StateNew, State = unique });
        return unique;
    }

    // A new part: its entry (with the script data of `like`, when given) and an empty document at a free file name
    // beside `like`'s file or in the scene folder.
    public PartEntry NewPart(string stem, PartEntry? like, string? folder = null)
    {
        string id = NewId(stem);
        string dir = folder ?? (like != null ? Path.GetDirectoryName(like.Path)!.Replace('\\', '/') : "");
        string path = (dir.Length > 0 ? dir + "/" : "") + FreeFile(dir, id);
        return AddPart(new PartEntry { Id = id, Path = path, Spin = like?.Spin ?? 0, Near = like?.Near ?? 0, Stop = like?.Stop });
    }

    private string FreeFile(string dir, string id)
    {
        var used = Project.Parts.Select(p => Path.GetFileName(p.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (int n = 1; ; n++)
        {
            string name = (n == 1 ? id : id + n) + ".ptc";
            string? disk = Scene.Folder != null ? Path.Combine(Scene.Folder, dir.Replace('/', Path.DirectorySeparatorChar), name) : null;
            if (!used.Contains(name) && (disk == null || !System.IO.File.Exists(disk)))
                return name;
        }
    }

    // Removes an item with its state entries; the item's JSON and its state places go into the log.
    public void RemoveItem(string id)
    {
        var item = Project.Find(id) ?? throw new InvalidOperationException("No item " + id);
        foreach (var (s, i) in Project.States.Select((s, i) => (s, i)).ToList())
            for (int k = s.Items.Count - 1; k >= 0; k--)
                if (s.Items[k].Ref == id)
                    RemoveFromState(s.Name, k);
        string json = JsonSerializer.Serialize(item, item.GetType(), Composition.Json);
        RecipeUndo.Remove(Project, item);
        Log(new RecipeOp { Op = RecipeOp.ItemRemove, Item = id, KeyName = item.Kind.ToString(), Before = json });
    }

    // A script value of a part (spin, near, stop, around); stored as JSON texts so null (no stop) round-trips.
    public void SetField(string partId, string field, float? value)
    {
        var p = Scene.Part(partId)!;
        float? before = PartFields.Get(p, field);
        if (before == value)
            return;
        PartFields.Set(p, field, value);
        Log(new RecipeOp { Op = RecipeOp.Field, Item = partId, KeyName = field, Before = PartFields.Json(before), After = PartFields.Json(value) });
    }

    // Adds an item to a state (by name) at its end.
    public void AddToState(string state, string id, float delay = 0)
    {
        var s = Project.States.FirstOrDefault(x => x.Name == state);
        if (s == null)
            return;
        s.Items.Add(new StateItem { Ref = id, Delay = delay });
        Log(new RecipeOp { Op = RecipeOp.StateAdd, State = state, Item = id, Index = s.Items.Count - 1, Delay = delay });
    }

    // The states that start `like` (with its delay) also start `id`.
    public void AddToStatesOf(string like, string id)
    {
        foreach (var s in Project.States.ToList())
            if (s.Items.FirstOrDefault(x => x.Ref == like) is { } it)
                AddToState(s.Name, id, it.Delay);
    }

    public void RemoveFromState(string state, int index)
    {
        var s = Project.States.First(x => x.Name == state);
        var it = s.Items[index];
        s.Items.RemoveAt(index);
        Log(new RecipeOp { Op = RecipeOp.StateRemove, State = state, Item = it.Ref, Index = index, Delay = it.Delay });
    }

    public void RemoveFromStates(string id)
    {
        foreach (var s in Project.States.ToList())
            for (int k = s.Items.Count - 1; k >= 0; k--)
                if (s.Items[k].Ref == id)
                    RemoveFromState(s.Name, k);
    }

    // A file the run made on disk (a material copy): taking the node back deletes it if nobody changed it since.
    public void MadeFile(string path, byte[] bytes)
    {
        Scene.Written.Add(path);
        Log(new RecipeOp { Op = RecipeOp.FileMade, Before = path, After = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)) });
    }

    public void Note(string key, params object[] args) => Scene.Notes.Add((key, args));

    private void Log(RecipeOp op) => Node.Log.Add(op);

    private static string Indent(string leading)
    {
        int nl = leading.LastIndexOf('\n');
        return nl < 0 ? leading : leading[(nl + 1)..];
    }

    // An emitter from another file takes the line break and indentation of this file's emitters.
    public static void Relead(EnfBlock block, EnfDocument doc, EnfBlock emitors)
    {
        string indent = emitors.Items.OfType<EnfBlock>().FirstOrDefault() is { } first ? Indent(first.First.Leading) : Indent(emitors.First.Leading) + " ";
        Relead(block, doc.NewLine, indent);
    }

    // Every line break inside the block's text follows the file (CRLF or LF); the block starts on its own line at
    // `indent`, and its inner lines keep their indentation relative to the block's first line.
    private static void Relead(EnfBlock block, string newline, string indent)
    {
        string old = Indent(block.First.Leading);
        string Fix(string leading)
        {
            int nl = leading.LastIndexOf('\n');
            if (nl < 0)
                return leading;
            string inner = leading[(nl + 1)..];
            string rest = inner.StartsWith(old, StringComparison.Ordinal) ? indent + inner[old.Length..] : indent + inner;
            int lines = leading.Count(c => c == '\n');
            return string.Concat(Enumerable.Repeat(newline, lines)) + rest;
        }
        void Walk(EnfItem item)
        {
            switch (item)
            {
                case EnfLine l:
                    foreach (var t in l.Tokens)
                        t.Leading = Fix(t.Leading);
                    break;
                case EnfBlock b:
                    foreach (var t in b.Header)
                        t.Leading = Fix(t.Leading);
                    b.Open.Leading = Fix(b.Open.Leading);
                    foreach (var c in b.Items)
                        Walk(c);
                    b.Close.Leading = Fix(b.Close.Leading);
                    break;
            }
        }
        if (!block.First.Leading.Contains('\n'))
            block.Header[0].Leading = "\n" + old;
        Walk(block);
    }
}

// The script values of a part a recipe may set, by the names its log uses.
public static class PartFields
{
    public static float? Get(PartEntry p, string field) => field switch
    {
        "spin" => p.Spin,
        "near" => p.Near,
        "stop" => p.Stop,
        "around" => p.Around,
        "swirl" => p.Swirl,
        "swirl_min" => p.SwirlMin,
        _ => throw new ArgumentException(field),
    };

    public static void Set(PartEntry p, string field, float? v)
    {
        switch (field)
        {
            case "spin": p.Spin = v ?? 0; break;
            case "near": p.Near = v ?? 0; break;
            case "stop": p.Stop = v; break;
            case "around": p.Around = v ?? 0; break;
            case "swirl": p.Swirl = v ?? 0; break;
            case "swirl_min": p.SwirlMin = v ?? 0; break;
            default: throw new ArgumentException(field);
        }
    }

    public static string Json(float? v) => v is float f ? f.ToString("R", CultureInfo.InvariantCulture) : "null";

    public static float? Parse(string? s) => s == null || s == "null" ? null : float.Parse(s, CultureInfo.InvariantCulture);
}
