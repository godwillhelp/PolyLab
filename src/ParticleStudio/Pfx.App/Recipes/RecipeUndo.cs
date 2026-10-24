using System.Text.Json;
using Pfx.Core.Editing;
using Pfx.Core.Model;
using Pfx.Core.Text;

namespace Pfx.App.Recipes;

// Takes a node's changes back by replaying its log backwards. A change the user has made something else of since (a key
// edited again, an emitter renamed) is left as the user made it and named in the scene's notes: the node never
// overwrites later work.
public static class RecipeUndo
{
    public static void Revert(RecipeNode node, RecipeScene scene)
    {
        for (int i = node.Log.Count - 1; i >= 0; i--)
            Revert(node.Log[i], scene);
        node.Log.Clear();
    }

    private static void Revert(RecipeOp op, RecipeScene scene)
    {
        var p = scene.Project;
        switch (op.Op)
        {
            case RecipeOp.EmitterAdd:
            {
                if (scene.Doc(op.Part!) is not { } doc || doc.Emitter(op.Emitter!) is not { } e)
                {
                    scene.Notes.Add(("Rcp.Note.EmitterGone", new object[] { op.Emitter!, op.Part! }));
                    return;
                }
                scene.Run(doc, doc.RemoveEmitter(e));
                return;
            }
            case RecipeOp.EmitterRemove:
            {
                if (scene.Doc(op.Part!) is not { } doc)
                {
                    scene.Notes.Add(("Rcp.Note.PartGone", new object[] { op.Part! }));
                    return;
                }
                var emitors = doc.EmitorsBlock;
                if (emitors == null)
                {
                    scene.Run(doc, EnfEdits.AddChildBlock(doc.Doc, doc.Block, "Emitors", null, doc.Block.Items.Count));
                    emitors = doc.EmitorsBlock!;
                }
                var block = RecipeScene.Item(op.Before!);
                scene.Run(doc, new SpliceEdit<EnfItem>(emitors.Items, Math.Min(op.Index, emitors.Items.Count), 0, new[] { block }));
                return;
            }
            case RecipeOp.Key:
            {
                if (scene.Doc(op.Part!) is not { } doc)
                    return;
                KeyedBlock? kb = op.Emitter == null ? doc : doc.Emitter(op.Emitter);
                if (kb == null)
                {
                    scene.Notes.Add(("Rcp.Note.EmitterGone", new object[] { op.Emitter!, op.Part! }));
                    return;
                }
                int at = RecipeScene.IndexOfKey(kb.Block, op.KeyName!);
                string? now = at >= 0 ? RecipeScene.Text(kb.Block.Items[at]) : null;
                if (now != op.After)
                {
                    scene.Notes.Add(("Rcp.Note.KeyChanged", new object[] { op.KeyName!, op.Emitter ?? op.Part! }));
                    return;
                }
                var items = kb.Block.Items;
                if (op.Before == null)
                    scene.Run(doc, new SpliceEdit<EnfItem>(items, at, 1, Array.Empty<EnfItem>()));
                else if (at >= 0)
                    scene.Run(doc, new SpliceEdit<EnfItem>(items, at, 1, new[] { RecipeScene.Item(op.Before) }));
                else
                    scene.Run(doc, new SpliceEdit<EnfItem>(items, Math.Clamp(op.Index, 0, items.Count), 0, new[] { RecipeScene.Item(op.Before) }));
                return;
            }
            case RecipeOp.PartText:
            {
                if (scene.Doc(op.Part!) is not { } doc)
                    return;
                if (RecipeContext.Hash(doc.Doc.Write()) != op.After)
                {
                    scene.Notes.Add(("Rcp.Note.PartChanged", new object[] { op.Part! }));
                    return;
                }
                scene.Run(doc, new ItemsEdit(doc.Doc, EnfDocument.Parse(op.Before!)));
                return;
            }
            case RecipeOp.ItemAdd:
            {
                if (p.Find(op.Item!) is not { } item)
                    return;
                foreach (var s in p.States)
                    s.Items.RemoveAll(x => x.Ref == op.Item);
                Remove(p, item);
                return;
            }
            case RecipeOp.ItemRemove:
            {
                IProjectItem item = op.KeyName switch
                {
                    nameof(ItemKind.Part) => JsonSerializer.Deserialize<PartEntry>(op.Before!, Composition.Json)!,
                    nameof(ItemKind.Light) => JsonSerializer.Deserialize<LightEntry>(op.Before!, Composition.Json)!,
                    nameof(ItemKind.Haze) => JsonSerializer.Deserialize<HazeEntry>(op.Before!, Composition.Json)!,
                    nameof(ItemKind.Sound) => JsonSerializer.Deserialize<SoundEntry>(op.Before!, Composition.Json)!,
                    nameof(ItemKind.Screen) => JsonSerializer.Deserialize<ScreenEntry>(op.Before!, Composition.Json)!,
                    nameof(ItemKind.Strike) => JsonSerializer.Deserialize<StrikeEntry>(op.Before!, Composition.Json)!,
                    _ => JsonSerializer.Deserialize<FlashEntry>(op.Before!, Composition.Json)!,
                };
                if (p.Find(item.Id) != null)
                {
                    scene.Notes.Add(("Rcp.Note.IdTaken", new object[] { item.Id }));
                    return;
                }
                Add(p, item);
                return;
            }
            case RecipeOp.Field:
            {
                if (scene.Part(op.Item!) is not { } part)
                    return;
                if (PartFields.Json(PartFields.Get(part, op.KeyName!)) != op.After)
                {
                    scene.Notes.Add(("Rcp.Note.KeyChanged", new object[] { op.KeyName!, op.Item! }));
                    return;
                }
                PartFields.Set(part, op.KeyName!, PartFields.Parse(op.Before));
                return;
            }
            case RecipeOp.StateAdd:
            {
                var s = p.States.FirstOrDefault(x => x.Name == op.State);
                int k = s?.Items.FindLastIndex(x => x.Ref == op.Item) ?? -1;
                if (k >= 0)
                    s!.Items.RemoveAt(k);
                return;
            }
            case RecipeOp.StateRemove:
            {
                if (p.States.FirstOrDefault(x => x.Name == op.State) is { } s)
                    s.Items.Insert(Math.Min(op.Index, s.Items.Count), new StateItem { Ref = op.Item!, Delay = op.Delay });
                return;
            }
            case RecipeOp.StateNew:
            {
                if (p.States.FirstOrDefault(x => x.Name == op.State) is { Items.Count: 0 } s)
                    p.States.Remove(s);
                return;
            }
            case RecipeOp.FileMade:
            {
                string path = op.Before!;
                if (!File.Exists(path))
                    return;
                if (Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))) != op.After)
                {
                    scene.Notes.Add(("Rcp.Note.FileChanged", new object[] { path }));
                    return;
                }
                File.Delete(path);
                return;
            }
        }
    }

    public static void Add(ProjectFile p, IProjectItem item)
    {
        switch (item)
        {
            case PartEntry x: p.Parts.Add(x); break;
            case LightEntry x: p.Lights.Add(x); break;
            case HazeEntry x: p.Hazes.Add(x); break;
            case SoundEntry x: p.Sounds.Add(x); break;
            case ScreenEntry x: p.Screens.Add(x); break;
            case StrikeEntry x: p.Strikes.Add(x); break;
            case FlashEntry x: p.Flashes.Add(x); break;
        }
    }

    public static void Remove(ProjectFile p, IProjectItem item)
    {
        switch (item)
        {
            case PartEntry x: p.Parts.Remove(x); break;
            case LightEntry x: p.Lights.Remove(x); break;
            case HazeEntry x: p.Hazes.Remove(x); break;
            case SoundEntry x: p.Sounds.Remove(x); break;
            case ScreenEntry x: p.Screens.Remove(x); break;
            case StrikeEntry x: p.Strikes.Remove(x); break;
            case FlashEntry x: p.Flashes.Remove(x); break;
        }
    }

    // Replaces a document's whole text (a generator's file) but keeps its root block object, which the part's document
    // and its emitter views hold: the root's tokens and items are swapped, and the old ones come back on revert.
    public sealed class ItemsEdit : IEdit
    {
        private readonly EnfDocument _doc;
        private readonly EnfDocument _new;
        private Snapshot? _old;

        public ItemsEdit(EnfDocument doc, EnfDocument replacement)
        {
            _doc = doc;
            _new = replacement;
        }

        private sealed record Snapshot(List<EnfItem> DocItems, string Trailing, List<EnfToken> Header, string OpenLeading, List<EnfItem> Items, string CloseLeading);

        private static Snapshot Take(EnfDocument d, EnfBlock root) =>
            new(d.Items.ToList(), d.Trailing, root.Header.ToList(), root.Open.Leading, root.Items.ToList(), root.Close.Leading);

        private void Put(Snapshot s, EnfBlock? source)
        {
            var root = _doc.Root!;
            root.Header.Clear();
            root.Header.AddRange(s.Header);
            root.Open.Leading = s.OpenLeading;
            root.Items.Clear();
            root.Items.AddRange(s.Items);
            root.Close.Leading = s.CloseLeading;
            _doc.Items.Clear();
            _doc.Items.AddRange(s.DocItems.Select(i => i == source ? root : i));
            _doc.Trailing = s.Trailing;
        }

        public void Apply()
        {
            var root = _doc.Root!;
            _old = Take(_doc, root);
            Put(Take(_new, _new.Root!), _new.Root);
        }

        public void Revert() => Put(_old!, _doc.Root);
    }
}
