using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Pfx.App.Core;
using Pfx.App.Inspector;
using Pfx.App.Model;
using Pfx.App.Views;
using Pfx.Core.Schema;
using Pfx.Textures;

namespace Pfx.App.Automation;

// --checks <dir>: the P5 acceptance checks run inside the real window (spec 7 P5):
// - screenshots at 1280x720, 1366x768 and 1920x1080 (window outer height minus 40 px of taskbar) in RU, EN and UK,
//   composed from the window bitmap and the viewport's own frame (WPF cannot capture the child window; the desktop is
//   never captured);
// - over the visual tree: every visible control inside its parent (scrollable directions excepted), no text cut
//   without a tooltip that shows it whole, every button, field and menu header with a tooltip that is not a raw key;
// - the inspector rows that exist on screen cover every schema key (effect, emitter, material);
// - open -> save through the document model is byte-identical, and an unchanged file is not rewritten;
// - one inspector edit reaches the viewport (time from the edit to the new preview on screen).
// Every wait is for an event: a presented frame, a finished layout pass, a built preview.
public sealed partial class ShellChecks
{
    private static readonly (int W, int H)[] Sizes = { (1280, 720), (1366, 768), (1920, 1080) };
    private const int TaskbarHeight = 40;
    private static readonly Regex RawKey = new(@"^[A-Z][A-Za-z]*\.[A-Za-z0-9.]+$");

    private readonly MainWindow _w;
    private readonly Launch _launch;
    private readonly string _dir;
    private readonly StringBuilder _report = new();
    private TaskCompletionSource? _frame;
    private TaskCompletionSource<double>? _built;
    private int _problems;

    public ShellChecks(MainWindow window, Launch launch)
    {
        _w = window;
        _launch = launch;
        _dir = Path.GetFullPath(launch.Checks!);
    }

    public void Start()
    {
        Directory.CreateDirectory(_dir);
        _w.Viewport.Rendered += () => _frame?.TrySetResult();
        _w.PreviewBuilt += (_, total, _) => _built?.TrySetResult(total);
        _ = Run();
    }

    private void Log(string line)
    {
        _report.AppendLine(line);
        File.WriteAllText(Path.Combine(_dir, "report.txt"), _report.ToString());
    }

    private void Problem(string line)
    {
        _problems++;
        Log("  PROBLEM " + line);
    }

    private Task Frame()
    {
        _frame = new TaskCompletionSource();
        return _frame.Task;
    }

    // Queued after the layout and render passes of everything already dispatched.
    private async Task Settled()
    {
        await _w.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        await _w.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
    }

    private async Task Run()
    {
        try
        {
            Log($"shell checks {DateTime.Now:yyyy-MM-dd HH:mm}, files: {string.Join(", ", _w.Docs.Select(d => d.Path))}");
            if (_w.Docs.Count == 0)
            {
                Problem("no effect opened (pass .ptc files on the command line)");
                Finish();
                return;
            }
            while (_w.Pipeline.AnyBusy || _w.Viewport.Previews.Count < _w.Docs.Count)
                await Frame();
            await EditToViewport();
            await Behaviour();
            await CurveChecks();
            await ScrubChecks();
            await TextureChecks();
            await ToolChecks();
            await XrayChecks();
            await CompositionChecks();
            await LintChecks();
            await RecipeChecks();
            await Coverage();
            foreach (var lang in Loc.Languages)
                foreach (var size in Sizes)
                {
                    foreach (var pane in new[] { "timeline", "curves", "slicer+texture", "atlas" })
                        await Screen(lang, size.W, size.H, pane);
                    foreach (var tool in new[] { "select", "rotate", "shape", "path" })
                        await Screen(lang, size.W, size.H, "timeline", tool);
                    _w.SetTool("select");
                    foreach (var pane in new[] { "xray", "xab", "xsubst", "import+xray" })
                        await Screen(lang, size.W, size.H, pane);
                    _w.ShowPane("viewport");
                    foreach (var pane in new[] { "compose+export", "compose+timeline", "gameref", "checks", "recipes+checks", "library+timeline" })
                        await Screen(lang, size.W, size.H, pane);
                    _w.ShowPane("viewport");
                }
            RoundTrip();
        }
        catch (Exception ex)
        {
            Problem("check run failed: " + ex);
        }
        Finish();
    }

