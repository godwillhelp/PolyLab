using System.Diagnostics;
using Pfx.App.Core;
using Pfx.App.Lint;
using Pfx.App.Model;
using Pfx.App.ViewModels;
using Pfx.Core.Model;
using Pfx.Core.Text;
using Pfx.Render;

namespace Pfx.App.Views;

// Lint and budget (spec 3.11). Lint runs on a worker after every change of a part, the project, the roots or the sky:
// one run at a time, and changes made meanwhile start one more run when it ends, so the panel always ends on the newest
// state without piling up runs. The budget simulates the whole cycle, so it runs only while the check panel is on
// screen; a newer request cancels an older one.
public partial class MainWindow : ICheckHost
{
    private LintReport _lint = LintReport.Empty;
    private IReadOnlyList<EffectDoc> _lintDocs = Array.Empty<EffectDoc>();
    private bool _linting;
    private bool _lintAgain;
    private BudgetReport? _budget;
    private CancellationTokenSource? _budgetCancel;
    private bool _budgetQueued = true;

    public LintReport Lint => _lint;
    public BudgetReport? Budget => _budget;
    public bool BudgetBusy => _budgetCancel != null;
    public bool LintBusy => _linting;
    public CheckView CheckTab => CheckPanel;
    public double LastLintMs { get; private set; }
    public double LastBudgetMs { get; private set; }
    // Raised on the UI thread when the newest lint (or budget) run has been shown.
    public event Action? Linted;
    public event Action? Budgeted;

    private void InitChecks() => CheckPanel.Init(this);

    private LintInput LintInputNow(out IReadOnlyList<EffectDoc> docs)
    {
        var project = _project?.Snapshot();
        docs = _docs.ToList();
        var parts = docs.Select(d => new LintPart(d.Path, d.Title, d.Ptc.ToBytes(),
            PartOf(d) is { } id ? project?.Parts.FirstOrDefault(p => p.Id == id) : null)).ToList();
        return new LintInput(parts, TakeSnapshot(), project, _project?.Folder, _viewport.View.Sky.Name);
    }

    public void RequestLint()
    {
        if (_linting)
        {
            _lintAgain = true;
            return;
        }
        _linting = true;
        _lintAgain = false;
        var input = LintInputNow(out var docs);
        var ui = TaskScheduler.FromCurrentSynchronizationContext();
        var clock = Stopwatch.StartNew();
        Task.Run(() => Linter.Run(input)).ContinueWith(t =>
        {
            _linting = false;
            if (t.Exception is { } ex)
            {
                Toast(Loc.F("Toast.LintFailed", (ex.InnerException ?? ex).Message), "err");
            }
            else
            {
                LastLintMs = clock.Elapsed.TotalMilliseconds;
                _lint = t.Result;
                _lintDocs = docs;
                CheckPanel.ShowLint();
                ExportPanel.ShowLint();
            }
            if (_lintAgain)
                RequestLint();
            else
                Linted?.Invoke();
        }, ui);
        _budgetQueued = true;
        if (CheckPanel.IsVisible)
            BudgetWanted();
    }

    public void BudgetWanted()
    {
        if (!_budgetQueued)
            return;
        _budgetQueued = false;
        _budgetCancel?.Cancel();
        var cancel = new CancellationTokenSource();
        _budgetCancel = cancel;
        var input = LintInputNow(out _);
        float step = _pipeline.Settings.SimStep;
        var ui = TaskScheduler.FromCurrentSynchronizationContext();
        var clock = Stopwatch.StartNew();
        CheckPanel.ShowBudget();
        Task.Run(() => BudgetReport.Run(input, step, cancel.Token), cancel.Token).ContinueWith(t =>
        {
            if (_budgetCancel != cancel)
            {
                cancel.Dispose();
                return;
            }
            _budgetCancel = null;
            cancel.Dispose();
            if (t.IsCanceled)
                return;
            if (t.Exception is { } ex)
                Toast(Loc.F("Toast.LintFailed", (ex.InnerException ?? ex).Message), "err");
            else
            {
                LastBudgetMs = clock.Elapsed.TotalMilliseconds;
                _budget = t.Result;
            }
            CheckPanel.ShowBudget();
            Budgeted?.Invoke();
        }, ui);
    }

    public int LintBlocking => _lint.Count(LintLevel.Block);
    public string SearchText { get => SearchBox.Text; set => SearchBox.Text = value; }

    public DrawView DrawMode
    {
        get => _viewport.View.Order;
        set => _viewport.View = _viewport.View with { Order = value };
    }

