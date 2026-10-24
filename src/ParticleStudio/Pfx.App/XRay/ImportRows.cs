using Pfx.XRay;
using Suite.Ui.Core;

namespace Pfx.App.XRay;

// Rows of the import tab and the X-Ray panel. Texts are composed when shown (the language can change).
public sealed class KindRow
{
    public required string Title { get; init; }
    public required string How { get; init; }
    public required string Count { get; init; }
    public required string Brush { get; init; }
    public required string Tip { get; init; }
}

public sealed class RootRow : ObservableObject
{
    private bool _selected;
    public required XrFoundRoot Root { get; init; }
    public required string Name { get; init; }
    public required string Path { get; init; }
    public required string Effects { get; init; }
    public required string Overrides { get; init; }
    public required string OverridesBrush { get; init; }
    public required string Tip { get; init; }
    public bool Core => Root.Core;
    public bool CanToggle => !Root.Core;
    public bool Selected { get => _selected; set => Set(ref _selected, value); }
}

public sealed class SkipRow
{
    public required string Reason { get; init; }
    public required string Count { get; init; }
    public required string Tip { get; init; }
}

public sealed class ArchiveRow : ObservableObject
{
    private bool _asking;
    public required string File { get; init; }
    public required string Name { get; init; }
    public required string Target { get; init; }
    public required string Tip { get; init; }
    public bool Asking { get => _asking; set => Set(ref _asking, value); }
}

public sealed class AnomalyRow
{
    public required XrAnomalyPlan Plan { get; init; }
    public required string Name { get; init; }
    public required string Info { get; init; }
    public required string Status { get; init; }
    public required string Brush { get; init; }
    public required string Tip { get; init; }
}

public sealed class SourceRow
{
    public required XrSource Source { get; init; }
    public required string Title { get; init; }
    public required string Info { get; init; }
    public required string Tip { get; init; }
}

public sealed class EntryRow
{
    public required XrEntry Entry { get; init; }
    public required string Name { get; init; }
    public required string Kind { get; init; }
    public required string Where { get; init; }
    public required string Tip { get; init; }
}

public sealed class DetailRow
{
    public required string Label { get; init; }
    public required string Value { get; init; }
    public string Brush { get; init; } = "B.Text";
    public string Tip { get; init; } = "";
    public bool Header { get; init; }
}