    private void Finish()
    {
        Log(_problems == 0 ? "RESULT: all checks passed" : $"RESULT: {_problems} problem(s)");
        _w.Close();
    }

    private async Task EditToViewport()
    {
        var doc = _w.Docs[0];
        var first = doc.Ptc.Emitters.FirstOrDefault();
        if (first == null)
            return;
        _w.Select(doc, new[] { first.Block }, false);
        await Settled();
        var field = _w.EmitterInspector.AllFields.OfType<PairField>().First(f => f.Key == "Velocity");
        double before = field.Base.Value;
        var oldPreview = _w.Pipeline.PreviewOf(doc);
        _built = new TaskCompletionSource<double>();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        field.Base.Commit(Loc.Num(before + 1));
        double uiMs = clock.Elapsed.TotalMilliseconds;
        double total = await _built.Task;
        var newPreview = _w.Pipeline.PreviewOf(doc);
        bool swapped = newPreview != oldPreview && _w.Viewport.Previews.Contains(newPreview!);
        double v = newPreview?.Params[0].Velocity ?? double.NaN;
        Log($"edit -> viewport: Velocity {before} -> {before + 1} on {first.Name}: UI thread {uiMs:0.0} ms, edit to new preview on screen {total:0.0} ms, preview swapped {swapped}, preview Velocity {v}");
        if (!swapped || Math.Abs(v - (before + 1)) > 1e-4)
            Problem("the edit did not reach the viewport");
        doc.UndoStep();
        _built = new TaskCompletionSource<double>();
        await _built.Task;
        _built = null;
    }

    private async Task<double> Built()
    {
        _built = new TaskCompletionSource<double>();
        double ms = await _built.Task;
        _built = null;
        return ms;
    }

    private void Expect(bool ok, string what)
    {
        if (ok)
            Log("  ok: " + what);
        else
            Problem(what);
    }

