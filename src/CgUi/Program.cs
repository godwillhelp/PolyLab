using System.Text.Encodings.Web;
using System.Text.Json;
using PolyLab.CgUi;

const string usage = """
    cgui compile <readback.json> --out <dir> --screen <nodeId>=<Name> [--screen ...]
                 [--sheet <nodeId>=<Name>] [--live $a,$b] [--prefix CG_UIData_]
      readback.json = REST GET /v1/files/:key/nodes?ids=...&geometry=paths&plugin_data=shared
    """;

if (args.Length < 2 || args[0] != "compile")
{
    Console.Error.WriteLine(usage);
    return 2;
}

string input = args[1], outDir = "", prefix = "CG_UIData_";
var screens = new List<(string Id, string Name)>();
var sheets = new List<(string Id, string Name)>();
var live = new List<string>();
for (int i = 2; i < args.Length; i++)
{
    string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
    (string, string) Pair(string v) => v.Split('=', 2) is [var id, var name] ? (id, name) : throw new ArgumentException($"expected <nodeId>=<Name>: {v}");
    switch (args[i])
    {
        case "--out": outDir = Next(); break;
        case "--screen": screens.Add(Pair(Next())); break;
        case "--sheet": sheets.Add(Pair(Next())); break;
        case "--live": live.AddRange(Next().Split(',', StringSplitOptions.RemoveEmptyEntries)); break;
        case "--prefix": prefix = Next(); break;
        default:
            Console.Error.WriteLine($"unknown argument {args[i]}\n{usage}");
            return 2;
    }
}
if (outDir == "" || screens.Count == 0)
{
    Console.Error.WriteLine(usage);
    return 2;
}

Directory.CreateDirectory(outDir);
using var doc = JsonDocument.Parse(File.ReadAllBytes(input));
var jsonOut = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
var items = new List<GalleryItem>();
var source = new System.Text.StringBuilder();
bool failed = false;

foreach (var (id, name, sheet) in screens.Select(s => (s.Id, s.Name, false)).Concat(sheets.Select(s => (s.Id, s.Name, true))))
{
    var reader = new FigmaReader(doc.RootElement, sheet ? [] : live, images: !sheet);
    var ir = reader.Read(id, name);
    var enc = IntStream.Encode(ir);
    var decoded = IntStream.Decode(enc.Ints, enc.Strs);
    if (!IntStream.Encode(decoded).Ints.SequenceEqual(enc.Ints))
        throw new InvalidOperationException($"{name}: the int stream does not round-trip");
    File.WriteAllText(Path.Combine(outDir, name + ".ir.json"), JsonSerializer.Serialize(ir, jsonOut));
    var shots = Gallery.Heights.Select(h => Gallery.Shoot(ir, decoded, h)).ToArray();
    if (shots[0].Raster.Lines.Count > Gallery.LineBudget)
        ir.Diags.Add(new Diag(false, id, name,
            $"{shots[0].Raster.Lines.Count} canvas lines at 1080p exceed the budget of {Gallery.LineBudget} (CG_UI_PIPELINE.md §3.1)"));
    items.Add(new GalleryItem(ir, enc, shots, sheet));
    int errors = ir.Diags.Count(d => d.Error);
    failed |= errors > 0;
    if (!sheet)
        source.Append(EnforceOut.ClassSource(prefix + name, enc));
    Console.WriteLine($"{name} ({id}): {enc.Ints.Length} ints, {enc.Strs.Length} strings, lines " +
                      string.Join(" / ", shots.Select(s => $"{s.Raster.Lines.Count} @{s.ScreenH}p")) +
                      $", {errors} errors, {ir.Diags.Count - errors} warnings");
    foreach (var d in ir.Diags.Where(d => d.Error))
        Console.WriteLine($"  error {d.NodeId} \"{d.NodeName}\": {d.Message}");
}

File.WriteAllText(Path.Combine(outDir, "gallery.html"), Gallery.Html(items, Path.GetFullPath(input)));
string cPath = Path.Combine(outDir, prefix.TrimEnd('_') + ".c");
if (failed)
{
    Console.WriteLine($"errors: {cPath} not written");
    return 1;
}
File.WriteAllText(cPath, source.ToString());
Console.WriteLine($"wrote {cPath}, gallery.html and the .ir.json files to {Path.GetFullPath(outDir)}");
return 0;
