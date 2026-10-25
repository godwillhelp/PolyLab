using System.Globalization;

namespace Pfx.App.Core;

// Command line:
//   Particle_Studio [file.ptc ...]          open these effects as parts of one scene
//   --roots "P:\;D:\mod"                        data roots for this run (else the saved ones)
//   --lang ru|en|uk                             interface language for this run
//   --settings-dir <dir>                        settings and layouts folder (automation: never the user's own)
//   --checks <dir>                              shell checks: screenshots at 1280x720, 1366x768, 1920x1080 in RU,
//                                               EN and UK, clipping, tooltips, inspector coverage, round-trip; then exit
//   --selftest <file>                           device loss, resize and minimise inside the shell; then exit
//   --edit-bench <file> [--time <s>]            edit -> preview times on the opened effects; then exit
//   --copies <n> --spacing <m>                  stress-test copies for the opened effects
//   --texconv <exe>                             texconv.exe for this run (else the saved one)
//   --flipbook <dir>                            P7 acceptance: <dir>/frames → atlas → EDDS → first emitter, saved;
//                                               viewport frames at 0.5, 1.5, … s into <dir>; then exit
//   --path <dir>                                P8 acceptance: a drawn flight fitted to the first emitter, saved;
//                                               report, drawn / fitted points and a viewport frame into <dir>; then exit
//   --compose <dir> --compose-sounds <a;b>      P10 acceptance: a zharka built from empty parts into <dir> (project,
//                                               parts, sounds), its states photographed; then exit
//   --export <project> --export-log <file> --profile dayz|cg --to <folder> [--prefix P] [--data-prefix D]
//            [--slot <file>] [--type <word>]    the export panel's plan and write for that project; then exit
//   --zone <part.ptc> --zone-log <file>         an X-Ray zone import opened as its project (OpenZoneProject); then exit
//   --tornado <dir> [--tornado-donor <ptc>]     the tornado generator into <dir>, lint, budget, draw views; then exit
//   --lint <project | folder> --lint-log <file>  the check panel's lint over each part alone, one row per issue; then exit
//   --link-bench <dir> [--link-pid <pid>]       P11 acceptance: the opened part through the live link to a running
//                                               DayZDiag (edits, reloads, client memory, game frames); then exit
//   --read-look <dir> [--time <s>]              the curve editor's game read (CG8) and the viewport, photographed in the
//               [--first-frame <s>]             maximised window for every graph whose read is off the spline; with
//                                               --first-frame also the viewport at first frame 0 and <s> typed into
//                                               the camera options, and the check panel (CG10); then exit
public sealed class Launch
{
    public List<string> Files { get; } = new();
    public string? Roots { get; private set; }
    public string? Lang { get; private set; }
    public string? SettingsDir { get; private set; }
    public string? Checks { get; private set; }
    public string? SelfTest { get; private set; }
    public string? EditBench { get; private set; }
    public float Time { get; private set; } = 10;
    public int Copies { get; private set; } = 1;
    public float Spacing { get; private set; } = 5;
    public string? Texconv { get; private set; }
    public string? Flipbook { get; private set; }
    public string? PathRun { get; private set; }
    // X-Ray import runs: --xray <dir> = the P9 acceptance flow; the inputs (folders, ';'), xray.py and its expected
    // list (tools/xray/expected.py) are test inputs given here, never defaults.
    public string? XrayRun { get; private set; }
    public string? XrayInputs { get; private set; }
    public string? XrayScript { get; private set; }
    public string? XrayExpected { get; private set; }
    public string? Compose { get; private set; }
    public string? Proof { get; private set; }
    // --proof-set p12b (default) | p12c | p12d | cg; --proof-sound <ogg>: the sound file the p12c set plays (a test input).
    public string? ProofSet { get; private set; }
    public string? ProofSound { get; private set; }
    public string? ComposeSounds { get; private set; }
    public string? Export { get; private set; }
    public string? ExportLog { get; private set; }
    public string? ExportProfile { get; private set; }
    public string? ExportTo { get; private set; }
    public string? ExportPrefix { get; private set; }
    public string? ExportDataPrefix { get; private set; }
    public string? ExportSlot { get; private set; }
    public string? ExportType { get; private set; }
    public string? Zone { get; private set; }
    public string? ZoneLog { get; private set; }
    public string? LinkBench { get; private set; }
    public string? Lint { get; private set; }
    public string? LintLog { get; private set; }
    public string? Tornado { get; private set; }
    public string? TornadoDonor { get; private set; }
    public int LinkPid { get; private set; }
    public string? ReadLook { get; private set; }
    public float? FirstFrame { get; private set; }

    public bool Automated => Checks != null || SelfTest != null || EditBench != null || Flipbook != null || PathRun != null || XrayRun != null
                             || Compose != null || Export != null || Zone != null || LinkBench != null || Tornado != null || Proof != null
                             || ReadLook != null;

    public static Launch Parse(string[] args)
    {
        var l = new Launch();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException("Missing value after " + a);
            switch (a)
            {
                case "--roots": l.Roots = Next(); break;
                case "--lang": l.Lang = Next(); break;
                case "--settings-dir": l.SettingsDir = Next(); break;
                case "--checks": l.Checks = Next(); break;
                case "--selftest": l.SelfTest = Next(); break;
                case "--edit-bench": l.EditBench = Next(); break;
                case "--time": l.Time = float.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--copies": l.Copies = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--spacing": l.Spacing = float.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--texconv": l.Texconv = Next(); break;
                case "--flipbook": l.Flipbook = Next(); break;
                case "--path": l.PathRun = Next(); break;
                case "--xray": l.XrayRun = Next(); break;
                case "--xray-inputs": l.XrayInputs = Next(); break;
                case "--xray-script": l.XrayScript = Next(); break;
                case "--xray-expected": l.XrayExpected = Next(); break;
                case "--compose": l.Compose = Next(); break;
                case "--compose-sounds": l.ComposeSounds = Next(); break;
                case "--export": l.Export = Next(); break;
                case "--export-log": l.ExportLog = Next(); break;
                case "--profile": l.ExportProfile = Next(); break;
                case "--to": l.ExportTo = Next(); break;
                case "--prefix": l.ExportPrefix = Next(); break;
                case "--data-prefix": l.ExportDataPrefix = Next(); break;
                case "--slot": l.ExportSlot = Next(); break;
                case "--type": l.ExportType = Next(); break;
                case "--zone": l.Zone = Next(); break;
                case "--zone-log": l.ZoneLog = Next(); break;
                case "--link-bench": l.LinkBench = Next(); break;
                case "--lint": l.Lint = Next(); break;
                case "--lint-log": l.LintLog = Next(); break;
                case "--tornado": l.Tornado = Next(); break;
                case "--proof": l.Proof = Next(); break;
                case "--proof-set": l.ProofSet = Next(); break;
                case "--proof-sound": l.ProofSound = Next(); break;
                case "--tornado-donor": l.TornadoDonor = Next(); break;
                case "--link-pid": l.LinkPid = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--read-look": l.ReadLook = Next(); break;
                case "--first-frame": l.FirstFrame = float.Parse(Next(), CultureInfo.InvariantCulture); break;
                default:
                    if (a.StartsWith("--", StringComparison.Ordinal))
                        throw new ArgumentException("Unknown option " + a);
                    l.Files.Add(a);
                    break;
            }
        }
        return l;
    }
}
