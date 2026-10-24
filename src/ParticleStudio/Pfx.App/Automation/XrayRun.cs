using System.Numerics;
using System.Text;
using System.Windows.Threading;
using Pfx.App.Core;
using Pfx.App.Views;
using Pfx.App.XRay;
using Pfx.XRay;

namespace Pfx.App.Automation;

// --xray <dir>: the P9 acceptance flow inside the real window, through the methods the controls call (no input reaches
// the desktop). Test inputs come from the command line only: --xray-inputs (folders, ';'), --xray-script (xray.py),
// --xray-expected (tools/xray/expected.py output). Steps: scan each folder in the import tab; every root xray.py lists
// must be found with its tag; the mods become sources in xray.py's order with exactly its options on; the index counts
// per tag must equal xray.py's; three anomalies (electra, gravitational, fire) are imported from their zone sections
// through the X-Ray panel into <dir>\import, and every file the import wrote that xray.py's own stage also holds must be
// byte-identical; the first part then plays with its X-Ray original side by side and over it (viewport frames).
public sealed class XrayRun
{
    // (zone section, option the zone comes from or null for the core, type word as CG's manifest names it).
    public static readonly (string Section, string? Option, string PType)[] Anomalies =
    {
        ("zone_mine_electric", null, "electra"),
        ("zone_mine_gravitational_strong", null, "myasorubka"),
        ("zone_field_thermal", @"Anomaly Zone Effects\Thermal", "fieldthermal"),
    };

    private readonly MainWindow _w;
    private readonly Launch _launch;
    private readonly string _dir;
    private readonly StringBuilder _log = new();
    private TaskCompletionSource? _frame;
    private int _bad;

    public XrayRun(MainWindow window, Launch launch)
    {
        _w = window;
        _launch = launch;
        _dir = Path.GetFullPath(launch.XrayRun!);
    }

    public void Start()
    {
        Directory.CreateDirectory(_dir);
        _w.Viewport.Rendered += () => _frame?.TrySetResult();
        _ = Run();
    }

    private void Log(string line)
    {
        _log.AppendLine(line);
        File.WriteAllText(Path.Combine(_dir, "xray.txt"), _log.ToString());
    }

    private void Expect(bool ok, string what)
    {
        Log((ok ? "  ok   " : "  FAIL ") + what);
        if (!ok)
            _bad++;
    }