    public IReadOnlyList<DrawRow> DrawOrder(out double fill)
    {
        var previews = _viewport.Previews;
        var fills = ScreenFill.Estimate(_viewport.Camera, Math.Max(1, _viewport.PixelWidth), Math.Max(1, _viewport.PixelHeight), previews);
        fill = fills.Total;
        var rows = new List<DrawRow>();
        foreach (var d in _viewport.Draws.Where(d => d.Copy == 0))
        {
            if (d.Effect >= previews.Count || d.Emitter >= previews[d.Effect].Params.Count)
                continue;
            var p = previews[d.Effect];
            rows.Add(new DrawRow(rows.Count, Path.GetFileName(p.FilePath), p.Params[d.Emitter].Name, d.Key, d.MaySwap, p.Materials[d.Emitter].Additive,
                p.SortParticles[d.Emitter], d.Effect, d.Emitter, fills.PerEmitter.GetValueOrDefault((d.Effect, d.Emitter))));
        }
        return rows;
    }

    public void SelectDraw(DrawRow row)
    {
        var previews = _viewport.Previews;
        if (row.Effect >= previews.Count)
            return;
        string path = Path.GetFullPath(previews[row.Effect].FilePath);
        if (_docs.FirstOrDefault(d => string.Equals(Path.GetFullPath(d.Path), path, StringComparison.OrdinalIgnoreCase)) is { } doc
            && row.Index < doc.Ptc.Emitters.Count)
            Select(doc, new[] { doc.Ptc.Emitters[row.Index].Block }, false);
    }

    // A lint row's click: the part and emitter selected, then the field found like the search finds it (graphs open
    // in the curve editor), or the anomaly panel for a project row.
    // The recipe a lint row names, applied to the emitters (or parts) of its rows.
    public void ApplyLintRecipe(IReadOnlyList<LintIssue> issues)
    {
        var rule = issues[0].Rule;
        if (Pfx.App.Recipes.RecipeCatalog.Find(Pfx.App.Recipes.RecipeCatalog.ForLint[rule.Id]) is not { } recipe)
            return;
        if (_project == null)
            NewProject();
        if (_project == null)
            return;
        var targets = new List<RecipeTarget>();
        string? PartId(EffectDoc doc)
        {
            if (PartOf(doc) == null)
                AddOpenPart(doc);
            return PartOf(doc);
        }
        foreach (var issue in issues)
        {
            if (issue.Part < 0)
            {
                // TurnRates names a state: the parts of it that turn become one body.
                var state = _project.File.States.FirstOrDefault(s => s.Name == issue.Args[0] as string);
                foreach (var part in _project.Snapshot().Parts.Where(p => p.Spin != 0 && state?.Items.Any(i => i.Ref == p.Id) == true))
                    targets.Add(new RecipeTarget { Part = part.Id });
                continue;
            }
            if (issue.Part >= _lintDocs.Count || !_docs.Contains(_lintDocs[issue.Part]) || PartId(_lintDocs[issue.Part]) is not { } id)
                continue;
            var doc = _lintDocs[issue.Part];
            if (rule.Id == "StackedAxis" && issue.Args.Length > 1 && issue.Args[1] is string names)
                targets.AddRange(names.Split(", ").Select(n => new RecipeTarget { Part = id, Emitter = n }));
            else if (issue.Emitter >= 0 && issue.Emitter < doc.Ptc.Emitters.Count && recipe.Scope == Pfx.App.Recipes.RecipeScope.Emitters)
                targets.Add(new RecipeTarget { Part = id, Emitter = doc.Ptc.Emitters[issue.Emitter].Name });
            else
                targets.Add(new RecipeTarget { Part = id });
        }
        AddRecipe(recipe, null, targets.DistinctBy(t => (t.Part, t.Emitter)).ToList());
    }

    public void Jump(LintIssue issue)
    {
        if (issue.Part < 0 || issue.Part >= _lintDocs.Count)
        {
            ShowPane("compose");
            return;
        }
        var doc = _lintDocs[issue.Part];
        if (!_docs.Contains(doc))
            return;
        var emitters = doc.Ptc.Emitters;
        if (issue.Emitter < 0 || issue.Emitter >= emitters.Count)
        {
            Select(doc, Array.Empty<EnfBlock>(), true);
            return;
        }
        Select(doc, new[] { emitters[issue.Emitter].Block }, false);
        if (issue.Key == null)
        {
            ShowPane("layers");
            return;
        }
        if (Linter.Graphs.Any(g => g.Key == issue.Key))
        {
            CurvesPanel.Editor.Focus(issue.Key);
            ShowPane("curves");
            return;
        }
        SearchBox.Text = issue.Key;
    }

    private StatusItem LintStatus()
    {
        int e = _lint.Count(LintLevel.Block), w = _lint.Count(LintLevel.Warn), i = _lint.Count(LintLevel.Info);
        return new StatusItem(e + w + i == 0 ? Loc.T("Status.LintClean") : Loc.F("Status.Lint", e, w, i), Loc.T("Tip.Status.Lint"),
            e > 0 ? "B.Err" : w > 0 ? "B.Warn" : "B.Ok");
    }
}
