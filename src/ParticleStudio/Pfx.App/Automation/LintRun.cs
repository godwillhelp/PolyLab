using System.Text;
using Pfx.App.Lint;
using Pfx.App.Model;
using Pfx.Core.Model;
using Pfx.Render;

namespace Pfx.App.Automation;

// --lint <project | folder> --lint-log <file>: the check panel's Linter over many effects without a window. A project
// gives its parts with their entries (turn, near radius, stop), a folder every .ptc under it with none. Each part is
// linted alone, as when it is the only effect open, so effects that share a folder never count as neighbours; the sky
// is the viewport's default (noon). One tab-separated row per issue: part path (relative to the project or folder),
// emitter, rule, level, key, the message arguments joined by " | ", the emitter's material file.
public static class LintRun
{
    public static int Run(Pfx.App.Core.Launch launch, IReadOnlyList<string> roots)
    {
        string target = Path.GetFullPath(launch.Lint!);
        string log = Path.GetFullPath(launch.LintLog ?? throw new ArgumentException("--lint needs --lint-log"));
        List<(string Rel, string Disk, PartEntry? Entry)> parts;
        if (Directory.Exists(target))
        {
            parts = Directory.EnumerateFiles(target, "*.ptc", SearchOption.AllDirectories)
                .Order(StringComparer.OrdinalIgnoreCase)
                .Select(p => (Path.GetRelativePath(target, p).Replace(Path.DirectorySeparatorChar, '/'), p, (PartEntry?)null)).ToList();
        }
        else
        {
            var comp = Composition.Open(target);
            parts = comp.File.Parts.Select(p => (p.Path, comp.Resolve(p.Path), (PartEntry?)p)).ToList();
        }

        var files = new ExportSnapshot(Array.Empty<EffectDoc>(), roots);
        var sb = new StringBuilder("part\temitter\trule\tlevel\tkey\targs\tmaterial\r\n");
        foreach (var (rel, disk, entry) in parts)
        {
            var part = new LintPart(disk, rel, File.ReadAllBytes(disk), entry);
            var input = new LintInput(new[] { part }, files, null, null, Sky.Noon.Name);
            var report = Linter.Run(input);
            var read = Linter.Read(input)[0];
            foreach (var i in report.Issues)
            {
                string emitter = i.Emitter >= 0 ? report.EmitterNames[0][i.Emitter] : "";
                string material = i.Emitter >= 0 ? read.Emitters[i.Emitter].MaterialDisk ?? "" : "";
                sb.Append(rel).Append('\t').Append(emitter).Append('\t').Append(i.Rule.Id).Append('\t').Append(i.Level)
                  .Append('\t').Append(i.Key ?? "").Append('\t').Append(string.Join(" | ", i.Args)).Append('\t')
                  .Append(material).Append("\r\n");
            }
        }
        Directory.CreateDirectory(Path.GetDirectoryName(log)!);
        File.WriteAllText(log, sb.ToString(), new UTF8Encoding(false));
        return 0;
    }
}
