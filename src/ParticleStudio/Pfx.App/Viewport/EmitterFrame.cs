using System.Numerics;
using Pfx.App.Model;
using Pfx.Core.Editing;
using Pfx.Core.Model;
using Pfx.Render;
using Pfx.Sim;

namespace Pfx.App.Viewport;

// What the viewport tools need from the shell.
public interface IToolHost
{
    EffectDoc? ActiveDoc { get; }
    // The selected emitters of the active part (empty when the part itself is selected).
    IReadOnlyList<EmitterView> SelectedEmitters { get; }
    EffectPreview? PreviewOf(EffectDoc doc);
    PreviewSettings PreviewSettings { get; }
    ViewportHost Viewport { get; }
    void Toast(string text, string kind);
    // A new emitter selection after a tool added emitters (crossed planes, emitters along a path).
    void SelectEmitters(EffectDoc doc, IReadOnlyList<Pfx.Core.Text.EnfBlock> blocks);
}

// One emitter where the viewport shows it: the keys read from the document (so a typed value moves the handle at
// once, before the preview is rebuilt) and the source pose of the shown frame. The world transform is the one the
// simulator applies at birth: Offset turned by the source yaw plus the source position; shape and cone turned by the
// emitter Angles, then the source yaw (EffectPreview.EmitterOrigin / EmitterAxes).
public sealed record EmitterFrame(EmitterView View, EmitterParams Params, Vector3 SourcePosition, float SourceYaw)
{
    private const float Rad = MathF.PI / 180f;

    public Vector3 Offset => Params.Offset;
    public Vector3 Angles => Params.Angles;
    public Matrix4x4 SourceRotation => Orientation.Yaw(SourceYaw * Rad);
    public Vector3 Origin => Vector3.Transform(Offset, SourceRotation) + SourcePosition;
    public Matrix4x4 Axes => Orientation.YawPitchRoll(Angles) * SourceRotation;

    public Matrix4x4 AxesWith(Vector3 angles) => Orientation.YawPitchRoll(angles) * SourceRotation;

    // World direction to source space (Offset and emission directions are written in source space).
    public Vector3 ToSource(Vector3 world) => Vector3.TransformNormal(world, Orientation.Yaw(-SourceYaw * Rad));

    public static EmitterFrame Of(EmitterView view, IToolHost host, EffectDoc doc)
    {
        var p = EmitterParams.From(view);
        var (pos, yaw) = SourcePose(host, doc);
        return new EmitterFrame(view, p, pos, yaw);
    }

    // Source position and yaw in the shown frame (script and preview motion included), or the preview settings'
    // origin before a preview exists.
    public static (Vector3 Position, float Yaw) SourcePose(IToolHost host, EffectDoc doc)
    {
        if (host.PreviewOf(doc) is { Sims.Count: > 0 } preview)
        {
            var sim = preview.Sims[0];
            var (pos, yaw, _) = sim.Motion.At(sim.Time);
            return (pos, yaw);
        }
        return (host.PreviewSettings.Origin, host.PreviewSettings.Yaw);
    }
}

// Helpers shared by the tools: edits made one after another (an edit splices the token list at the indices it was
// made with, so each one is made after the one before it has run), all under one merge key = one undo step.
public static class ToolEdits
{
    public static void Steps(EffectDoc doc, string label, string mergeKey, IEnumerable<Func<IEdit?>> makers)
    {
        foreach (var make in makers)
            if (make() is { } edit)
                doc.Do(edit, label, mergeKey);
    }

    // A vector key as written (doubles: a float would come back as 0.1000000015), or zeros.
    public static double[] Vec(EmitterView v, string key) => v.Numbers(key) is { Length: >= 3 } n ? n[..3] : new double[3];

    // A vector key written only when it changes.
    public static Func<IEdit?> SetVec(EmitterView v, string key, double[] value) => () =>
        v.Has(key) && Vec(v, key).SequenceEqual(value) ? null : v.Set(key, value);

    public static Func<IEdit?> SetNum(EmitterView v, string key, double value) => () =>
        v.Number(key) == value && v.Has(key) ? null : v.Set(key, value);

    public static Func<IEdit?> SetWord(EmitterView v, string key, string word) => () =>
        v.Text(key) == word && v.Has(key) ? null : v.SetWord(key, word);

    // The template and n - 1 copies of it (name_2, name_3, ..), each copied from the previous copy so they land in
    // order right after the template; returns all n.
    public static List<EmitterView> CloneChain(EffectDoc doc, EmitterView template, int n, string label, string merge)
    {
        var result = new List<EmitterView> { template };
        var taken = doc.Ptc.Emitters.Select(v => v.Name).ToList();
        for (int i = 1; i < n; i++)
        {
            string name = Pfx.App.Core.Names.Unique(template.Name + "_" + (i + 1), taken);
            taken.Add(name);
            doc.Do(doc.Ptc.CloneEmitter(result[^1], name), label, merge);
            result.Add(doc.Ptc.Emitter(name)!);
        }
        return result;
    }

    // The step a value can be placed with by mouse: the largest power of ten not above what one pixel moves it
    // (finer digits cannot be aimed at, coarser ones would skip pixels).
    public static double Precision(double perPixel) =>
        perPixel > 0 && double.IsFinite(perPixel) ? Math.Pow(10, Math.Floor(Math.Log10(perPixel))) : 1e-3;

    // On a step; the second rounding drops the binary tail (0.30000000000000004) before the file text is written.
    public static double Round(double value, double step) => Math.Round(Math.Round(value / step) * step, 10);
}
