using System.Collections.ObjectModel;
using Pfx.App.Core;
using Pfx.App.Model;
using Pfx.Core.Model;
using Pfx.Core.Text;
using Suite.Ui.Core;

namespace Pfx.App.ViewModels;

public sealed class DocTab : ObservableObject
{
    private bool _active;

    public DocTab(EffectDoc doc) => Doc = doc;

    public EffectDoc Doc { get; }
    public string Title => Doc.Title;
    public string Tip => Doc.Path;
    public bool Dirty => Doc.Dirty;
    public bool Active { get => _active; set => Set(ref _active, value); }

    public void Refresh() => RaiseAll();
}

// A row of the layers panel: a file (= ptc part) header or one of its emitters, or a recipe node of the project and
// what it made (those have no document of their own).
public abstract class LayerRow : ObservableObject
{
    protected LayerRow(EffectDoc? doc) => Doc = doc;

    public EffectDoc? Doc { get; }
    public abstract string Name { get; }
    public abstract string Tip { get; }
    public virtual void Refresh() => RaiseAll();
}

public sealed class PartRow : LayerRow
{
    public PartRow(EffectDoc doc) : base(doc) => Part = doc;

    public EffectDoc Part { get; }
    public override string Name => Part.Title;
    public override string Tip => Loc.F("Tip.PartRow", Part.Path);
    public bool Dirty => Part.Dirty;
    public string Count => Loc.F("Val.Emitters", Part.Ptc.Emitters.Count);
}

// A recipe node of the project (spec 3.10: "shows what it created as children in the layers panel").
public sealed class RecipeRow : LayerRow
{
    public RecipeRow(RecipeNode node, string name, string statusBrush) : base(null)
    {
        Node = node;
        Title = name;
        StatusBrush = statusBrush;
    }

    public RecipeNode Node { get; }
    public string Title { get; }
    public string StatusBrush { get; }
    public override string Name => Title + " · " + Node.Id;
    public override string Tip => Loc.F("Tip.RecipeRow", Title, Node.Id, Node.Log.Count);
    public bool Off => !Node.On;
}

// One change a node made (an emitter, a light, a key it set); clicking it selects what it names.
public sealed class MadeRow : LayerRow
{
    public MadeRow(RecipeNode node, RecipeOp op, string text) : base(null)
    {
        Node = node;
        Op = op;
        Text = text;
    }

    public RecipeNode Node { get; }
    public RecipeOp Op { get; }
    public string Text { get; }
    public override string Name => Text;
    public override string Tip => Loc.T("Tip.MadeRow");
}

public sealed class EmitterRow : LayerRow
{
    private bool _renaming;
    private string? _renameProblem;

    public EmitterRow(EffectDoc doc, EnfBlock block) : base(doc)
    {
        Block = block;
        Part = doc;
    }

    public EnfBlock Block { get; }
    public EffectDoc Part { get; }
    public EmitterView? View => Part.Ptc.Emitters.FirstOrDefault(e => e.Block == Block);
    public override string Name => Block.Name ?? Loc.T("Val.Unnamed");
    public override string Tip => Loc.F("Tip.EmitterRow", Name, View?.Text("Material") ?? Loc.T("Val.NoMaterial"));
    public bool Visible { get => !Part.Hidden.Contains(Block); set { if (value) Part.Hidden.Remove(Block); else Part.Hidden.Add(Block); Raise(); Raise(nameof(Dim)); Changed?.Invoke(); } }
    public bool IsSolo { get => Part.Solo.Contains(Block); set { if (value) Part.Solo.Add(Block); else Part.Solo.Remove(Block); Raise(); Changed?.Invoke(); } }
    public bool IsLocked { get => Part.Locked.Contains(Block); set { if (value) Part.Locked.Add(Block); else Part.Locked.Remove(Block); Raise(); Changed?.Invoke(); } }
    public bool Dim => !Visible || (Part.Solo.Count > 0 && !Part.Solo.Contains(Block));
    public bool Modified { get; set; }
    public System.Windows.Media.Color Swatch { get; private set; }
    public bool Renaming { get => _renaming; set { Set(ref _renaming, value); RenameProblem = null; } }
    public string? RenameProblem { get => _renameProblem; set { Set(ref _renameProblem, value); Raise(nameof(HasRenameProblem)); } }
    public bool HasRenameProblem => _renameProblem != null;

    // Eye, solo or lock changed: the preview and the inspector follow.
    public event Action? Changed;

    public override void Refresh()
    {
        var v = View;
        var color = v?.Numbers("Color");
        var g = Pfx.Sim.Graph.FromNumbers(color ?? new double[] { 0, 1, 1, 1 }, 3);
        static byte B(float x) => (byte)Math.Round(Math.Clamp(x, 0f, 1f) * 255);
        Swatch = System.Windows.Media.Color.FromRgb(B(g.Evaluate(0.3f, 0)), B(g.Evaluate(0.3f, 1)), B(g.Evaluate(0.3f, 2)));
        base.Refresh();
    }
}

public sealed class Toast : ObservableObject
{
    public Toast(string text, string kind)
    {
        Text = text;
        Brush = kind switch { "ok" => "B.Ok", "warn" => "B.Warn", "err" => "B.Err", _ => "B.Info" };
    }

    public string Text { get; }
    public string Brush { get; }
}

public sealed class StatusItem
{
    public StatusItem(string text, string tip, string brush = "B.TextDim")
    {
        Text = text;
        Tip = tip;
        Brush = brush;
    }

    public string Text { get; }
    public string Tip { get; }
    public string Brush { get; }
}

public sealed class ShellState
{
    public ObservableCollection<DocTab> Tabs { get; } = new();
    public ObservableCollection<LayerRow> Layers { get; } = new();
    public ObservableCollection<Toast> Toasts { get; } = new();
    public ObservableCollection<StatusItem> Status { get; } = new();
}
