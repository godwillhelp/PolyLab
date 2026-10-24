using Pfx.App.XRay;
using Pfx.XRay;

namespace Pfx.App.Automation;

// P9 checks, through the methods the controls call: a folder without X-Ray content says "nothing usable" and lists what
// it skipped; with --xray-inputs, the first folder is scanned in the import tab, its known mods become sources, the
// index is searched, a zone's plan ("everything for this anomaly") is shown, the substitute editor takes a texture and
// gives it back; an opened part that came from an import plays its X-Ray original in the A/B view.
public sealed partial class ShellChecks
{
    private async Task XrayWait(Func<bool> done)
    {
        var x = _w.Xray;
        while (!done())
        {
            var hit = new TaskCompletionSource();
            void Hit() => hit.TrySetResult();
            x.Changed += Hit;
            _w.Viewport.Rendered += Hit;
            try
            {
                await hit.Task;
            }
            finally
            {
                x.Changed -= Hit;
                _w.Viewport.Rendered -= Hit;
            }
            await Settled();
        }
    }

    private async Task XrayChecks()
    {
        Log("X-Ray import:");
        var x = _w.Xray;
        string empty = Path.Combine(_dir, "no_xray");
        Directory.CreateDirectory(Path.Combine(empty, "sub"));
        File.WriteAllText(Path.Combine(empty, "notes.txt"), "hello\nworld\n");
        File.WriteAllBytes(Path.Combine(empty, "sub", "data.db"), "SQLite format 3\0"u8.ToArray().Concat(new byte[64]).ToArray());
        XrScanResult? none = null;
        _w.ShowPane("import");
        _w.ImportPanel.FolderText = empty;
        _w.ImportPanel.ScanNow(r => none = r);
        await XrayWait(() => none != null || !x.Busy);
        await Settled();
        Expect(none is { Nothing: true } && none.Skipped.Values.Sum(v => v.Count) == 2,
            $"a folder without X-Ray content: nothing usable, skipped {string.Join(", ", none?.Skipped.Select(s => $"{s.Key} {s.Value.Count}") ?? Array.Empty<string>())}");
        Expect(_w.ImportPanel.FindName("NothingText") is System.Windows.UIElement { IsVisible: true }, "the import tab says so");

        var inputs = _launch.XrayInputs?.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (inputs is not { Length: > 0 })
        {
            Log("  (no --xray-inputs: scan, index, plan, substitute and A/B checks skipped)");
            _w.ShowPane("viewport");
            return;
        }
        var scans = new List<XrScanResult>();
        foreach (var folder in inputs)
        {
            XrScanResult? got = null;
            _w.ImportPanel.FolderText = folder;
            _w.ImportPanel.ScanNow(r => got = r);
            await XrayWait(() => got != null || !x.Busy);
            if (got == null)
            {
                Problem("scan failed: " + folder);
                return;
            }
            scans.Add(got);
            Log($"  scan {folder}: {got.Files} files, read {got.Read}, from the cache {got.Reused}, {got.Took.TotalSeconds:0.0} s, {got.Mods.Count} mods");
        }
        foreach (var scan in scans)
            foreach (var mod in scan.Mods.Where(m => m.Guess.Known != null))
            {
                _w.ImportPanel.ShowScan(scan);
                _w.ImportPanel.SelectMod(mod);
                _w.ImportPanel.TagText = mod.Guess.Tag;
                _w.ImportPanel.AddSource();
            }
        await XrayWait(() => x.Index != null && !x.IndexBuilding);
        Expect(x.Sources.Sources.Count > 0 && x.Index!.Entries.Any(), $"sources {string.Join(" ", x.Sources.Sources.Select(s => s.Tag))}, index {x.Index?.Entries.Count()} entries");
        await XrayWait(() => _w.ImportPanel.Anomalies.Count > 0 || x.Sources.Sources.All(s => s.Known is null));
        Expect(_w.ImportPanel.Anomalies.Count > 0, $"anomalies of the selected mod listed: {_w.ImportPanel.Anomalies.Count}");

        _w.ShowPane("xray");
        _w.XrayPanel.SetKinds(XrEntryKind.Zone, XrEntryKind.Group);
        _w.XrayPanel.Query = "electra";
        await Settled();
        var results = _w.XrayPanel.Results;
        Expect(results.Count > 0 && results.All(r => r.Entry.Key.Contains("electra") || (r.Entry.Texture?.Contains("electra") ?? false)), $"search 'electra' in zones and groups: {results.Count}");
        var group = results.FirstOrDefault(r => r.Entry.Kind == XrEntryKind.Group);
        if (group != null)
        {
            _w.XrayPanel.Pick(group.Entry);
            Expect(_w.XrayPanel.Details.Any(d => d.Brush == "B.Ok"), $"group {group.Entry.Name}: details name the winning source ({group.Entry.Winner.Source.Tag})");
        }
        var zone = results.FirstOrDefault(r => r.Entry.Kind == XrEntryKind.Zone);
        if (zone != null)
        {
            _w.XrayPanel.Pick(zone.Entry);
            var plan = _w.XrayPanel.Plan;
            Expect(plan != null && plan.Particles.Count > 0 && _w.XrayPanel.Details.Count(d => d.Header) >= 2,
                $"everything for {zone.Entry.Key}: {plan?.Particles.Count} particles, {plan?.Sounds.Count} sounds, {plan?.Lights.Count} lights, {plan?.Timings.Count} timings; parts {_w.XrayPanel.Parts().Count}");
        }
        else
        {
            Problem("no zone matches 'electra'");
        }

        var sub = _w.SubstPanel;
        string fake = "pfx_checks/missing_texture";
        sub.AddMissing(new[] { fake });
        Expect(sub.Rows.Any(r => r.Name == fake && !r.Saved), "a missing texture is listed in the substitute editor");
        string? any = x.Index!.Entries.FirstOrDefault(e => e.Kind == XrEntryKind.Texture)?.Winner.File;
        if (any != null)
        {
            sub.Accept(fake, any);
            Expect(x.Substitutes.TryGetValue(fake, out var v) && v == any && sub.Rows.Any(r => r.Name == fake && r.Saved), "accepting writes it into the table (sent with every import)");
            x.Substitutes.Remove(fake);
            sub.Show();
        }
        var tick = System.Diagnostics.Stopwatch.StartNew();
        await sub.CheckAll();
        Log($"  substitute check over every effect: {sub.Rows.Count(r => !r.Saved)} missing textures listed in {tick.Elapsed.TotalSeconds:0.0} s");

        var imported = _w.Docs.FirstOrDefault(d => ImportRecord.ForPart(d.Path) != null);
        if (imported != null)
        {
            _w.ShowPane("viewport");
            _w.Select(imported, Array.Empty<Pfx.Core.Text.EnfBlock>(), true);
            await XrayWait(() => _w.Ab.Record != null && _w.Ab.Ready);
            Expect(_w.AbPanel.Rows.Count > 0, $"A/B of {imported.Title}: {_w.Ab.Record!.Ref}, {_w.AbPanel.Rows.Count} X-Ray emitters listed, {_w.Ab.Live} X-Ray particles drawn");
        }
        else
        {
            Log("  (no imported part among the opened files: A/B check skipped)");
        }
        _w.ShowPane("viewport");
    }
}