    // What a user does with the layers and docks, run through the same handlers the controls call (the mouse itself is
    // not simulated: no input is sent to the owner's desktop).
    private async Task Behaviour()
    {
        Log("behaviour:");
        var doc = _w.Docs[0];
        int count = doc.Ptc.Emitters.Count;
        var first = doc.Ptc.Emitters[0];
        string name = first.Name;
        var row = _w.LayerRows.OfType<ViewModels.EmitterRow>().First(r => r.Block == first.Block);

        Expect(_w.RenameProblem(row, "ref") != null && _w.RenameProblem(row, "a b") != null && _w.RenameProblem(row, "smoke_main") == null,
            "rename check refuses an Enforce keyword and spaces, accepts smoke_main");
        _w.RenameEmitter(row, "smoke_main");
        await Built();
        Expect(doc.Ptc.Emitters[0].Name == "smoke_main" && doc.UndoLabel != null, "rename writes the file name and is one undo step");
        string lang = Loc.Lang;
        _w.LanguageChanged(Loc.Languages.First(l => l != lang));
        bool follows = _w.UndoText.Text == Loc.F("Status.Undo", Loc.F("Undo.Rename", name, "smoke_main"));
        _w.LanguageChanged(lang);
        Expect(follows, "the undo label in the status bar follows a language switch");
        doc.UndoStep();
        await Built();
        Expect(doc.Ptc.Emitters[0].Name == name, "undo brings the old name back");

        _w.Select(doc, new[] { first.Block }, false);
        _w.CloneEmitter();
        await Built();
        Expect(doc.Ptc.Emitters.Count == count + 1 && doc.Ptc.Emitters[1].Name == name + "_cloned", "clone lands right after its source as " + name + "_cloned");
        _w.DeleteEmitters();
        await Built();
        Expect(doc.Ptc.Emitters.Count == count, "delete removes the selected clone");
        _w.AddEmitter();
        await Built();
        Expect(doc.Ptc.Emitters[^1].Name.StartsWith("emitor", StringComparison.Ordinal), "new emitter at the end named " + doc.Ptc.Emitters[^1].Name);
        doc.UndoStep();
        await Built();
        var rows = _w.LayerRows.OfType<ViewModels.EmitterRow>().Where(r => r.Doc == doc).ToList();
        _w.MoveEmitter(rows[0], rows[^1]);
        await Built();
        Expect(doc.Ptc.Emitters[^1].Block == first.Block, "drag to the last row moves the emitter to the end");
        doc.UndoStep();
        await Built();
        Expect(!doc.Dirty, "after undoing everything the file is unchanged");

        row = _w.LayerRows.OfType<ViewModels.EmitterRow>().First(r => r.Block == first.Block);
        row.Visible = false;
        await Built();
        var preview = _w.Pipeline.PreviewOf(doc)!;
        Expect(preview.Params[0].BirthRate == 0 && !doc.Dirty, "eye off: the preview stops that emitter, the file stays unchanged");
        row.Visible = true;
        await Built();

        _w.Select(doc, new[] { first.Block }, false);
        await Settled();
        var size = _w.EmitterInspector.AllFields.OfType<PairField>().First(f => f.Key == "SizeMultiplier").Base;
        string? labelBefore = doc.UndoLabel;
        double sizeBefore = doc.Ptc.Emitters[0].Number("SizeMultiplier") ?? 1;
        size.SetFromDrag(v => v + 0.1, "scrub:check");
        size.SetFromDrag(v => v + 0.1, "scrub:check");
        doc.Seal();
        await Built();
        bool moved = Math.Abs((doc.Ptc.Emitters[0].Number("SizeMultiplier") ?? 1) - sizeBefore - 0.2) < 1e-6;
        doc.UndoStep();
        await Built();
        Expect(moved && doc.UndoLabel == labelBefore && Math.Abs((doc.Ptc.Emitters[0].Number("SizeMultiplier") ?? 1) - sizeBefore) < 1e-9,
            "two drag steps with one merge key undo as one step");

        _w.SwitchWorkspace("textures");
        await Settled();
        await Frame();
        bool texturesOk = _w.PanesBound;
        _w.SwitchWorkspace("emitters");
        await Settled();
        await Frame();
        Expect(texturesOk && _w.PanesBound && _w.Viewport.PixelWidth > 0, "workspace switch reloads the layout, every panel keeps its content, the viewport keeps rendering");
        _w.ResetLayout();
        await Settled();
        await Frame();
        Expect(_w.PanesBound && _w.ToastShown, "layout reset keeps the panels; the toast popup is open");
        var later = new[] { "timeline", "curves", "slicer", "atlas", "texture" };
        string older = Regex.Replace(_w.DefaultLayout, "<LayoutAnchorable [^>]*ContentId=\"(" + string.Join("|", later) + ")\"[^>]*/>", "");
        bool stripped = later.All(id => !older.Contains("\"" + id + "\""));
        _w.LoadLayout(older);
        await Settled();
        await Frame();
        Expect(stripped && later.All(_w.PaneIds.Contains) && _w.PanesBound,
            "a layout saved before the timeline, curves and texture panels existed (P5) gets all five back");
        _w.ResetLayout();
        await Settled();
    }

    // ---- curve editor (P6): the gestures through the methods the canvas, strip and key table call ----

