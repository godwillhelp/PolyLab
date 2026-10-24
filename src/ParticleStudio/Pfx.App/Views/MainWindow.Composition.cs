using System.Numerics;
using System.Windows;
using Microsoft.Win32;
using Pfx.App.Core;
using Pfx.App.Model;
using Pfx.App.Preview;
using Pfx.App.Recipes;
using Pfx.App.Timeline;
using Pfx.App.ViewModels;
using Pfx.Core.Generators;
using Pfx.Core.Model;
using Pfx.Render;
using Pfx.Sim;
using Pfx.XRay;

namespace Pfx.App.Views;

// The open project (spec 3.8): its document, the one undo history over the project and the parts, the states played
// by the preview (each part runs in the windows of its state runs), the viewport's markers for lights, hazes, shells,
// near radii, strikes and the far rings, and the sounds of the states on the scene clock.
public partial class MainWindow : IComposeHost, IExportHost
{
    // Far rings of the K design's client LOD (§5.2): lights within 60 m, idle visuals within 120 m.
    private static readonly (float Radius, string Key)[] FarRings = { (60, "Ov.RingLights"), (120, "Ov.RingIdle") };
    // Line segments per circle: a 60 m ring then has 1.9 m chords, smooth at any zoom the viewport offers.
    private const int CircleSegments = 192;

    private CompositionDoc? _project;
    private readonly ShellHistory _history = new();
    private bool _statesPlaying;
    private ProjectTimeline? _timeline;
    private readonly SoundPreview _sounds = new();
    private double _soundClock = -1;
    private bool _projectCloseConfirmed;

    public CompositionDoc? Project => _project;
    public ProjectTimeline? ProjectTimeline => _statesPlaying ? _timeline : null;
    public ComposeView ComposeTab => ComposePanel;
    public TimelineView TimelineView => TimelinePanel;
    public ExportView ExportTab => ExportPanel;

    private void InitComposition()
    {
        ComposePanel.Init(this);
        ExportPanel.Init(this);
        _pipeline.WindowsOf = WindowsOf;
        Closed += (_, _) => _sounds.Dispose();
    }

    public bool StatesPlaying
    {
        get => _statesPlaying;
        set
        {
            if (_statesPlaying == value)
                return;
            _statesPlaying = value && _project != null;
            RefreshTimeline();
            _pipeline.Loop = _statesPlaying && _timeline != null ? (0f, (float)_timeline.Length) : null;
            foreach (var d in _docs)
                _pipeline.Request(d);
            _pipeline.Seek(0);
            _pipeline.Playing = true;
            _sounds.StopAll();
            _soundClock = -1;
            ComposePanel.Show();
            TimelinePanel.FitNow();
            UpdateButtons();
        }
    }

    public ExportSnapshot TakeSnapshot() => new(_docs, _roots.Roots);

    public void ExportWritten(Pfx.Core.Export.ExportPlan plan)
    {
    }

