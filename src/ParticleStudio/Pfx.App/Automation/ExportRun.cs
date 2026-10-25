using System.Text;
using Pfx.App.Views;
using Pfx.Core.Export;

namespace Pfx.App.Automation;

// --export <project.plpfx> --export-log <file> --profile dayz|cg --to <folder> [--prefix P] [--data-prefix D]
// [--slot <file>] [--type <word>]: opens the project, sets the export profile through the export panel's own project
// edit, plans on the panel's worker, writes the plan with the panel's Write and logs every file with its state; then
// exits. The same steps a user takes on the export panel.
public sealed class ExportRun
{
    private readonly MainWindow _w;
    private readonly Pfx.App.Core.Launch _l;
    private readonly StringBuilder _log = new();

    public ExportRun(MainWindow window, Pfx.App.Core.Launch launch)
    {
        _w = window;
        _l = launch;
    }

    public void Start() => _ = Run();

    private void Log(string line)
    {
        _log.AppendLine(line);
        File.WriteAllText(_l.ExportLog!, _log.ToString());
    }

    private Task<ExportPlan?> Plan()
    {
        var done = new TaskCompletionSource<ExportPlan?>();
        void Handler()
        {
            _w.ExportTab.Planned -= Handler;
            done.TrySetResult(_w.ExportTab.Plan);
        }
        _w.ExportTab.Planned += Handler;
        _w.ExportTab.Replan();
        return done.Task;
    }

    private async Task Run()
    {
        try
        {
            _w.OpenProject(_l.Export!);
            var p = _w.Project ?? throw new InvalidOperationException("project not opened");
            string to = Path.GetFullPath(_l.ExportTo ?? throw new InvalidOperationException("--to missing"));
            p.Do(f =>
            {
                f.Export.Profile = _l.ExportProfile ?? "dayz";
                if (f.Export.Profile == "cg")
                {
                    f.Export.Cg.DonorFolder = to;
                    f.Export.Cg.SlotFile = Path.GetFullPath(_l.ExportSlot ?? throw new InvalidOperationException("--slot missing"));
                    f.Export.Cg.Type = _l.ExportType ?? f.Export.Cg.Type;
                }
                else
                {
                    f.Export.Dayz.ModFolder = to;
                    f.Export.Dayz.Prefix = _l.ExportPrefix ?? f.Export.Dayz.Prefix;
                    f.Export.Dayz.DataPrefix = _l.ExportDataPrefix ?? "";
                }
            }, () => "export settings");
            var plan = await Plan() ?? throw new InvalidOperationException("no plan");
            Log($"export {p.Path} profile {p.File.Export.Profile} -> {to}");
            foreach (var e in plan.Errors)
                Log("ERROR " + ExportView.Message(e));
            foreach (var n in plan.Notes.DistinctBy(m => m.Key + string.Join("|", m.Args)))
                Log("NOTE " + ExportView.Message(n));
            foreach (var f in plan.Files)
                Log($"{f.State,-8} {f.What,-9} {f.Bytes.Length,9} {f.Path}");
            foreach (var r in plan.Remove)
                Log($"REMOVE {r}");
            if (!plan.CanWrite)
                throw new InvalidOperationException("the plan has errors");
            _w.ExportTab.Write();
            var again = await Plan() ?? throw new InvalidOperationException("no second plan");
            Log($"after writing: {again.Files.Count(f => f.State == FileState.Same)} of {again.Files.Count} unchanged, {again.Remove.Count} to remove");
            if (plan.Example != null)
                Log("example:\n" + plan.Example);
            Log("RESULT: done");
        }
        catch (Exception ex)
        {
            Log("FAILED: " + ex);
        }
        _w.Close();
    }
}