    private async Task CurveChecks()
    {
        Log("curve editor:");
        var doc = _w.Docs[0];
        var first = doc.Ptc.Emitters[0];
        _w.Select(doc, new[] { first.Block }, false);
        await Settled();
        var size = _w.EmitterInspector.AllFields.OfType<GraphField>().First(f => f.Key == "Size");
        size.Open();
        await Settled();
        var ed = _w.Curves.Editor;
        Expect(ed.Emitter?.Block == first.Block && ed.Active.Key == "Size" && _w.CurvesPaneActive,
            "the inspector's Size thumbnail opens Size of the same emitter in the curve editor");
        ed.Focus("Alpha");
        var before = ed.Keys(ed.Active);
        string? undoBefore = doc.UndoLabel;
        string rawBefore = Raw.Of(first.Block, "Alpha") ?? "";

        ed.AddKeyAt(0.37, 0.5);
        await Built();
        var added = ed.Keys(ed.Active);
        int at = added.FindIndex(k => Math.Abs(k.Time - 0.37) < 1e-9);
        Expect(added.Count == before.Count + 1 && at >= 0 && Math.Abs(added[at].Values[1] - 0.5) < 1e-9 && ed.Selection.Contains(at),
            $"double-click adds a key at x 0.37 y 0.5 ({before.Count} -> {added.Count} keys) and selects it");
        var untouched = added.Where((_, i) => i != at).Select(k => string.Join(" ", k.Texts)).ToList();
        Expect(untouched.SequenceEqual(before.Select(k => string.Join(" ", k.Texts))), "the other keys keep their text as written");

        string? labelAfterAdd = doc.UndoLabel;
        ed.BeginDrag();
        ed.DragTo(0.02, 0.05);
        ed.DragTo(0.04, 0.1);
        ed.EndDrag();
        await Built();
        var dragged = ed.Keys(ed.Active)[at];
        Expect(Math.Abs(dragged.Time - 0.41) < 1e-9 && Math.Abs(dragged.Values[1] - 0.6) < 1e-9, $"drag moves the key by x +0.04 y +0.1 (now {dragged.Time} {dragged.Values[1]})");
        doc.UndoStep();
        await Built();
        Expect(doc.UndoLabel == labelAfterAdd && Math.Abs(ed.Keys(ed.Active)[at].Time - 0.37) < 1e-9, "the two drag steps undo as one step");

        ed.Select(at, false);
        ed.SetValue(at, "0.25");
        await Built();
        ed.SetTime(at, "+=0.01");
        await Built();
        var typed = ed.Keys(ed.Active)[at];
        Expect(Math.Abs(typed.Values[1] - 0.25) < 1e-9 && Math.Abs(typed.Time - 0.38) < 1e-9, "typed value 0.25 and time +=0.01 land on the key");
        ed.SetValue(at, "1.4");
        await Built();
        Expect(ed.Curve(ed.Active).Evaluate(0.38f) == 1f && ed.Curve(ed.Active).Unclamped(0.38f) > 1.39f, "a typed 1.4 is written; the drawn (game) curve is clamped at 1 there");

        ed.Copy();
        ed.SetActive(Array.FindIndex(Curves.CurveEditor.LifeLayers, l => l.Key == "Size"));
        int sizeKeys = ed.Keys(ed.Active).Count;
        ed.Paste();
        await Built();
        Expect(ed.Keys(ed.Active).Count == sizeKeys + 1 && ed.Keys(ed.Active).Any(k => Math.Abs(k.Time - 0.38) < 1e-9 && Math.Abs(k.Values[1] - 1.4) < 1e-9),
            "copy from Alpha, paste into Size: the key lands at its time with its value");
        ed.SelectAll();
        ed.DeleteSelected();
        await Built();
        Expect(ed.Keys(ed.Active).Count == 1, "deleting every key leaves one (a graph always keeps a key)");

        ed.MaxError = 0.02;
        ed.ApplyPreset("pulse");
        await Built();
        var pulse = ed.Keys(ed.Active);
        var g = ed.Curve(ed.Active);
        double worst = 0;
        for (int i = 0; i <= 4096; i++)
        {
            double t = i / 4096.0;
            worst = Math.Max(worst, Math.Abs(g.Evaluate((float)t) - (0.75 - 0.25 * Math.Cos(4 * Math.PI * t))));
        }
        Expect(!ed.BakeMissed && worst <= 0.02, $"preset pulse at accuracy 0.02: {pulse.Count} keys, measured off by {worst:0.0000} ({ed.BakeText})");

        ed.SetActive(0);
        ed.SetStop(false, 0, new[] { 1.2, 0.5, 0.2 }, "check-stop");
        doc.Seal();
        await Built();
        var stop = ed.Keys(ed.ColorLayer)[0];
        Expect(stop.Values[1] == 1.2 && stop.Values[2] == 0.5 && stop.Values[3] == 0.2, "a colour stop takes R 1.2 G 0.5 B 0.2 (R marked as clamped by the game)");

        while (doc.UndoLabel != undoBefore && doc.Undo.CanUndo)
        {
            doc.UndoStep();
            await Built();
        }
        Expect(!doc.Dirty && (Raw.Of(first.Block, "Alpha") ?? "") == rawBefore, "undoing every curve edit gives the file back byte for byte");
    }

