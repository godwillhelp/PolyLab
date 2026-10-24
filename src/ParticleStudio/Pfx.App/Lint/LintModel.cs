using Pfx.App.Model;
using Pfx.Core.Model;

namespace Pfx.App.Lint;

// Block = export refuses to write (the game shows garbage or nothing); Warn = the game shows it differently from what
// the author expects; Info = a fact worth knowing (an unmeasured law, a Workbench quirk).
public enum LintLevel { Block, Warn, Info }

// One rule: its level, where its knowledge comes from (spec section or phase that measured it) and which keys of the
// language files hold its message ("Lint.<Id>"), its fix ("Lint.<Id>.Fix") and its plain name ("Lint.<Id>.Name").
public sealed record LintRule(string Id, LintLevel Level, string Source);

// What a rule found: the part (index into LintInput.Parts, -1 = the project), the emitter (-1 = the part itself), the
// key the click jumps to (an emitter or material key, null = none) and the message arguments.
public sealed record LintIssue(LintRule Rule, int Part, int Emitter, string? Key, object[] Args)
{
    public LintLevel Level => Rule.Level;
}

public sealed record LintPart(string Path, string Title, byte[] Bytes, PartEntry? Entry);

// What a lint run reads, taken on the UI thread so the worker never touches live documents: the open parts' bytes,
// unsaved materials (through the export snapshot), the data roots, the project as it would be saved, the viewport sky
// and the export profile.
public sealed class LintInput
{
    public LintInput(IReadOnlyList<LintPart> parts, ExportSnapshot files, ProjectFile? project, string? projectFolder, string sky)
    {
        Parts = parts;
        Files = files;
        Project = project;
        ProjectFolder = projectFolder;
        Sky = sky;
    }

    public IReadOnlyList<LintPart> Parts { get; }
    public ExportSnapshot Files { get; }
    public ProjectFile? Project { get; }
    public string? ProjectFolder { get; }
    public string Sky { get; }
}

// Parts and emitter names as the run saw them (the rows name them; the documents may have changed since).
public sealed record LintReport(IReadOnlyList<LintIssue> Issues, IReadOnlyList<string> Parts, IReadOnlyList<IReadOnlyList<string>> EmitterNames)
{
    public static readonly LintReport Empty = new(Array.Empty<LintIssue>(), Array.Empty<string>(), Array.Empty<IReadOnlyList<string>>());

    public int Count(LintLevel level) => Issues.Count(i => i.Level == level);
}
