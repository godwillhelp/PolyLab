using System.Diagnostics;
using Pfx.App.Model;
using Pfx.Core.Model;
using Pfx.Core.Text;
using Pfx.Render;
using Pfx.Sim;

namespace Pfx.App.Preview;

// Keeps the viewport's previews in step with the open documents without blocking the UI (spec 1.1 rule 8). An edit
// costs the UI thread one byte snapshot of the file; a worker parses that copy, switches off the births of hidden
// emitters (indices stay, so the other emitters keep their random streams), builds the EffectPreview and brings it to
// the playback time, exact there (emitters the edit did not touch are copied from the old preview's checkpoint); the
// finished preview replaces the old one, which keeps playing meanwhile. Edits that arrive during a build are
// coalesced: only the newest snapshot is built next. Seeks (timeline scrubbing, loop wraps) run on a worker too and are
// coalesced the same way; the UI thread only copies the finished state in.
public sealed class PreviewPipeline
{
    private sealed class Slot
    {
        public required EffectDoc Doc;
        public EffectPreview? Preview;
        public bool Busy;
        public Snapshot? Pending;
        public bool Seeking;
        public float? PendingSeek;
        // The time the user asked for last (scrub, loop wrap) until a preview stands there.
        public float? Wanted;
        public CancellationTokenSource? Filling;
    }

    private sealed record Snapshot(byte[] Ptc, string Path, HashSet<int> Off, List<(string Game, string Disk, byte[] Bytes)> Materials,
        PreviewSettings Settings, float Time, bool Playing, float? Hold, ReuseSource? Reuse, long Started);

    private readonly ViewportHost _viewport;
    private readonly Func<IReadOnlyList<string>> _roots;
    private readonly List<Slot> _slots = new();
    private readonly HashSet<Slot> _wrapping = new();
    private PreviewSettings _settings = new();
    private bool _playing = true;
    private (float From, float To)? _loop;

    public PreviewPipeline(ViewportHost viewport, Func<IReadOnlyList<string>> roots)
    {
        _viewport = viewport;
        _roots = roots;
    }

    // Edit (snapshot taken) to new preview on screen, in milliseconds, and the worker's share of it.
    public event Action<EffectDoc, double, double>? Built;
    public event Action<EffectDoc, Exception>? Failed;
    // A seek job's result is on screen: the document, the worker's milliseconds and the request-to-screen milliseconds.
    public event Action<EffectDoc, double, double>? Seeked;

    public PreviewSettings Settings
    {
        get => _settings;
        set
        {
            _settings = value;
            foreach (var s in _slots)
                Request(s.Doc);
        }
    }

    public bool Playing
    {
        get => _playing;
        set
        {
            _playing = value;
            _wrapping.Clear();
            foreach (var p in _viewport.Previews)
                p.Playing = value;
        }
    }

    public bool AnyBusy => _slots.Any(s => s.Busy);
    public bool AnyFilling => _slots.Any(s => s.Filling != null);
    public bool AnySeeking => _slots.Any(s => s.Seeking || s.PendingSeek != null);

    // The scene clock: where the user asked to be while a seek runs, else the first preview's playback time.
    public float Time => _slots.FirstOrDefault(s => s.Wanted != null)?.Wanted ?? _viewport.Preview?.Time ?? 0;

    // The work area the playback loops in (timeline), null = play on.
    public (float From, float To)? Loop
    {
        get => _loop;
        set
        {
            _loop = value is { } l && l.To > l.From ? value : null;
            foreach (var p in _viewport.Previews)
                p.HoldAt = _loop?.To;
        }
    }

    public EffectPreview? PreviewOf(EffectDoc doc) => _slots.FirstOrDefault(s => s.Doc == doc)?.Preview;

    public void Add(EffectDoc doc)
    {
        _slots.Add(new Slot { Doc = doc });
        Request(doc);
    }

    public void Remove(EffectDoc doc)
    {
        var slot = _slots.FirstOrDefault(s => s.Doc == doc);
        if (slot == null)
            return;
        _slots.Remove(slot);
        _wrapping.Remove(slot);
        slot.Filling?.Cancel();
        if (slot.Preview != null)
            _viewport.Previews.Remove(slot.Preview);
    }

    public void Request(EffectDoc doc)
    {
        var slot = _slots.FirstOrDefault(s => s.Doc == doc);
        if (slot == null)
            return;
        var snap = Take(slot);
        if (slot.Busy)
            slot.Pending = snap;
        else
            Start(slot, snap);
    }

    public void Restart()
    {
        _wrapping.Clear();
        foreach (var s in _slots)
        {
            s.Wanted = null;
            s.PendingSeek = null;
        }
        foreach (var p in _viewport.Previews)
            p.Restart();
    }

    public void Step()
    {
        Playing = false;
        foreach (var p in _viewport.Previews)
            p.StepOnce();
    }