    // ---- exact scrubbing (P6): the same time gives the same frame, also after an edit and its undo ----

    private async Task<double> SeekAll(float t)
    {
        double worst = 0;
        void OnSeek(EffectDoc d, double worker, double total) => worst = Math.Max(worst, total);
        _w.Seeked += OnSeek;
        _w.Pipeline.Seek(t);
        while (_w.Pipeline.AnySeeking)
            await Frame();
        _w.Seeked -= OnSeek;
        await Frame();
        return worst;
    }

    private static int Differing(RgbaImage a, RgbaImage b)
    {
        if (a.Width != b.Width || a.Height != b.Height)
            return int.MaxValue;
        int n = 0;
        for (int i = 0; i < a.Pixels.Length; i += 4)
            if (a.Pixels[i] != b.Pixels[i] || a.Pixels[i + 1] != b.Pixels[i + 1] || a.Pixels[i + 2] != b.Pixels[i + 2] || a.Pixels[i + 3] != b.Pixels[i + 3])
                n++;
        return n;
    }

    private async Task ScrubChecks()
    {
        Log("scrubbing:");
        _w.Pipeline.Playing = false;
        double ms1 = await SeekAll(2.5f);
        var a = _w.Viewport.CaptureFrame(false)!;
        double ms2 = await SeekAll(7f);
        double ms3 = await SeekAll(2.5f);
        var b = _w.Viewport.CaptureFrame(false)!;
        int diff = Differing(a, b);
        Expect(diff == 0, $"scrub to 2.5 s, to 7 s, back to 2.5 s: {diff} of {a.Width * a.Height} pixels differ (seeks {ms1:0} / {ms2:0} / {ms3:0} ms to screen)");
        SaveImage(a, "scrub_2.5s.png");

        var doc = _w.Docs[0];
        var first = doc.Ptc.Emitters[0];
        double v = first.Number("Velocity") ?? 2;
        doc.Do(first.Set("Velocity", v + 2), () => "check");
        await Built();
        var edited = _w.Viewport.CaptureFrame(false)!;
        int editDiff = Differing(a, edited);
        var preview = _w.Pipeline.PreviewOf(doc)!;
        Expect(editDiff > 0 && preview.ShownSteps == preview.StepsOf(2.5f), $"an edit while paused rebuilds at 2.5 s itself ({editDiff} pixels changed by Velocity {v} -> {v + 2})");
        doc.UndoStep();
        await Built();
        await Frame();
        var undone = _w.Viewport.CaptureFrame(false)!;
        int undoDiff = Differing(a, undone);
        Expect(undoDiff == 0, $"after the undo the frame at 2.5 s equals the one before the edit ({undoDiff} pixels differ)");
        _w.Pipeline.Playing = true;
    }

