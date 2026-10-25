using System.Globalization;
using System.Numerics;
using Pfx.Core.Export;
using Pfx.Core.Model;
using Pfx.Core.Text;

namespace Pfx.Link;

// One part of the tool's scene as the game has it: its source id in the link mod, the text it was spawned from plus the
// sets sent since (Live), the names of its emitters in the game's order, and a reload in flight.
public sealed class LivePart
{
    internal LivePart(object key, int id, string name)
    {
        Key = key;
        Id = id;
        Name = name;
    }

    public object Key { get; }
    public int Id { get; }
    public string Name { get; internal set; }
    public string? GamePath { get; internal set; }
    public IReadOnlyList<string> GameEmitters { get; internal set; } = Array.Empty<string>();
    public bool InGame => GamePath != null && SpawnSeq == 0 && Live != null;
    public bool Loading => SpawnSeq != 0;
    // When the game reported this version spawned (tool clock): the source's age in the game is measured from here.
    public long SpawnedAt { get; internal set; }
    public string? Error { get; internal set; }

    internal PtcDocument? Live;
    internal string Stamp = "";
    internal int SpawnSeq;
    internal long SpawnAt;
    internal byte[]? Latest;
    internal IExportSource? LatestSource;
    internal long LatestAt;
    internal bool Behind;
}

public enum TimingKind { Set, Reload, Shot }

// A measured round trip: from the edit (or request) in the tool to the game's answer.
public sealed record LinkTiming(TimingKind Kind, double Ms, string What, bool Matches = true);

// A screenshot the game wrote: where, at which game time (ms since mission start), how long that frame took, and the
// field of view the game reported for its current camera (radians, Camera.GetCurrentFOV).
public sealed record GameShot(string Name, string File, int GameTime, double FrameMs, int Width, int Height, double RoundTripMs, double Fov);

// One emitter's engine originals next to the values in the file it was spawned from.
public sealed record OriginalRow(string Emitter, string Key, string File, string Game, bool Matches);

// The live link's particle logic (spec 3.12). Every change of an open part arrives here as its new bytes: the first
// time, or after a change that is not a parameter, the part is staged as a new version and the game spawns it in
// place of the old one; a change of GameParams keys only is sent as SetParticleParm. While a reload is on its way,
// newer changes wait and go as one more reload when the game reports the spawn (no timer: the answer is the event).
public sealed class LiveLink : IDisposable
{
    private sealed record SentSet(long At, string What, int Part, string Arguments);

    private readonly LinkHub _hub;
    private readonly Dictionary<object, LivePart> _parts = new();
    private readonly Dictionary<int, SentSet> _sets = new();
    private readonly Dictionary<string, long> _shots = new();
    private readonly Dictionary<int, List<OriginalRow>> _originals = new();
    private LiveStage? _stage;
    private int _nextId;
    private string _mark = "mark cam 6";
    private string? _camera;

    public LiveLink(LinkHub hub)
    {
        _hub = hub;
        _hub.Message += OnMessage;
        _hub.ClientChanged += _ => Resync();
    }

    public LinkHub Hub => _hub;
    public LiveStage? Stage => _stage;
    public string? GameFolder { get; private set; }
    public IReadOnlyCollection<LivePart> Parts => _parts.Values;

    public event Action<LinkTiming>? Timed;
    public event Action<LivePart>? PartChanged;
    public event Action<GameShot>? ShotTaken;
    public event Action<string, string>? Failed;
    public event Action<int, IReadOnlyList<OriginalRow>>? Originals;

    // Starts listening; the stage folder lives in the game folder for this session. Throws SocketException (port taken)
    // or IOException / UnauthorizedAccessException (game folder not writable).
    public void Start(string gameFolder, int port)
    {
        Stop();
        LiveStage.RemoveStale(gameFolder);
        _stage = new LiveStage(gameFolder);
        GameFolder = gameFolder;
        try
        {
            _hub.Start(port);
        }
        catch
        {
            _stage.Dispose();
            _stage = null;
            throw;
        }
    }

    // Ends the session: the game keeps what it shows until the next session's hello (the mod stops nothing by itself);
    // the stage folder is deleted.
    public void Stop()
    {
        _hub.Stop();
        _stage?.Dispose();
        _stage = null;
        foreach (var p in _parts.Values)
            Forget(p);
        _sets.Clear();
        _shots.Clear();
    }