    // Every part to time t (timeline scrub). Exact: the same t gives the same frame every time.
    public void Seek(float t)
    {
        foreach (var s in _slots)
            RequestSeek(s, Math.Max(0, t));
    }

    // Called after every presented frame: wraps the work-area loop once every part has reached its end. Parts wrap
    // together: playback pauses until each part's seek back has landed, so parts never drift apart at the seam.
    public void Tick()
    {
        if (_loop is not { } loop || !_playing || _wrapping.Count > 0)
            return;
        var shown = _slots.Where(s => s.Preview != null).ToList();
        if (shown.Count == 0 || shown.Any(s => s.Busy || s.Seeking || s.Preview!.ShownSteps < s.Preview.StepsOf(loop.To)))
            return;
        foreach (var s in shown)
        {
            s.Preview!.Playing = false;
            _wrapping.Add(s);
        }
        foreach (var s in shown)
            RequestSeek(s, loop.From);
    }

    private void RequestSeek(Slot slot, float t)
    {
        slot.Wanted = t;
        if (slot.Seeking || slot.Busy)
        {
            slot.PendingSeek = t;
            return;
        }
        var preview = slot.Preview;
        if (preview == null)
            return;
        var job = preview.PrepareSeek(t);
        if (job == null)
        {
            preview.Time = t;
            Landed(slot);
            return;
        }
        slot.Seeking = true;
        long started = Stopwatch.GetTimestamp();
        var ui = TaskScheduler.FromCurrentSynchronizationContext();
        Task.Run(job.Run).ContinueWith(task => FinishSeek(slot, job, task, started), ui);
    }