    // The UI thread's queue after everything already posted (job results arrive as posted continuations).
    private async Task Idle() => await _w.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);

    // Waits for the condition, woken by what changes it: a workspace event (job progress or end, index ready) or a
    // shown viewport frame (previews built, seeks landed).
    private async Task Until(Func<bool> done)
    {
        while (!done())
        {
            var hit = new TaskCompletionSource();
            void Hit() => hit.TrySetResult();
            _w.Xray.Changed += Hit;
            _w.Viewport.Rendered += Hit;
            try
            {
                await hit.Task;
            }
            finally
            {
                _w.Xray.Changed -= Hit;
                _w.Viewport.Rendered -= Hit;
            }
            await Idle();
        }
    }

    private async Task Run()
    {
        try
        {
            var x = _w.Xray;
            var expected = XrExpected.Load(_launch.XrayExpected ?? throw new ArgumentException("--xray-expected is needed"));
            _w.Settings.ImportFolder = Path.Combine(_dir, "import");
            Directory.CreateDirectory(_w.Settings.ImportFolder);
            _w.SwitchWorkspace("import");
            var scans = new List<XrScanResult>();
            foreach (var folder in (_launch.XrayInputs ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                XrScanResult? got = null;
                _w.ImportPanel.FolderText = folder;
                if (!_w.ImportPanel.ScanNow(r => got = r))
                    throw new InvalidOperationException("scan did not start: " + folder);
                await Until(() => got != null || !x.Busy);
                if (got == null)
                    throw new InvalidOperationException("scan failed: " + folder);
                scans.Add(got);
                Log($"scan {got.Folder}: {got.Files} files, read {got.Read}, from the cache {got.Reused}, {got.Took.TotalSeconds:0.0} s; {got.Mods.Count} mods, {got.Roots.Count()} roots" +
                    (got.Nothing ? "; nothing usable" : ""));
                foreach (var m in got.Mods)
                    Log($"  mod {m.Name}: tag '{m.Guess.Tag}' ({m.Guess.Known ?? "unknown"} {m.Guess.Marker}), {m.Roots.Count} roots, core {m.Core.Name}");
                foreach (var (why, (n, ex)) in got.Skipped.OrderByDescending(s => s.Value.Count))
                    Log($"  skipped {why}: {n}");
                await Shot($"scan_{scans.Count}");
            }
            Log("roots of xray.py:");
            foreach (var c in expected.CheckRoots(scans))
                Expect(c.Ok, $"{c.Expected.Tag}:{c.Expected.Variant} {c.Expected.Path} -> {(c.Found == null ? "not found" : $"{(c.Found.Core ? "core" : "option")} '{c.Found.Name}' of {c.Mod!.Name}, tag '{c.Tag}'")}");

            Log("sources, added in the import tab:");
            var wanted = expected.Roots.Select(r => r.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var mod in scans.SelectMany(s => s.Mods).Where(m => m.Roots.Any(r => wanted.Contains(r.Path))).OrderBy(m => expected.Order.ToList().IndexOf(m.Guess.Tag)))
            {
                if (_w.ImportPanel.Mod != mod)
                {
                    _w.ImportPanel.ShowScan(scans.First(s => s.Mods.Contains(mod)));
                    _w.ImportPanel.SelectMod(mod);
                }
                foreach (var row in _w.ImportPanel.Roots)
                    row.Selected = row.Core || wanted.Contains(row.Root.Path);
                _w.ImportPanel.TagText = mod.Guess.Tag;
                var s = _w.ImportPanel.AddSource();
                Log($"  {s?.Tag}: {s?.Name}, {s?.Roots.Count(r => r.Selected)} of {s?.Roots.Count} roots on");
            }
            await Until(() => !x.IndexBuilding && x.Index != null);
            Log("index per tag, as xray.py counts:");
            var counts = x.Index!.CountsPerTag();
            foreach (var (tag, want) in expected.Counts)
                Expect(counts.GetValueOrDefault(tag) == want, $"{tag}: xray.py {want.Effects} effects / {want.Groups} groups, index {counts.GetValueOrDefault(tag).Effects} / {counts.GetValueOrDefault(tag).Groups}");
            Log($"index entries: {string.Join(", ", x.Index.Entries.GroupBy(e => e.Kind).OrderBy(g => g.Key).Select(g => $"{g.Key} {g.Count()}"))}");

            _w.ShowPane("xray");
            _w.XrayPanel.Query = "electra";
            _w.XrayPanel.SetKinds(XrEntryKind.Zone, XrEntryKind.Group);
            await Idle();
            Log($"search 'electra' in zones and groups: {_w.XrayPanel.Results.Count} found");
            await Shot("search");

            var opened = new List<string>();
            foreach (var (section, option, ptype) in Anomalies)
            {
                var entry = x.Index.Entry(XrEntryKind.Zone, section) ?? throw new InvalidOperationException("no zone " + section);
                _w.XrayPanel.Pick(entry);
                var plan = _w.XrayPanel.Plan!;
                if (option != null)
                {
                    var root = plan.Source.Roots.First(r => r.Name.EndsWith(option, StringComparison.OrdinalIgnoreCase));
                    _w.XrayPanel.SetVariant(root.Id);
                    plan = _w.XrayPanel.Plan!;
                }
                _w.XrayPanel.PType = ptype;
                Log($"{section} ({plan.Source.Tag}:{_w.XrayPanel.Variant}): {plan.Particles.Count} particles, textures {plan.TexturesFound} of {plan.TexturesTotal}, " +
                    $"{plan.Sounds.Count} sounds, {plan.Lights.Count} lights, {plan.Timings.Count} timings");
                await Shot("plan_" + ptype);
                IReadOnlyList<string>? files = null;
                if (!_w.ImportParts(_w.XrayPanel.Parts(), plan, f => files = f))
                    throw new InvalidOperationException("import did not start");
                await Until(() => files != null || !x.Busy);
                if (files == null)
                    throw new InvalidOperationException("import failed: " + section);
                opened.AddRange(files);
                Compare(expected.Stage);
            }

            await AbShots(opened);
        }
        catch (Exception ex)
        {
            _bad++;
            Log("FAILED: " + ex);
        }
        Log(_bad == 0 ? "RESULT: accepted" : $"RESULT: {_bad} failure(s)");
        _w.Close();
    }

    // Every .ptc / .emat / .edds of the last import against xray.py's own stage: byte for byte where the stage has it.
    private void Compare(string? stage)
    {
        var result = _w.Xray.LastImport!;
        string mine = Path.Combine(_w.Settings.ImportFolder, ImportRecord.Prefix);
        int same = 0, differ = 0, only = 0;
        foreach (var p in result.Particles)
            foreach (var rel in p.Files)
            {
                string a = Path.Combine(mine, rel.Replace('/', '\\'));
                string? b = stage != null ? Path.Combine(stage, rel.Replace('/', '\\')) : null;
                if (b == null || !File.Exists(b))
                {
                    only++;
                    continue;
                }
                bool eq = File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b));
                if (eq)
                    same++;
                else
                {
                    differ++;
                    Log($"    differs from xray.py's stage: {rel}");
                }
            }
        foreach (var p in result.Particles)
            Log($"  {p.PType}_{p.State} <- {p.Tag}:{p.Variant} {p.Ref}: {(p.Ok ? string.Join(", ", p.Ptc) : "no emitters")}");
        Expect(differ == 0 && same > 0, $"import = xray.py's stage byte for byte: {same} files equal, {differ} differ, {only} not in the stage (parts the manifest never converted)");
    }

    private async Task Shot(string name)
    {
        await Idle();
        var root = (System.Windows.FrameworkElement)_w.Content;
        double dpi = System.Windows.Media.VisualTreeHelper.GetDpi(_w).DpiScaleX;
        ShellChecks.SaveShot(_w, root, _w.ViewportArea, dpi, Path.Combine(_dir, name + ".png"));
    }

    // The swap chain follows a resize on a later frame: wait until it has the viewport area's pixel size.
    private async Task ViewportSized()
    {
        double dpi = System.Windows.Media.VisualTreeHelper.GetDpi(_w).DpiScaleX;
        await Until(() => _w.ViewportArea.IsVisible && _w.Viewport.PixelWidth == (int)Math.Round(_w.ViewportArea.ActualWidth * dpi)
                          && _w.Viewport.PixelHeight == (int)Math.Round(_w.ViewportArea.ActualHeight * dpi));
    }

    private Task Frame()
    {
        _frame = new TaskCompletionSource();
        return _frame.Task;
    }

    // A/B frames of two parts with many particles: the thermal field (smoke, sparks, haze) and the strong gravitational
    // idle (turning debris), side by side and overlaid, after 6 s of build-up.
    private static readonly string[] AbParts = { "fieldthermal_ar_idle.ptc", "myasorubka_ar_idle.ptc" };

    private async Task AbShots(List<string> opened)
    {
        _w.SwitchWorkspace("emitters");
        _w.WindowState = System.Windows.WindowState.Maximized;
        foreach (var name in AbParts)
        {
            string? file = opened.FirstOrDefault(f => f.EndsWith(name, StringComparison.OrdinalIgnoreCase));
            if (file == null)
            {
                Expect(false, "imported part " + name);
                continue;
            }
            foreach (var d in _w.Docs.ToList())
                _w.CloseDocument(d);
            _w.Open(new[] { file });
            var doc = _w.Docs.Single();
            _w.Select(doc, Array.Empty<Pfx.Core.Text.EnfBlock>(), true);
            _w.ViewportDocument.IsSelected = true;
            await Until(() => _w.Viewport.PixelWidth > 0 && !_w.Pipeline.AnyBusy && _w.Viewport.Previews.Count == 1 && _w.Ab.Record != null);
            await ViewportSized();
            var ab = _w.Ab;
            Expect(ab.Ready, $"A/B plays {ab.Record?.Ref} ({ab.Record?.Emitters.Count} X-Ray emitters listed in the A/B panel{(ab.Missing != null ? ", missing " + ab.Missing : "")}{(ab.Problems.Count > 0 ? ", not started " + string.Join(" ", ab.Problems) : "")})");
            var cam = _w.Viewport.Camera;
            foreach (var (mode, eye, target) in new[] { (AbMode.Side, new Vector3(-3.5f, 3, -10), new Vector3(-3.5f, 1.2f, 0)), (AbMode.Over, new Vector3(0, 2.5f, -6.5f), new Vector3(0, 1.2f, 0)) })
            {
                ab.Mode = mode;
                ab.Distance = 7;
                _w.AbPanel.Show();
                cam.LookAt(eye, target);
                _w.Pipeline.Playing = false;
                _w.Pipeline.Seek(6);
                await Until(() => !_w.Pipeline.AnySeeking && !_w.Pipeline.AnyBusy);
                await Frame();
                await Frame();
                string stem = Path.GetFileNameWithoutExtension(name);
                if (_w.Viewport.CaptureFrame(true) is { } image)
                    Pfx.Textures.ImageFiles.Save(Path.Combine(_dir, $"ab_{stem}_{mode.ToString().ToLowerInvariant()}.png"), MainWindow.Opaque(image));
                int dayz = _w.Viewport.Previews.Sum(p => p.AliveTotal);
                Log($"A/B {stem} {mode} at 6 s: X-Ray particles {ab.Live}, DayZ particles {dayz}");
                Expect(ab.Live > 0 && dayz > 0, $"both sides draw particles ({stem}, {mode})");
            }
            _w.ShowPane("xab");
            await Shot("ab_panel_" + Path.GetFileNameWithoutExtension(name));
        }
    }
}