    public void Dispose() => Stop();

    public bool Connected => _hub.State == LinkState.Connected && _stage != null;

    public LivePart? PartOf(object key) => _parts.TryGetValue(key, out var p) ? p : null;

    // The part's current text. Called after every change of the part (and once to show it in the game).
    public void Push(object key, string name, byte[] bytes, IExportSource source, long changedAt)
    {
        if (!_parts.TryGetValue(key, out var part))
        {
            part = new LivePart(key, ++_nextId, name);
            _parts[key] = part;
        }
        part.Name = name;
        part.Latest = bytes;
        part.LatestSource = source;
        part.LatestAt = changedAt;
        if (!Connected)
            return;
        if (part.Loading)
        {
            part.Behind = true;
            return;
        }
        Apply(part);
    }

    // The part leaves the game (its document closed, or the user took it out).
    public void Remove(object key)
    {
        if (!_parts.Remove(key, out var part))
            return;
        if (Connected && part.GamePath != null)
            _hub.Send("stop " + part.Id);
    }

    // Every part out of the game, with its particles (IMMEDIATE stop): the empty scene a baseline is shot against.
    public void ClearGame()
    {
        foreach (var p in _parts.Values)
            Forget(p);
        if (Connected)
            _hub.Send("clear");
    }

    // Everything sent again: after a new client said hello, or on the user's "reconnect".
    public void Resync()
    {
        foreach (var p in _parts.Values)
            Forget(p);
        _sets.Clear();
        if (!Connected)
            return;
        long now = LinkHub.Now;
        foreach (var p in _parts.Values)
            p.LatestAt = now;
        _hub.Send(_mark);
        if (_camera != null)
            _hub.Send(_camera, "cam");
        foreach (var p in _parts.Values.Where(p => p.Latest != null).ToList())
            Apply(p);
    }

    // The anchor every part is spawned at: on the ground this far in front of the game camera, or at a world position.
    public void MarkInFront(float metres)
    {
        _mark = "mark cam " + GameParams.Num(metres);
        Resync();
    }

    public void MarkAt(Vector3 world)
    {
        _mark = "mark at " + Vec(world);
        Resync();
    }

    // The game camera at the viewport's camera, relative to the anchor (the viewport's origin); fov = vertical degrees.
    public void Camera(Vector3 eye, Vector3 target, float fovDegrees)
    {
        _camera = "cam " + Vec(eye) + " " + Vec(target) + " " + GameParams.Num(fovDegrees * Math.PI / 180);
        if (Connected)
            _hub.Send(_camera, "cam");
    }

    public void CameraOff()
    {
        _camera = null;
        if (Connected)
            _hub.Send("camoff", "cam");
    }

    // A screenshot; series of up to MaxSeries, one every `everyFrames` frames.
    public const int MaxSeries = 100;

    public void Shot(string name)
    {
        _shots[name] = LinkHub.Now;
        _hub.Send("shot " + name);
    }

    public void Series(string prefix, int count, int everyFrames)
    {
        count = Math.Clamp(count, 1, MaxSeries);
        for (int i = 0; i < count; i++)
            _shots[SeriesName(prefix, i)] = LinkHub.Now;
        _hub.Send("shots " + prefix + " " + count + " " + Math.Max(1, everyFrames));
    }

    public static string SeriesName(string prefix, int index) => prefix + "_" + index.ToString("00", CultureInfo.InvariantCulture);

    // The engine's original values of every emitter of a part, compared with the file (checks GameParams' pairing).
    public void RequestOriginals(object key)
    {
        if (PartOf(key) is { InGame: true } p)
            _hub.Send("orig " + p.Id);
    }

    // One key of one emitter sent again as it is now (the field menu's "push to the running game now"); false when the
    // key is not a live parameter (the caller reloads instead).
    public bool PushKey(object key, PtcDocument now, string emitter, string ptcKey, long at)
    {
        if (PartOf(key) is not { InGame: true } part || now.Emitter(emitter) is not { } e || LiveDiff.Single(e, ptcKey) is not { } change)
            return false;
        int index = part.GameEmitters.ToList().IndexOf(emitter);
        if (index < 0)
            return false;
        return SendSet(part, index, change, at);
    }