    // A zone import as a project next to its parts (<import folder>\cg_xray\<tag>\<zone>.ndzpfx), its sounds copied
    // beside them (the X-Ray files stay where the user's install has them).
    public void OpenZoneProject(Pfx.App.XRay.ImportRecord record, string stage)
    {
        if (!ProjectMayClose())
            return;
        string folder = Path.Combine(stage, record.Tag);
        var records = Pfx.App.XRay.RecordProject.ZoneOf(record, Path.Combine(folder, "particles"));
        string Rel(string full) => Path.GetRelativePath(folder, full).Replace('\\', '/');
        string? Sound(Pfx.App.XRay.ImportRecord.SoundData s)
        {
            if (s.File == null || !File.Exists(s.File))
                return null;
            string target = Path.Combine(folder, "sounds", Path.GetFileName(s.File));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (!File.Exists(target) || new FileInfo(target).Length != new FileInfo(s.File).Length)
                File.Copy(s.File, target, true);
            return Rel(target);
        }
        XrLightAnim? Anim(string name)
        {
            if (_xray.Index is not { } index || _xray.IndexSource(record.Tag) is not { } source)
                return null;
            string? variant = record.Variant == "core" ? null : record.Variant;
            return index.Order(source, variant).SelectMany(d => d.Lanims).FirstOrDefault(a => a.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));
        }
        var file = Pfx.App.XRay.RecordProject.From(records, rel => Rel(Path.Combine(stage, rel)), Sound, Anim);
        file.Export.Cg = new CgProfile { DonorFolder = Settings.CgDonorFolder, SlotFile = Settings.CgSlotFile, Type = record.PType };
        _projectCloseConfirmed = true;
        SetProject(new CompositionDoc(null, file));
        SaveProjectTo(Path.Combine(folder, file.Name + ".ndzpfx"));
        Open(file.Parts.Select(p => Path.GetFullPath(Path.Combine(folder, p.Path))).Where(File.Exists));
        foreach (var part in _project!.File.Parts)
            if (DocOfPart(part) is { } d)
                d.InitMotion(CompositionDoc.MotionOf(part));
        ShowPane("compose");
        Toast(Loc.F("Toast.CmpZone", file.Name, file.States.Count, file.Sounds.Count, file.Lights.Count), "ok");
    }

    // ---- project documents ----

    public void NewProject()
    {
        if (!ProjectMayClose())
            return;
        var file = new ProjectFile
        {
            Name = _docs.Count > 0 ? ProjectNames.Id(Path.GetFileNameWithoutExtension(_docs[0].Path)) : "effect",
            States = { new StateEntry { Name = "idle" } },
        };
        file.Export.Cg = new CgProfile { DonorFolder = Settings.CgDonorFolder, SlotFile = Settings.CgSlotFile };
        foreach (var d in _docs)
        {
            string id = ProjectNames.Unique(ProjectNames.Id(Path.GetFileNameWithoutExtension(d.Path)), file.Items().Select(i => i.Id));
            file.Parts.Add(new PartEntry { Id = id, Path = Path.GetFullPath(d.Path) });
            file.States[0].Items.Add(new StateItem { Ref = id });
        }
        SetProject(new CompositionDoc(null, file));
        Toast(Loc.F("Toast.CmpNew", file.Parts.Count), "ok");
    }

    // Generator (spec 3.10, O8): the CG tornado as own_tornado.py builds it, written into a folder of the user's choice
    // with its project, then opened. The bolt donor is asked for once and remembered.
    public void NewTornado()
    {
        if (!ProjectMayClose())
            return;
        var folder = new OpenFolderDialog { Title = Loc.T("Gen.PickFolder") };
        if (folder.ShowDialog(this) != true)
            return;
        string? donor = File.Exists(Settings.TornadoDonor) ? Settings.TornadoDonor : null;
        if (donor == null)
        {
            var pick = new OpenFileDialog { Filter = Loc.T("Filter.Ptc"), Title = Loc.T("Gen.PickDonor") };
            if (pick.ShowDialog(this) == true)
            {
                donor = pick.FileName;
                Settings.TornadoDonor = donor;
                if (!_launch.Automated)
                    Settings.Save();
            }
        }
        GenerateTornado(folder.FolderName, donor);
    }

    // A new project in the folder holding one tornado generator node (P12b: its settings stay editable on the node),
    // saved at once so its parts are files; refuses a folder that already holds one of the files (it never overwrites).
    public string? GenerateTornado(string folder, string? donor)
    {
        var settings = new TornadoSettings { BoltDonor = donor };
        string name = ProjectNames.Id(settings.Name);
        string project = Path.Combine(folder, name + ".ndzpfx");
        var parts = TornadoGenerator.Generate(settings).Parts.Select(p => Path.Combine(folder, TornadoGenerator.PartsFolder, settings.Name + p.Suffix + ".ptc"));
        if (parts.Append(project).FirstOrDefault(File.Exists) is { } taken)
        {
            Toast(Loc.F("Toast.GenExists", taken), "err");
            return null;
        }
        Directory.CreateDirectory(folder);
        var file = new ProjectFile { Name = name };
        file.Export.Cg = new CgProfile { DonorFolder = Settings.CgDonorFolder, SlotFile = Settings.CgSlotFile };
        SetProject(new CompositionDoc(project, file));
        var values = new Dictionary<string, string>();
        if (donor != null)
            values["donor"] = donor;
        AddRecipe(RecipeCatalog.Find("gen_tornado")!, null, Array.Empty<RecipeTarget>(), values);
        if (_project!.File.Recipes.Count == 0)
            return null;
        SaveProjectTo(null);
        Toast(Loc.F("Toast.GenDone", _project.File.Parts.Count, folder), "ok");
        return project;
    }

    public void OpenProjectDialog()
    {
        var dialog = new OpenFileDialog { Filter = Loc.T("Filter.Project") };
        if (dialog.ShowDialog(this) == true)
            OpenProject(dialog.FileName);
    }

    public void OpenProject(string path)
    {
        if (!ProjectMayClose())
            return;
        CompositionDoc doc;
        try
        {
            doc = CompositionDoc.Open(path);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            Toast(Loc.F("Toast.CmpOpenFailed", Path.GetFileName(path), ex.Message), "err");
            return;
        }
        SetProject(doc);
        var missing = doc.File.Parts.Where(p => !File.Exists(doc.Resolve(p.Path))).ToList();
        Open(doc.File.Parts.Select(p => doc.Resolve(p.Path)).Where(File.Exists));
        foreach (var part in doc.File.Parts)
            if (DocOfPart(part) is { } d)
                d.InitMotion(CompositionDoc.MotionOf(part));
        Settings.AddRecent(Path.GetFullPath(path));
        if (!_launch.Automated)
            Settings.Save();
        if (missing.Count > 0)
            Toast(Loc.F("Toast.CmpPartsMissing", string.Join(", ", missing.Select(m => m.Id))), "warn");
        BuildMenu();
    }

    // A dirty project needs a second request before it is replaced (the toast says so; no message box).
    private bool ProjectMayClose()
    {
        if (_project == null || !_project.Dirty || _projectCloseConfirmed || _launch.Automated)
        {
            _projectCloseConfirmed = false;
            return true;
        }
        _projectCloseConfirmed = true;
        Toast(Loc.T("Toast.CmpDirty"), "warn");
        return false;
    }

    private void SetProject(CompositionDoc doc)
    {
        if (_project != null)
            _history.Forget(_project);
        _project = doc;
        doc.DocOf = DocOfPart;
        doc.Changed += OnProjectChanged;
        _statesPlaying = false;
        _pipeline.Loop = null;
        OnProjectChanged();
    }

    public void SaveProject(bool saveAs)
    {
        if (_project == null)
            return;
        string? path = null;
        if (saveAs || _project.Path == null)
        {
            var dialog = new SaveFileDialog { Filter = Loc.T("Filter.Project"), FileName = _project.File.Name + ".ndzpfx", InitialDirectory = _project.Folder ?? "" };
            if (dialog.ShowDialog(this) != true)
                return;
            path = dialog.FileName;
        }
        SaveProjectTo(path);
    }

    // Saves the project and every changed part of it (the project is the source of truth, its parts are its files).
    public void SaveProjectTo(string? path)
    {
        if (_project == null)
            return;
        try
        {
            int parts = 0;
            foreach (var d in _docs.Where(d => d.Dirty && PartOf(d) != null))
                parts += d.Save();
            _project.Save(path);
            Settings.AddRecent(_project.Path!);
            if (!_launch.Automated)
                Settings.Save();
            Toast(Loc.F("Toast.CmpSaved", _project.Title, parts), "ok");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Toast(Loc.F("Toast.SaveFailed", _project.Title, ex.Message), "err");
        }
    }

    // A new empty part (an effect with no emitters, as the vanilla files start) written where the user chose and opened.
    public EffectDoc? NewPart(string path)
    {
        string full = Path.GetFullPath(path);
        if (!File.Exists(full))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, System.Text.Encoding.ASCII.GetBytes("EffectDef {\n Emitors {\n }\n}"));
        }
        Open(new[] { full });
        return _docs.FirstOrDefault(d => string.Equals(Path.GetFullPath(d.Path), full, StringComparison.OrdinalIgnoreCase));
    }

    public void NewPartDialog()
    {
        var dialog = new SaveFileDialog { Filter = Loc.T("Filter.Ptc"), FileName = "part.ptc", InitialDirectory = _project?.Folder ?? "" };
        if (dialog.ShowDialog(this) != true)
            return;
        if (NewPart(dialog.FileName) is { } doc)
            AddOpenPart(doc);
    }

    public void AddPartFile()
    {
        var dialog = new OpenFileDialog { Filter = Loc.T("Filter.Ptc"), Multiselect = true };
        if (dialog.ShowDialog(this) != true)
            return;
        Open(dialog.FileNames);
        foreach (var f in dialog.FileNames)
            if (_docs.FirstOrDefault(d => string.Equals(d.Path, Path.GetFullPath(f), StringComparison.OrdinalIgnoreCase)) is { } doc)
                AddOpenPart(doc);
    }

    public void AddOpenPart(EffectDoc doc)
    {
        if (_project == null || PartOf(doc) != null)
            return;
        string id = ProjectNames.Unique(ProjectNames.Id(Path.GetFileNameWithoutExtension(doc.Path)), _project.File.Items().Select(i => i.Id));
        string rel = _project.Relative(Path.GetFullPath(doc.Path));
        int state = ComposePanel.SelectedState;
        _project.Do(f =>
        {
            f.Parts.Add(new PartEntry { Id = id, Path = rel });
            if (state < f.States.Count)
                f.States[state].Items.Add(new StateItem { Ref = id });
        }, Loc.F("Undo.CmpAddItem", id));
    }

    // A sound file: copied in place only by reference (relative to the project); an X-Ray .ogg brings its range and
    // volume from its Vorbis comment (xray.py's ogg_params rule, XrSoundParams).
    public void PickSound(string soundId)
    {
        if (_project == null)
            return;
        var dialog = new OpenFileDialog { Filter = Loc.T("Filter.Ogg") };
        if (dialog.ShowDialog(this) != true)
            return;
        SetSoundFile(soundId, dialog.FileName);
    }

    public void SetSoundFile(string soundId, string file)
    {
        if (_project == null)
            return;
        XrSoundParams? x = null;
        try
        {
            using var f = File.OpenRead(file);
            var head = new byte[(int)Math.Min(f.Length, XrRecognize.HeadBytes)];
            f.ReadExactly(head);
            x = XrSoundParams.Read(head);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
        }
        string rel = _project.Relative(Path.GetFullPath(file));
        _project.Do(p =>
        {
            if (p.Find(soundId) is not SoundEntry s)
                return;
            s.File = rel;
            s.GameSet = null;
            if (x?.Range is double range)
                s.Range = (float)range;
            if (x?.Volume is double volume)
                s.Volume = (float)volume;
        }, Loc.F("Undo.Cmp", Loc.T("Cmp.SoundFile")));
    }

    public void PreviewSound(string soundId)
    {
        if (_project?.File.Find(soundId) is not SoundEntry { File: { } file } s)
            return;
        string key = "panel:" + soundId;
        if (_sounds.IsPlaying(key))
        {
            _sounds.Stop(key);
            return;
        }
        try
        {
            _sounds.Play(key, _project.Resolve(file), s.Volume, false);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or NAudio.MmException)
        {
            Toast(Loc.F("Toast.CmpSoundFailed", Path.GetFileName(file), ex.Message), "err");
        }
    }

    public void SelectPart(string partId)
    {
        if (_project?.File.Parts.FirstOrDefault(p => p.Id == partId) is { } part && DocOfPart(part) is { } doc)
            Select(doc, Array.Empty<Pfx.Core.Text.EnfBlock>(), true);
    }

    private EffectDoc? DocOfPart(PartEntry part)
    {
        if (_project == null)
            return null;
        string full = _project.Resolve(part.Path);
        return _docs.FirstOrDefault(d => string.Equals(Path.GetFullPath(d.Path), full, StringComparison.OrdinalIgnoreCase));
    }

    public string? PartOf(EffectDoc doc) =>
        _project?.File.Parts.FirstOrDefault(p => string.Equals(_project.Resolve(p.Path), Path.GetFullPath(doc.Path), StringComparison.OrdinalIgnoreCase))?.Id;

    private string _recipeRows = "";

    private void OnProjectChanged()
    {
        if (_project == null)
            return;
        _history.Sync(_project);
        // The layers panel lists the recipe nodes: rebuilt when a node came, went, switched or changed what it made.
        string rows = string.Join("|", _project.File.Recipes.Select(r => r.Id + ":" + r.On + ":" + r.Log.Count));
        if (rows != _recipeRows)
        {
            _recipeRows = rows;
            RebuildLayers();
            if (_active != null)
                Layers.SetSelection(_state.Layers.Where(r => r is EmitterRow er && _selected.Contains(er.Block)));
        }
        RecipesPanel.Show();
        RefreshTimeline();
        if (_statesPlaying && _timeline != null)
            _pipeline.Loop = (0f, (float)_timeline.Length);
        foreach (var d in _docs)
            _pipeline.Request(d);
        ComposePanel.Show();
        ExportPanel.Invalidate();
        RequestLint();
        TimelinePanel.Rebuild();
        UpdateTitle();
    }

    private void RefreshTimeline()
    {
        if (_project == null)
        {
            _timeline = null;
            return;
        }
        var parts = _docs.Where(d => PartOf(d) != null).Select(d => (d, ParamsOf(d))).ToList();
        // A state that lasts until an event gets the time the parts need to show their whole look: every emitter's
        // effect time plus its longest particle life (TimelineModel.SceneLength).
        double open = parts.Count > 0 ? TimelineModel.SceneLength(parts) : 1;
        var file = _project.File;
        _timeline = new ProjectTimeline(file, StateCycle.Spans(file, open), StateCycle.Runs(file, open), Math.Max(StateCycle.Length(file, open), 0.1), PartOf);
    }

    private WindowSet? WindowsOf(EffectDoc doc)
    {
        if (!_statesPlaying || _timeline == null)
            return null;
        string? id = PartOf(doc);
        return new WindowSet(_timeline.Runs.Where(r => r.Kind == ItemKind.Part && r.Ref == id)
            .Select(r => new SimWindow((float)r.Start, (float)r.End, r.HardStop is double h ? (float)h : null)).ToList());
    }

    // ---- sounds of the states on the scene clock (after every presented frame) ----

    private void TickSounds()
    {
        if (!_statesPlaying || _timeline == null || _project == null || !_pipeline.Playing)
        {
            if (_soundClock >= 0)
            {
                foreach (var r in _timeline?.Runs.Where(r => r.Kind == ItemKind.Sound) ?? Enumerable.Empty<ItemRun>())
                    _sounds.Stop(RunKey(r));
                _soundClock = -1;
            }
            return;
        }
        double now = _pipeline.Time;
        bool wrapped = now < _soundClock;
        foreach (var r in _timeline.Runs.Where(r => r.Kind == ItemKind.Sound))
        {
            string key = RunKey(r);
            bool inside = now >= r.Start && now < r.End;
            bool started = _soundClock < r.Start && now >= r.Start || wrapped && inside;
            if (!inside && _sounds.IsPlaying(key) && _project.File.Find(r.Ref) is SoundEntry { Loop: true })
                _sounds.Stop(key);
            if (!started || _project.File.Find(r.Ref) is not SoundEntry { File: { } file } s)
                continue;
            try
            {
                _sounds.Play(key, _project.Resolve(file), s.Volume, s.Loop);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or NAudio.MmException)
            {
                Toast(Loc.F("Toast.CmpSoundFailed", Path.GetFileName(file), ex.Message), "err");
            }
        }
        _soundClock = now;
    }

    private static string RunKey(ItemRun r) => $"run:{r.State}:{r.Ref}:{r.Start}";

    // ---- viewport markers ----

    private void ComposeProjectLines(List<LineVertex> lines)
    {
        if (_project == null)
            return;
        var p = _project.File;
        double t = _pipeline.Time;
        bool Active(string id) => !_statesPlaying || _timeline == null || _timeline.Runs.Any(r => r.Ref == id && t >= r.Start && t < r.End);
        var origin = _pipeline.Settings.Origin;
        foreach (var (name, shell) in p.Anomaly.Shells.Where(s => s.Value.R > 0))
            Cylinder(lines, origin, shell.R, shell.YMin, shell.YMax, ShellColor(name));
        if (ComposePanel.FarRings)
            foreach (var (radius, _) in FarRings)
                Circle(lines, origin, radius, 0.02f, radius == 60 ? Palette.Accent.WithAlpha(0.7f) : Palette.TextDim.WithAlpha(0.7f));
        foreach (var part in p.Parts.Where(x => x.Near > 0))
            Circle(lines, origin, part.Near, 0.03f, Palette.Path.WithAlpha(0.6f));
        foreach (var l in p.Lights)
        {
            var at = origin + new Vector3(l.Offset[0], l.Offset[1], l.Offset[2]);
            var c = LightColor(l.Color).WithAlpha(Active(l.Id) ? 0.9f : 0.3f);
            Star(lines, at, 0.25f, c);
            Circle(lines, origin + new Vector3(l.Offset[0], 0, l.Offset[2]), l.Radius, 0.02f, c.WithAlpha(c.W * 0.6f));
            if (l.HazeRadius > 0)
                Circle(lines, at, l.HazeRadius, 0, Palette.Warn.WithAlpha(0.5f));
        }
        foreach (var h in p.Hazes)
        {
            var at = origin + new Vector3(h.At[0], h.At[1], h.At[2]);
            var c = Palette.Warn.WithAlpha(Active(h.Id) ? 0.85f : 0.3f);
            Circle(lines, at, h.Radius, 0, c);
            Circle(lines, at, h.Radius * 0.5f, 0, c.WithAlpha(c.W * 0.6f));
        }
        foreach (var k in p.Strikes)
        {
            var c = Palette.Key.WithAlpha(Active(k.Id) ? 0.85f : 0.3f);
            Circle(lines, origin, k.RadiusLow, 0.05f, c);
            Circle(lines, origin, k.RadiusHigh, k.Height, c);
            SceneGeometry.Segment(lines, origin + new Vector3(k.RadiusLow, 0, 0), origin + new Vector3(k.RadiusHigh, k.Height, 0), c);
        }
    }

    // Glows of the lights that shine now, as camera-facing discs whose size follows radius × brightness of the curve.
    internal void ComposeProjectSprites(SpriteBatch batch)
    {
        if (_project == null || _project.File.Lights.Count == 0)
            return;
        var (right, up, _) = _viewport.Camera.Basis();
        double t = _pipeline.Time;
        var origin = _pipeline.Settings.Origin;
        batch.Begin(null, SpriteBlend.Add);
        foreach (var l in _project.File.Lights)
        {
            double k = 1;
            if (_statesPlaying && _timeline != null)
            {
                var run = _timeline.Runs.FirstOrDefault(r => r.Ref == l.Id && t >= r.Start && t < r.End);
                if (run == null)
                    continue;
                k = LightCurve(l, t - run.Start);
            }
            var at = origin + new Vector3(l.Offset[0], l.Offset[1], l.Offset[2]);
            float size = 0.2f * (float)Math.Clamp(k, 0, 4);
            var c = LightColor(l.Color);
            batch.Fan(at, right * size, up * size, new Vector4(c.X, c.Y, c.Z, 0.35f), new Vector4(c.X, c.Y, c.Z, 0));
        }
        batch.Close();
    }

    // The brightness multiplier of a light's curve at a time since it started (straight between keys, as the
    // generated runtime's CurveAt).
    public static double LightCurve(LightEntry l, double t)
    {
        var keys = l.Curve;
        if (keys.Count == 0)
            return 1;
        double at = t, period = keys[^1][0];
        if (l.CurveLoop && period > 0)
            at = t % period;
        if (at <= keys[0][0])
            return keys[0][1];
        for (int i = 1; i < keys.Count; i++)
        {
            if (at > keys[i][0])
                continue;
            double span = keys[i][0] - keys[i - 1][0];
            return span <= 0 ? keys[i][1] : keys[i - 1][1] + (keys[i][1] - keys[i - 1][1]) * (at - keys[i - 1][0]) / span;
        }
        return keys[^1][1];
    }

    // The project's lines of the viewport's text panel: its name, the state now playing and what of it shines and sounds.
    private List<OverlayLine> ProjectOverlayLines()
    {
        var lines = new List<OverlayLine>();
        if (_project == null)
            return lines;
        lines.Add(new(Loc.F("Ov.Project", _project.Title), Palette.TextHi));
        if (_statesPlaying && _timeline != null)
        {
            double t = _pipeline.Time;
            if (_timeline.States.FirstOrDefault(s => t >= s.From && t < s.To) is { } st)
                lines.Add(new(Loc.F("Ov.State", ComposeView.StateLabel(st.Name), t - st.From, st.To - st.From), Palette.Accent));
            int lights = _timeline.Runs.Count(r => r.Kind is ItemKind.Light or ItemKind.Haze && t >= r.Start && t < r.End);
            int sounds = _timeline.Runs.Count(r => r.Kind == ItemKind.Sound && t >= r.Start && t < r.End);
            lines.Add(new(Loc.F("Ov.StateItems", lights, sounds), Palette.Text));
        }
        else
        {
            lines.Add(new(Loc.T("Ov.StatesOff"), Palette.TextDim));
        }
        return lines;
    }

    // Labels of the far rings where they cross the ground towards the camera's right.
    private void ComposeProjectOverlay(OverlayBuilder o)
    {
        if (_project == null || !ComposePanel.FarRings)
            return;
        foreach (var (radius, key) in FarRings)
        {
            var at = _pipeline.Settings.Origin + new Vector3(radius * 0.7071f, 0, -radius * 0.7071f);
            if (_viewport.Camera.Project(at, _viewport.PixelWidth, _viewport.PixelHeight) is { } px)
                o.Text(px.X + 4, px.Y - 14, Loc.F(key, radius), Palette.Text);
        }
    }

    private static Vector4 ShellColor(string name) => name switch
    {
        "sense" => Palette.Warn,
        "hit" => Palette.Hex(0xE5534B),
        _ => Palette.Hex(0x4B9BF5),
    };

    // A light colour (linear 0..1, the light API's) brought into the palette's range so dark lights stay visible.
    private static Vector4 LightColor(float[] c)
    {
        float m = Math.Max(Math.Max(c[0], c[1]), Math.Max(c[2], 1e-3f));
        return new Vector4(c[0] / m, c[1] / m, c[2] / m, 1);
    }

    private static void Circle(List<LineVertex> lines, Vector3 centre, float radius, float height, Vector4 color)
    {
        for (int i = 0; i < CircleSegments; i++)
        {
            float a0 = i * MathF.Tau / CircleSegments, a1 = (i + 1) * MathF.Tau / CircleSegments;
            SceneGeometry.Segment(lines, centre + new Vector3(MathF.Cos(a0) * radius, height, MathF.Sin(a0) * radius),
                centre + new Vector3(MathF.Cos(a1) * radius, height, MathF.Sin(a1) * radius), color);
        }
    }

    private static void Cylinder(List<LineVertex> lines, Vector3 origin, float r, float y0, float y1, Vector4 color)
    {
        Circle(lines, origin, r, y0, color);
        Circle(lines, origin, r, y1, color);
        for (int i = 0; i < 8; i++)
        {
            float a = i * MathF.Tau / 8;
            var d = new Vector3(MathF.Cos(a) * r, 0, MathF.Sin(a) * r);
            SceneGeometry.Segment(lines, origin + d + new Vector3(0, y0, 0), origin + d + new Vector3(0, y1, 0), color.WithAlpha(color.W * 0.6f));
        }
    }

    private static void Star(List<LineVertex> lines, Vector3 at, float size, Vector4 color)
    {
        SceneGeometry.Segment(lines, at - Vector3.UnitX * size, at + Vector3.UnitX * size, color);
        SceneGeometry.Segment(lines, at - Vector3.UnitY * size, at + Vector3.UnitY * size, color);
        SceneGeometry.Segment(lines, at - Vector3.UnitZ * size, at + Vector3.UnitZ * size, color);
    }
}