    private void SaveImage(RgbaImage image, string file)
    {
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(ToBitmap(MainWindow.Opaque(image))));
        using var f = File.Create(Path.Combine(_dir, file));
        png.Save(f);
    }

    // ---- coverage of the schema by rows that exist on screen ----

    private async Task Coverage()
    {
        var doc = _w.Docs.FirstOrDefault(d => d.Materials.Count > 0) ?? _w.Docs[0];
        var seen = new Dictionary<SchemaScope, HashSet<string>>
        {
            [SchemaScope.Effect] = new(StringComparer.OrdinalIgnoreCase),
            [SchemaScope.Emitor] = new(StringComparer.OrdinalIgnoreCase),
            [SchemaScope.Material] = new(StringComparer.OrdinalIgnoreCase),
        };
        _w.Select(doc, Array.Empty<Pfx.Core.Text.EnfBlock>(), true);
        _w.ShowPane("inspector");
        await Open(_w.EffectVm);
        Collect(_w.InspectorPanel, seen[SchemaScope.Effect]);
        var emitter = doc.Ptc.Emitters.FirstOrDefault(e => e.Text("Material") is { } m && doc.Materials.ContainsKey(m)) ?? doc.Ptc.Emitters.First();
        _w.Select(doc, new[] { emitter.Block }, false);
        await Open(_w.EmitterInspector);
        Collect(_w.InspectorPanel, seen[SchemaScope.Emitor]);
        _w.ShowPane("material");
        await Open(_w.MaterialVm);
        Collect(_w.MaterialPanel, seen[SchemaScope.Material]);
        _w.ShowPane("inspector");
        var schema = PfxSchema.Shared;
        foreach (var (scope, fields) in new[] { (SchemaScope.Effect, schema.Effect), (SchemaScope.Emitor, schema.Emitor), (SchemaScope.Material, schema.Material) })
        {
            var missing = fields.Select(f => f.Key).Where(k => !seen[scope].Contains(k)).ToList();
            Log($"coverage {scope}: {fields.Count - missing.Count} of {fields.Count} schema keys have a row on screen" + (missing.Count > 0 ? ", missing: " + string.Join(", ", missing) : ""));
            if (missing.Count > 0)
                Problem($"inspector rows missing for {scope}: {string.Join(", ", missing)}");
        }
    }

    private async Task Open(InspectorVm vm)
    {
        foreach (var s in vm.Sections)
            s.IsOpen = true;
        await Settled();
    }

    // A row is the Grid with the field's context menu; its DataContext is the field.
    private static void Collect(DependencyObject root, HashSet<string> into)
    {
        foreach (var d in InspectorView.Descendants(root))
            if (d is Grid { ContextMenu: not null, DataContext: FieldVm f } g && g.IsVisible)
                foreach (var k in f.Keys)
                    into.Add(k);
    }

    // ---- screenshots and visual-tree checks ----

    // tool: a viewport tool shown with its options bar (P8), the first emitter selected.
    private async Task Screen(string lang, int width, int height, string pane, string? tool = null)
    {
        _w.LanguageChanged(lang);
        if (tool != null)
        {
            var doc = _w.Docs[0];
            _w.Select(doc, new[] { doc.Ptc.Emitters[0].Block }, false);
            _w.SetTool(tool);
        }
        foreach (var p in pane.Split('+'))
            _w.ShowPane(p);
        _w.WindowState = WindowState.Normal;
        _w.Left = 0;
        _w.Top = 0;
        _w.Width = width;
        _w.Height = height - TaskbarHeight;
        await Settled();
        var root = (FrameworkElement)_w.Content;
        var area = _w.ViewportArea;
        double dpi = VisualTreeHelper.GetDpi(_w).DpiScaleX;
        // A document tab in front of the viewport (the X-Ray import) hides it: no frames come, nothing to wait for.
        if (area.IsVisible)
        {
            int want = (int)Math.Round(area.ActualWidth * dpi), wantH = (int)Math.Round(area.ActualHeight * dpi);
            for (int i = 0; i < 600 && (_w.Viewport.PixelWidth != want || _w.Viewport.PixelHeight != wantH); i++)
                await Frame();
            await Frame();
        }
        await Settled();
        string name = $"shot_{lang}_{width}x{height}_{pane.Replace('+', '_')}" + (tool != null ? "_tool_" + tool : "");
        SaveShot(_w, root, area, dpi, Path.Combine(_dir, name + ".png"));
        Log($"{name}: window {_w.ActualWidth:0}x{_w.ActualHeight:0}, viewport {_w.Viewport.PixelWidth}x{_w.Viewport.PixelHeight}");
        int before = _problems;
        CheckTree(root, name);
        if (_problems == before)
            Log("  no clipped controls, no cut text without a tooltip, every control has a tooltip");
    }

    // The shell drawn by WPF with the viewport's own frame painted into its place (the child window is not in WPF's
    // visual tree, so RenderTargetBitmap leaves it empty).
    internal static void SaveShot(MainWindow w, FrameworkElement root, FrameworkElement area, double dpi, string path)
    {
        int pw = (int)Math.Round(root.ActualWidth * dpi), ph = (int)Math.Round(root.ActualHeight * dpi);
        var shell = new RenderTargetBitmap(pw, ph, 96 * dpi, 96 * dpi, PixelFormats.Pbgra32);
        shell.Render(root);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(shell, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
            if (area.IsVisible && w.Viewport.CaptureFrame(true) is { } frame)
            {
                var pos = area.TransformToAncestor(root).Transform(new Point(0, 0));
                dc.DrawImage(ToBitmap(MainWindow.Opaque(frame)), new Rect(pos, new Size(area.ActualWidth, area.ActualHeight)));
            }
        }
        var final = new RenderTargetBitmap(pw, ph, 96 * dpi, 96 * dpi, PixelFormats.Pbgra32);
        final.Render(visual);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(final));
        using var f = File.Create(path);
        png.Save(f);
    }

    private static BitmapSource ToBitmap(RgbaImage image)
    {
        var bgra = new byte[image.Pixels.Length];
        for (int i = 0; i < bgra.Length; i += 4)
        {
            bgra[i] = image.Pixels[i + 2];
            bgra[i + 1] = image.Pixels[i + 1];
            bgra[i + 2] = image.Pixels[i];
            bgra[i + 3] = 255;
        }
        return BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Bgra32, null, bgra, image.Width * 4);
    }

    private void CheckTree(FrameworkElement root, string where)
    {
        int cutWithTip = 0;
        var seenTips = new HashSet<string>();
        foreach (var d in InspectorView.Descendants(root))
        {
            if (d is not FrameworkElement fe || !fe.IsVisible || fe.ActualWidth <= 0 || fe.ActualHeight <= 0)
                continue;
            bool userControl = fe is Control or TextBlock or Image or System.Windows.Shapes.Shape;
            if (!userControl || fe.TemplatedParent is ScrollBar or Thumb or Track)
                continue;
            if (Clipped(fe, root) is { } clip)
                Problem($"{where}: clipped {Describe(fe)} {clip}");
            if (fe is TextBlock tb && Cut(tb))
            {
                if (HasTip(tb))
                    cutWithTip++;
                else
                    Problem($"{where}: text cut without a tooltip: {Describe(tb)}");
            }
            if (NeedsTip(fe))
            {
                object? tip = fe.ToolTip;
                if (tip == null)
                    Problem($"{where}: no tooltip on {Describe(fe)}");
                else if (tip is string s && (s.Length == 0 || RawKey.IsMatch(s)) && seenTips.Add(s))
                    Problem($"{where}: tooltip is a missing text key '{s}' on {Describe(fe)}");
            }
        }
        if (cutWithTip > 0)
            Log($"  {cutWithTip} long names shortened with … and shown whole in their tooltip");
    }

    // Template parts belong to their control (checked itself); AvalonDock's own buttons carry its own hints.
    private static bool NeedsTip(FrameworkElement fe) =>
        fe.TemplatedParent == null && fe is ButtonBase or TextBox or ComboBox or Controls.ScrubLabel or Controls.ValueSlider or Controls.RangeBar
        || (fe is MenuItem { Role: MenuItemRole.TopLevelHeader } && fe.TemplatedParent == null);

    private static bool HasTip(FrameworkElement fe)
    {
        for (DependencyObject? d = fe; d != null; d = VisualTreeHelper.GetParent(d))
            if (d is FrameworkElement { ToolTip: not null })
                return true;
        return false;
    }

    private static bool Cut(TextBlock tb)
    {
        if (tb.TextWrapping != TextWrapping.NoWrap || string.IsNullOrEmpty(tb.Text))
            return false;
        var ft = new FormattedText(tb.Text, CultureInfo.CurrentUICulture, tb.FlowDirection,
            new Typeface(tb.FontFamily, tb.FontStyle, tb.FontWeight, tb.FontStretch), tb.FontSize, Brushes.Black, null,
            TextOptions.GetTextFormattingMode(tb), VisualTreeHelper.GetDpi(tb).PixelsPerDip);
        return ft.WidthIncludingTrailingWhitespace > tb.ActualWidth - tb.Padding.Left - tb.Padding.Right + 1;
    }

    // Outside the nearest sized ancestor by more than a pixel, in a direction no scroll viewer between them scrolls.
    private static string? Clipped(FrameworkElement fe, FrameworkElement root)
    {
        DependencyObject? up = VisualTreeHelper.GetParent(fe);
        bool scrollX = false, scrollY = false;
        while (up != null)
        {
            if (up is ScrollContentPresenter scp && scp.TemplatedParent is ScrollViewer sv)
            {
                scrollX |= sv.HorizontalScrollBarVisibility != ScrollBarVisibility.Disabled;
                scrollY |= sv.VerticalScrollBarVisibility != ScrollBarVisibility.Disabled;
            }
            if (up is IScrollInfo info)
            {
                scrollX |= info.CanHorizontallyScroll;
                scrollY |= info.CanVerticallyScroll;
            }
            if (up is FrameworkElement p && p.ActualWidth > 0 && p.ActualHeight > 0 && p is not Canvas && up is not IScrollInfo)
            {
                Rect r;
                try
                {
                    r = fe.TransformToAncestor(p).TransformBounds(new Rect(0, 0, fe.ActualWidth, fe.ActualHeight));
                }
                catch (InvalidOperationException)
                {
                    return null;
                }
                const double Slack = 1.5;
                bool outX = r.Left < -Slack || r.Right > p.ActualWidth + Slack;
                bool outY = r.Top < -Slack || r.Bottom > p.ActualHeight + Slack;
                if ((outX && !scrollX) || (outY && !scrollY))
                    return $"by {Math.Max(Math.Max(-r.Left, r.Right - p.ActualWidth), Math.Max(-r.Top, r.Bottom - p.ActualHeight)):0.0} px in {p.GetType().Name}" + Ancestors(p);
                return null;
            }
            if (up == root)
                return null;
            up = VisualTreeHelper.GetParent(up);
        }
        return null;
    }

    // The clipping parent's own parents with their widths, so a report names the control that gave too little room.
    private static string Ancestors(FrameworkElement p)
    {
        const int Shown = 6;
        var sb = new System.Text.StringBuilder(" (");
        var up = VisualTreeHelper.GetParent(p);
        for (int i = 0; i < Shown && up != null; up = VisualTreeHelper.GetParent(up))
            if (up is FrameworkElement f)
            {
                sb.Append(i++ > 0 ? " < " : "").Append(f.GetType().Name).Append(' ').Append(f.ActualWidth.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture));
                if (f.Margin != default)
                    sb.Append(" m").Append(f.Margin);
            }
        return sb.Append(')').ToString();
    }

    private static string Describe(FrameworkElement fe)
    {
        string text = fe switch
        {
            TextBlock t => t.Text,
            ContentControl { Content: string s } => s,
            TextBox b => b.Text,
            _ => fe.ToolTip as string ?? "",
        };
        if (text.Length > 50)
            text = text[..50] + "…";
        string name = string.IsNullOrEmpty(fe.Name) ? "" : "#" + fe.Name;
        return $"{fe.GetType().Name}{name} '{text}' ({fe.ActualWidth:0}x{fe.ActualHeight:0})";
    }

    // ---- round trip through the document model ----

    private void RoundTrip()
    {
        string dir = Path.Combine(_dir, "roundtrip");
        Directory.CreateDirectory(dir);
        foreach (var doc in _w.Docs)
        {
            byte[] original = File.ReadAllBytes(doc.Path);
            string copy = Path.Combine(dir, Path.GetFileName(doc.Path));
            File.WriteAllBytes(copy, original);
            var stamp = File.GetLastWriteTimeUtc(copy);
            var opened = EffectDoc.Open(copy);
            int written = opened.Save();
            bool untouched = written == 0 && File.GetLastWriteTimeUtc(copy) == stamp;
            string saved = Path.Combine(dir, "saved_" + Path.GetFileName(doc.Path));
            opened.Save(saved);
            bool same = File.ReadAllBytes(saved).AsSpan().SequenceEqual(original);
            Log($"round trip {Path.GetFileName(doc.Path)}: save without edits rewrites nothing {untouched}; save as is byte-identical {same} ({original.Length} bytes)");
            if (!untouched || !same)
                Problem("round trip differs for " + doc.Path);
        }
    }
}