    // Reloads the part as it is now, whatever changed (the field menu for keys that are not live parameters).
    public void Reload(object key)
    {
        if (PartOf(key) is { } part && part.Latest != null && Connected)
        {
            if (part.Loading)
            {
                part.Behind = true;
                return;
            }
            Spawn(part, LinkHub.Now);
        }
    }

    private void Apply(LivePart part)
    {
        var bytes = part.Latest!;
        var source = part.LatestSource!;
        PtcDocument now;
        try
        {
            now = PtcDocument.From(EnfDocument.Parse(bytes));
        }
        catch (Exception e) when (e is EnfSyntaxException or InvalidDataException)
        {
            part.Error = e.Message;
            PartChanged?.Invoke(part);
            return;
        }
        if (part.Live == null || LiveStage.Stamp(bytes, source) != part.Stamp)
        {
            Spawn(part, part.LatestAt);
            return;
        }
        var diff = LiveDiff.Compare(part.Live, now);
        if (diff.Same)
            return;
        var names = part.GameEmitters.ToList();
        if (diff.Reload || diff.Changes.Any(c => !names.Contains(c.Emitter) || _hub.Client?.ParamIndex(c.Param.Name) is null or < 0))
        {
            Spawn(part, part.LatestAt);
            return;
        }
        foreach (var c in diff.Changes)
            SendSet(part, names.IndexOf(c.Emitter), c, part.LatestAt);
        part.Live = now;
    }

    private bool SendSet(LivePart part, int index, ParamChange c, long at)
    {
        int param = _hub.Client?.ParamIndex(c.Param.Name) ?? -1;
        if (param < 0)
            return false;
        string head = "set " + part.Id + " " + index + " " + param;
        int seq = _hub.Send(head + " " + c.Arguments, head);
        _sets[seq] = new SentSet(at, c.Emitter + "." + c.Key, part.Id, c.Arguments);
        return true;
    }

    private void Spawn(LivePart part, long at)
    {
        if (_stage == null)
            return;
        StagedPart staged;
        try
        {
            staged = _stage.Stage(part.Name, part.Latest!, part.LatestSource!);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            part.Error = e.Message;
            Failed?.Invoke(part.Name, e.Message);
            PartChanged?.Invoke(part);
            return;
        }
        part.Error = staged.Missing.Count > 0 ? string.Join("; ", staged.Missing.Select(m => string.Join(" ", m.Args))) : null;
        part.Live = PtcDocument.From(EnfDocument.Parse(part.Latest!));
        part.Stamp = LiveStage.Stamp(part.Latest!, part.LatestSource!);
        part.GamePath = staged.GamePath;
        part.SpawnAt = at;
        part.Behind = false;
        part.SpawnSeq = _hub.Send("spawn " + part.Id + " " + staged.GamePath, "spawn " + part.Id);
        PartChanged?.Invoke(part);
    }

    private static void Forget(LivePart p)
    {
        p.Live = null;
        p.SpawnSeq = 0;
        p.GamePath = null;
        p.GameEmitters = Array.Empty<string>();
        p.Stamp = "";
    }

