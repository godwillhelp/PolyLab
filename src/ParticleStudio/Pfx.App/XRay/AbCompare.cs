using System.Numerics;
using Pfx.Render;
using Pfx.Sim.XRay;
using Pfx.XRay;

namespace Pfx.App.XRay;

public enum AbMode { Side, Over, Off }

// The A/B view (spec 3.9): the original X-Ray group of an imported part, simulated by Pfx.Sim.XRay in the same viewport
// as its DayZ conversion, beside it (Side, the X-Ray copy Distance metres to the -X side) or on it (Over). It follows
// the scene clock in X-Ray frames of FrameMs: forward it steps on, backward (a seek or a loop wrap) it starts again from
// 0 and replays — deterministic per seed, so the same time shows the same particles.
public sealed class AbCompare
{
    // The engine's default update step; group frames run at it so each effect steps once per frame.
    public const int FrameMs = 33;
    public const ulong Seed = 1;

    private XrGroupSim? _group;
    private XrEffectSim? _single;
    private long _frames;
    private readonly Dictionary<XrEffect, string?> _textures = new();
    // Effects by name, parsed once (children start once per particle).
    private readonly Dictionary<string, XrEffect?> _defs = new();

    public ImportRecord? Record { get; private set; }
    public XrIndex? Index { get; private set; }
    public XrSource? Source { get; private set; }
    public AbMode Mode { get; set; } = AbMode.Side;
    public float Distance { get; set; } = 6;
    public int Live { get; private set; }
    public IReadOnlyCollection<string> Problems => _group?.Problems ?? (IReadOnlyCollection<string>)Array.Empty<string>();
    public bool Ready => _group != null || _single != null;
    public string? Missing { get; private set; }

    public Vector3 Shift => Mode == AbMode.Side ? new Vector3(-Distance, 0, 0) : Vector3.Zero;

    public void Clear()
    {
        Record = null;
        _group = null;
        _single = null;
        Missing = null;
        Live = 0;
    }

    // The subject: an import record and the index it resolves its effects in (the record's source and variant).
    public void Show(ImportRecord record, XrIndex index)
    {
        Record = record;
        Index = index;
        Source = index.Sources.Source(record.Tag);
        _textures.Clear();
        _defs.Clear();
        Restart();
    }

    private void Restart()
    {
        _frames = 0;
        _group = null;
        _single = null;
        Missing = null;
        if (Record == null || Index == null || Source == null)
        {
            Missing = Record?.Tag;
            return;
        }
        var rng = new XrRandom(Seed);
        string variant = Record.Variant;
        if (Index.Group(Source, variant, Record.Ref) is { } g)
        {
            _group = new XrGroupSim(g, Def, rng, D3.Zero);
            _group.Play();
        }
        else if (Def(Record.Ref) is { } e)
        {
            _single = new XrEffectSim(e, rng);
            _single.Play();
        }
        else
        {
            Missing = Record.Ref;
        }
    }

    private XrEffect? Def(string name)
    {
        if (!_defs.TryGetValue(name, out var e))
            _defs[name] = e = Index!.Effect(Source!, Record!.Variant, name);
        return e;
    }

    public IEnumerable<XrEffectSim> Effects() => _group?.Effects() ?? (_single != null ? new[] { _single } : Array.Empty<XrEffectSim>());

    // To the scene time t, then the quads for the camera.
    public void Compose(SpriteBatch batch, float t, Vector3 right, Vector3 up, Vector3 fwd)
    {
        Live = 0;
        if (Mode == AbMode.Off || Record == null)
            return;
        long target = (long)Math.Floor(t * 1000.0 / FrameMs);
        if (target < _frames)
            Restart();
        while (_frames < target && Ready)
        {
            _group?.Frame(FrameMs);
            _single?.Frame(FrameMs);
            _frames++;
        }
        foreach (var sim in Effects())
            Live += XrSprites.Add(batch, sim, Texture(sim.Def), Shift, right, up, fwd);
    }

    // The effect's texture as xray.py finds it (textures\<name>.dds in the source's order), else the substitute.
    private string? Texture(XrEffect e)
    {
        if (_textures.TryGetValue(e, out var file))
            return file;
        file = null;
        if (e.Texture.Length > 0 && Index != null && Source != null)
            file = Index.Find(Source, Record?.Variant, "textures/" + e.Texture + ".dds")?.File;
        _textures[e] = file;
        return file;
    }
}