    private void FinishSeek(Slot slot, SeekJob job, Task task, long started)
    {
        slot.Seeking = false;
        if (!_slots.Contains(slot))
            return;
        if (task.Exception is { } ex)
            Failed?.Invoke(slot.Doc, ex.InnerException ?? ex);
        else if (slot.Preview == job.Preview && job.Preview.Apply(job))
            Seeked?.Invoke(slot.Doc, job.Ms, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        if (slot.PendingSeek is { } next)
        {
            slot.PendingSeek = null;
            RequestSeek(slot, next);
            return;
        }
        if (slot.Wanted is { } wanted && slot.Preview is { } p && p.ShownSteps != p.StepsOf(wanted))
        {
            RequestSeek(slot, wanted);
            return;
        }
        Landed(slot);
    }

    private void Landed(Slot slot)
    {
        slot.Wanted = null;
        Fill(slot);
        if (!_wrapping.Remove(slot) || _wrapping.Count > 0)
            return;
        foreach (var p in _viewport.Previews)
            p.Playing = _playing;
    }

    private Snapshot Take(Slot slot)
    {
        var doc = slot.Doc;
        var emitters = doc.Ptc.Emitters;
        var off = new HashSet<int>();
        for (int i = 0; i < emitters.Count; i++)
        {
            var b = emitters[i].Block;
            if (doc.Hidden.Contains(b) || (doc.Solo.Count > 0 && !doc.Solo.Contains(b)))
                off.Add(i);
        }
        var materials = doc.Materials.Values.Where(m => m.Dirty).Select(m => (m.GamePath, m.DiskPath, m.Emat.ToBytes())).ToList();
        var old = slot.Preview;
        return new Snapshot(doc.Ptc.ToBytes(), doc.Path, off, materials, SettingsFor(doc), slot.Wanted ?? old?.Time ?? 0, _playing && slot.Wanted == null,
            _loop?.To, old?.ForReuse(), Stopwatch.GetTimestamp());
    }

    // The runs of a part when the open project plays its states (set by the shell), null = the part plays from 0.
    public Func<EffectDoc, WindowSet?>? WindowsOf { get; set; }

    // The scene settings with the part's own script motion (drawn path or spin) and its runs in the project's states.
    // The motion records hold the document's arrays and the window sets compare by value, so an unchanged part
    // compares equal and a rebuild may take over the old checkpoints.
    private PreviewSettings SettingsFor(EffectDoc doc)
    {
        var s = _settings with { Windows = WindowsOf?.Invoke(doc) };
        return doc.Motion is not { } m ? s : s with
        {
            Script = m.IsPath
                ? new SourceMotion.Keyframed(m.Times!, m.Points!, m.YawAlongPath, _settings.Yaw)
                : new SourceMotion.Turning(_settings.Origin, _settings.Yaw, m.SpinRate),
        };
    }

    // Checkpoints for a stretch the preview jumped over, in the background while the part is otherwise idle; a rebuild
    // cancels it (its preview is replaced).
    private void Fill(Slot slot)
    {
        if (slot.Filling != null || slot.Busy || slot.Seeking || slot.PendingSeek != null || slot.Preview?.PrepareFill() is not { } job)
            return;
        var cancel = new CancellationTokenSource();
        slot.Filling = cancel;
        var ui = TaskScheduler.FromCurrentSynchronizationContext();
        Task.Run(() => job.Run(cancel.Token)).ContinueWith(task =>
        {
            if (slot.Filling == cancel)
                slot.Filling = null;
            cancel.Dispose();
            if (task.Exception is { } ex)
                Failed?.Invoke(slot.Doc, ex.InnerException ?? ex);
            else if (_slots.Contains(slot) && slot.Preview == job.Preview && job.Preview.Apply(job))
                Fill(slot);
        }, ui);
    }

    private void Start(Slot slot, Snapshot snap)
    {
        slot.Filling?.Cancel();
        slot.Filling = null;
        slot.Busy = true;
        var roots = _roots();
        var ui = TaskScheduler.FromCurrentSynchronizationContext();
        var textures = _viewport.Textures;
        Task.Run(() => Build(snap, roots, textures)).ContinueWith(t => Finish(slot, snap, t), ui);
    }

    private static (EffectPreview Preview, double Ms) Build(Snapshot snap, IReadOnlyList<string> rootList, TextureCache textures)
    {
        var clock = Stopwatch.StartNew();
        var doc = PtcDocument.From(EnfDocument.Parse(snap.Ptc));
        var emitters = doc.Emitters;
        foreach (int i in snap.Off)
        {
            foreach (var key in new[] { "BirthRate", "BirthRateRND", "BirthRateVel" })
                emitters[i].Set(key, 0).Apply();
        }
        var roots = new DataRoots(rootList);
        foreach (var (game, disk, bytes) in snap.Materials)
            roots.SetMaterial(game, MaterialInfo.From(EmatDocument.From(EnfDocument.Parse(bytes)), disk));
        var preview = new EffectPreview(doc, snap.Path, roots, snap.Settings);
        foreach (var map in preview.AlbedoPaths.Concat(preview.NormalPaths).OfType<string>().Distinct())
            textures.Decoded(map);
        preview.StartAt(snap.Time, snap.Reuse);
        if (snap.Playing)
            FollowClock(preview, snap);
        return (preview, clock.Elapsed.TotalMilliseconds);
    }

    // The old preview plays on during the build (up to a loop end). Steps on to where its clock is now, again while that converges
    // (a round simulating its interval in less real time than the interval), so the swap does not jump back in time.
    private static void FollowClock(EffectPreview preview, Snapshot snap)
    {
        while (true)
        {
            var round = Stopwatch.StartNew();
            float now = snap.Time + (float)Stopwatch.GetElapsedTime(snap.Started).TotalSeconds;
            if (snap.Hold is float hold)
                now = Math.Min(now, Math.Max(hold, snap.Time));
            float gap = now - preview.ShownTime;
            if (preview.StepsOf(now) <= preview.ShownSteps)
                return;
            preview.Seek(now);
            if (round.Elapsed.TotalSeconds >= gap)
                return;
        }
    }

    private void Finish(Slot slot, Snapshot snap, Task<(EffectPreview Preview, double Ms)> task)
    {
        slot.Busy = false;
        if (!_slots.Contains(slot))
            return;
        if (task.Exception is { } ex)
        {
            Failed?.Invoke(slot.Doc, ex.InnerException ?? ex);
        }
        else
        {
            var (preview, workerMs) = task.Result;
            var old = slot.Preview;
            preview.HoldAt = _loop?.To;
            preview.Playing = old?.Playing ?? _playing;
            // The rest of the old clock's lead (under a frame when FollowClock converged) is stepped by the next frame.
            float clock = slot.Wanted ?? old?.Time ?? snap.Time;
            if (clock >= preview.ShownTime)
                preview.Time = clock;
            int at = old != null ? _viewport.Previews.IndexOf(old) : -1;
            if (at >= 0)
                _viewport.Previews[at] = preview;
            else
                _viewport.Previews.Insert(Math.Min(_slots.IndexOf(slot), _viewport.Previews.Count), preview);
            slot.Preview = preview;
            double total = Stopwatch.GetElapsedTime(snap.Started).TotalMilliseconds;
            Built?.Invoke(slot.Doc, total, workerMs);
            if (clock < preview.ShownTime || slot.PendingSeek != null || slot.Wanted != null)
            {
                float target = slot.PendingSeek ?? slot.Wanted ?? clock;
                slot.PendingSeek = null;
                RequestSeek(slot, target);
            }
        }
        if (slot.Pending is { } next)
        {
            slot.Pending = null;
            Start(slot, next with { Time = slot.Wanted ?? slot.Preview?.Time ?? next.Time, Reuse = slot.Preview?.ForReuse() ?? next.Reuse });
        }
        else
        {
            Fill(slot);
        }
    }
}