    private LivePart? ById(string text) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id) ? _parts.Values.FirstOrDefault(p => p.Id == id) : null;

    private static int Int(string text) => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : 0;

    private void OnMessage(string[] t)
    {
        long now = LinkHub.Now;
        switch (t[0])
        {
            // spawned <src> <seq> <count> <name...>
            case "spawned" when t.Length >= 4:
                if (ById(t[1]) is { } part && part.SpawnSeq == Int(t[2]))
                {
                    part.SpawnSeq = 0;
                    part.SpawnedAt = now;
                    part.GameEmitters = t.Skip(4).ToList();
                    Timed?.Invoke(new LinkTiming(TimingKind.Reload, LinkHub.Ms(part.SpawnAt, now), part.Name));
                    PartChanged?.Invoke(part);
                    if (part.Behind && Connected)
                    {
                        part.Behind = false;
                        Apply(part);
                    }
                }
                break;
            // nospawn <src> <seq> <path>
            case "nospawn" when t.Length >= 3:
                if (ById(t[1]) is { } failed && failed.SpawnSeq == Int(t[2]))
                {
                    Forget(failed);
                    failed.Error = t.Length > 3 ? t[3] : "";
                    Failed?.Invoke(failed.Name, "nospawn " + failed.Error);
                    PartChanged?.Invoke(failed);
                }
                break;
            // got <seq> <src> <emitter> <param> <value...>
            case "got" when t.Length >= 6:
                if (_sets.Remove(Int(t[1]), out var sent))
                {
                    string value = string.Join(" ", t.Skip(5));
                    Timed?.Invoke(new LinkTiming(TimingKind.Set, LinkHub.Ms(sent.At, now), sent.What + " = " + value, SameNumbers(sent.Arguments, value)));
                }
                break;
            // shot <name> <gameTime> <frameMs> <width> <height> <fov>
            case "shot" when t.Length >= 6:
                if (_hub.Client is { } c && ShotFolder(c) is { } folder)
                {
                    _shots.Remove(t[1], out long asked);
                    ShotTaken?.Invoke(new GameShot(t[1], Path.Combine(folder, t[1] + ".bmp"), Int(t[2]), Number(t[3]), Int(t[4]), Int(t[5]),
                        asked == 0 ? 0 : LinkHub.Ms(asked, now), t.Length > 6 ? Number(t[6]) : 0));
                }
                break;
            case "noshot" when t.Length >= 2:
                _shots.Remove(t[1]);
                Failed?.Invoke(t[1], string.Join(" ", t.Skip(2)));
                break;
            // orig <src> <emitter index> <one value per parameter in the game's enum order, vectors as x,y,z>; origend <src>
            case "orig" when t.Length >= 3:
                if (ById(t[1]) is { } op)
                    CompareOriginals(op, Int(t[2]), t.Skip(3).ToList());
                break;
            case "origend" when t.Length >= 2:
                if (_originals.Remove(Int(t[1]), out var rows))
                    Originals?.Invoke(Int(t[1]), rows);
                break;
            case "fail":
                Failed?.Invoke("", string.Join(" ", t.Skip(1)));
                break;
        }
    }

    private void CompareOriginals(LivePart part, int emitterIndex, List<string> values)
    {
        if (!_originals.TryGetValue(part.Id, out var rows))
            _originals[part.Id] = rows = new List<OriginalRow>();
        if (part.Live == null || emitterIndex >= part.GameEmitters.Count || part.Live.Emitter(part.GameEmitters[emitterIndex]) is not { } e)
            return;
        var names = _hub.Client?.Params ?? Array.Empty<string>();
        for (int i = 0; i < Math.Min(names.Count, values.Count); i++)
        {
            var p = GameParams.OfName(names[i]);
            if (p?.Key == null)
                continue;
            string game = values[i].Replace(',', ' ');
            var fileValues = e.Block.FindLine(p.Key) is { } line
                ? line.Values.SelectMany(v => v.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToList()
                : e.Schema.Find(Pfx.Core.Schema.SchemaScope.Emitor, p.Key)?.Default?.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList() ?? new List<string>();
            string file = GameParams.Arguments(p, fileValues) ?? string.Join(" ", fileValues);
            rows.Add(new OriginalRow(e.Name, p.Key, file, game, SameNumbers(file, game)));
        }
    }

    // "f 2.5" against "2.500000": the numbers after the kind letter, within float precision.
    public static bool SameNumbers(string sent, string got)
    {
        var a = sent.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        var b = got.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (a.Count > 0 && a[0] is "f" or "b" or "v")
            a.RemoveAt(0);
        if (a.Count != b.Count)
            return false;
        for (int i = 0; i < a.Count; i++)
        {
            if (!GameParams.TryNumber(a[i], out double x) || !GameParams.TryNumber(b[i], out double y))
                return false;
            if (Math.Abs(x - y) > 1e-4 * Math.Max(1, Math.Abs(x)))
                return false;
        }
        return true;
    }

    private static double Number(string text) => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0;

    private static string Vec(Vector3 v) => GameParams.Num(v.X) + " " + GameParams.Num(v.Y) + " " + GameParams.Num(v.Z);

    // The client's profile folder (its -profiles, relative to the game folder; DayZ's default without one) + the link
    // mod's shot folder.
    public string? ShotFolder(LinkClient c)
    {
        string profiles = c.Profiles;
        string root;
        if (profiles.Length == 0 || profiles == "-")
            root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DayZ");
        else if (Path.IsPathRooted(profiles))
            root = profiles;
        else if (GameFolder != null)
            root = Path.Combine(GameFolder, profiles);
        else
            return null;
        return Path.Combine(root, "PolyLab_PfxLink", "shots");
    }
}
