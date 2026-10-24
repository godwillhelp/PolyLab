using System.Text;
using Pfx.App.Views;
using Pfx.App.XRay;

namespace Pfx.App.Automation;

// --zone <part.ptc> --zone-log <file>: a part imported from an X-Ray zone (P9) opened as its zone's project through
// the shell's own OpenZoneProject (the path a zone import takes when it finishes); logs the states, sounds and lights
// next to the record's values; then exits.
public sealed class ZoneRun
{
    private readonly MainWindow _w;
    private readonly Pfx.App.Core.Launch _l;
    private readonly StringBuilder _log = new();

    public ZoneRun(MainWindow window, Pfx.App.Core.Launch launch)
    {
        _w = window;
        _l = launch;
    }

    public void Start() => Run();

    private void Log(string line)
    {
        _log.AppendLine(line);
        File.WriteAllText(_l.ZoneLog!, _log.ToString());
    }

    private void Run()
    {
        try
        {
            string part = Path.GetFullPath(_l.Zone!);
            var record = ImportRecord.ForPart(part) ?? throw new InvalidOperationException("no import record for " + part);
            string stage = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(part)!)!)!;
            _w.OpenZoneProject(record, stage);
            var p = _w.Project ?? throw new InvalidOperationException("no project");
            Log($"zone {record.Zone}: project {p.Path}");
            foreach (var s in p.File.States)
                Log($"  state {s.Name} {s.Duration} s over idle {s.OverIdle}: {string.Join(", ", s.Items.Select(i => i.Ref + (i.Delay > 0 ? "@" + i.Delay : "")))}");
            foreach (var s in p.File.Sounds)
            {
                var r = record.Sounds.FirstOrDefault(x => Pfx.Core.Model.ProjectNames.Id(x.Role) == s.Id);
                Log($"  sound {s.Id}: {s.File} range {s.Range} volume {s.Volume} loop {s.Loop}; record range {r?.Range} volume {r?.Volume}");
            }
            foreach (var l in p.File.Lights)
                Log($"  light {l.Id}: colour {string.Join(" ", l.Color)} radius {l.Radius} offset y {l.Offset[1]} life {l.Life} fade {l.FadeOut}" +
                    (l.Anim != null ? $" anim {l.Anim.Name} {l.Anim.Frames.Count} frames at {l.Anim.Fps}" : ""));
            foreach (var l in record.Lights)
                Log($"  record {l.Role}: {string.Join(", ", l.Keys.Select(k => k.Key + "=" + k.Value))}");
            Log($"  parts {p.File.Parts.Count}, hazes {p.File.Hazes.Count}, open parts {_w.Docs.Count}");
            Log("RESULT: done");
        }
        catch (Exception ex)
        {
            Log("FAILED: " + ex);
        }
        _w.Close();
    }
}
