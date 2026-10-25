using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Text;

namespace PolyLab.PBOGPacker.Core;

// pboProject-style reference check of a staged mod: every file a config, rvmat, layout, script or model
// names must exist in the mod itself, in another top folder of the work drive (P:\) or in the game data.
// A path is a string with a folder separator and a known file extension, as Mikero's tools count it;
// samples[] (sounds without extension) and files[] (script folders unless the entry has an extension)
// are checked by their array name.
// Texts give the line, MLOD models the LOD. Binarized models (ODOL) and rapified rvmats only keep their
// strings: BI's own ODOLs name files the game never shipped and every ODOL carries its materials inside,
// so only a missing texture of the mod itself is an error there; the rest is counted.
public static class RefCheck
{
    public sealed record Finding(string File, int Line, string? Lod, string Ref)
    {
        public override string ToString() =>
            File + (Line > 0 ? ":" + Line : Lod != null ? " (" + Lod + ")" : "") + "  " + Ref;
    }

    public sealed record Report(IReadOnlyList<Finding> Missing, IReadOnlyList<(string Model, int Count)> OdolOther,
        IReadOnlyList<string> Unchecked, int Refs, int Files);

    private enum Kind { File, Sound, Folder }

    private static readonly HashSet<string> Exts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".p3d", ".paa", ".pac", ".tga", ".png", ".jpg", ".jpeg", ".edds", ".dds", ".rvmat", ".emat", ".bisurf", ".ptc",
        ".rtm", ".anm", ".asi", ".agr", ".ast", ".aw", ".ogg", ".wss", ".wav", ".ogv", ".layout", ".imageset", ".styles",
        ".fnt", ".xml", ".json", ".csv", ".txt", ".c", ".h", ".hpp", ".sqf", ".sqs",
    };
    private static readonly string[] SoundExts = { ".ogg", ".wss", ".wav" };
    // The engine loads the .paa of a .tga / .png / .pac reference (pboProject converts them to it).
    private static readonly HashSet<string> PaaSources = new(StringComparer.OrdinalIgnoreCase) { ".tga", ".png", ".pac" };
    private static readonly char[] NotInPath = { ':', '%', '*', '?', '<', '>', '|', '"' };

    private static readonly (double Res, string Name)[] SpecialLods =
    {
        (1e13, "Geometry"), (2e13, "Geometry Buoyancy"), (4e13, "Geometry PhysX"), (1e15, "Memory"), (2e15, "LandContact"),
        (3e15, "Roadway"), (4e15, "Paths"), (5e15, "HitPoints"), (6e15, "View Geometry"), (7e15, "Fire Geometry"),
    };

    public static Report Scan(string stage, string prefix, string? work, string? game)
    {
        var missing = new List<Finding>();
        var other = new List<(string, int)>();
        var unreachable = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var cache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        int refs = 0, files = 0;

        // null: the folder the path points into is not available, so the path cannot be checked.
        bool? Exists(string path, Kind kind, out bool own)
        {
            own = path.StartsWith(prefix + "\\", StringComparison.OrdinalIgnoreCase) || path.Equals(prefix, StringComparison.OrdinalIgnoreCase);
            var top = path[..path.IndexOf('\\')];
            string? full;
            if (own) full = Path.Combine(stage, path[prefix.Length..].TrimStart('\\'));
            else if (top.Equals("dz", StringComparison.OrdinalIgnoreCase)) full = game == null ? null : Path.Combine(game, path[3..]);
            else full = work == null ? null : Path.Combine(work, path);
            // A top folder that is not on the work drive at all (another mod not extracted there, engine
            // resources such as Enfusion\Gui) cannot be checked; a missing file in one that is there is missing.
            if (full != null && !own && work != null && !top.Equals("dz", StringComparison.OrdinalIgnoreCase))
            {
                var dirKey = "top|" + top;
                if (!cache.TryGetValue(dirKey, out var there)) cache[dirKey] = there = Directory.Exists(Path.Combine(work, top));
                if (!there) full = null;
            }
            if (full == null)
            {
                unreachable.Add(top);
                return null;
            }
            var key = kind + "|" + full;
            if (!cache.TryGetValue(key, out var ok))
            {
                ok = kind switch
                {
                    Kind.Folder => Directory.Exists(full),
                    Kind.Sound => File.Exists(full) || SoundExts.Any(e => File.Exists(full + e)),
                    _ => File.Exists(full) || (PaaSources.Contains(Path.GetExtension(full)) && File.Exists(Path.ChangeExtension(full, ".paa"))),
                };
                cache[key] = ok;
            }
            refs++;
            return ok;
        }

        void Check(string rel, int line, string? lod, string raw, Kind kind)
        {
            if (Normalize(raw, kind) is { } path && Exists(path, kind, out _) == false)
                missing.Add(new Finding(rel, line, lod, raw));
        }

        foreach (var file in Directory.GetFiles(stage, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(stage, file);
            var name = Path.GetFileName(file);
            var ext = Path.GetExtension(file).ToLowerInvariant();
            var config = ext == ".hpp" || name.Equals("config.cpp", StringComparison.OrdinalIgnoreCase);
            if (!config && ext is not (".rvmat" or ".layout" or ".c" or ".p3d")) continue;
            files++;
            var data = File.ReadAllBytes(file);
            var magic = data.Length >= 4 ? Encoding.ASCII.GetString(data, 0, 4) : "";

            if (ext == ".p3d" && magic == "MLOD")
            {
                List<(string? Lod, string Ref)> faces;
                try { faces = Mlod(data); }
                catch (Exception e) when (e is InvalidDataException or ArgumentException)
                {
                    faces = Asciiz(data).Distinct(StringComparer.OrdinalIgnoreCase).Select(s => ((string?)null, s)).ToList();
                }
                foreach (var g in faces.GroupBy(f => f.Ref, StringComparer.OrdinalIgnoreCase))
                {
                    var lods = string.Join(", ", g.Select(f => f.Lod).OfType<string>());
                    Check(rel, 0, lods.Length > 0 ? lods : null, g.Key, Kind.File);
                }
            }
            else if (ext == ".p3d" || magic == "\0raP")
            {
                var foreign = 0;
                foreach (var r in Asciiz(data).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (Normalize(r, Kind.File) is not { } path || Exists(path, Kind.File, out var own) != false) continue;
                    // An ODOL carries its materials inside, so only a missing texture or other file of the mod counts.
                    if (ext != ".p3d" || own && !path.EndsWith(".rvmat", StringComparison.OrdinalIgnoreCase)) missing.Add(new Finding(rel, 0, null, r));
                    else foreign++;
                }
                if (foreign > 0) other.Add((rel, foreign));
            }
            else
            {
                var text = Encoding.UTF8.GetString(data);
                foreach (var (s, line, array) in Strings(text, ext == ".c"))
                {
                    // files[] lists script folders in the script modules, imagesets and styles in imageSets / widgetStyles.
                    var kind = !config ? Kind.File
                        : string.Equals(array, "samples", StringComparison.OrdinalIgnoreCase) ? Kind.Sound
                        : string.Equals(array, "files", StringComparison.OrdinalIgnoreCase) && !Exts.Contains(Path.GetExtension(s.Trim())) ? Kind.Folder
                        : Kind.File;
                    Check(rel, line, null, s, kind);
                }
            }
        }
        return new Report(missing, other, unreachable.ToList(), refs, files);
    }

    // A game path as the engine resolves it: from the drive root, either slash, case ignored. Null when the
    // string is no file path: no folder part, a procedural texture (#...), a stringtable key ($...), a
    // drive, URL or $profile: location, a format placeholder (%1), or no known extension.
    private static string? Normalize(string raw, Kind kind)
    {
        var s = raw.Trim();
        if (s.StartsWith('{') && s.IndexOf('}') is > 1 and <= 17 and var close && s[1..close].All(Uri.IsHexDigit)) s = s[(close + 1)..];
        if (s.Length == 0 || s[0] is '#' or '$' || s.IndexOfAny(NotInPath) >= 0) return null;
        s = s.Replace('/', '\\');
        while (s.Contains("\\\\")) s = s.Replace("\\\\", "\\");
        s = s.Trim('\\');
        if (s.IndexOf('\\') <= 0) return null;
        return kind != Kind.File || Exts.Contains(Path.GetExtension(s)) ? s : null;
    }

    // Strings of a config, rvmat, layout or script with their line and the array they sit in (name[] = {...},
    // nested braces included). Comments and preprocessor lines are skipped: an #include path is relative to
    // its file and CfgConvert checks it. Scripts escape with a backslash, configs double the quote.
    private static IEnumerable<(string Text, int Line, string? Array)> Strings(string t, bool cEscapes)
    {
        var line = 1;
        var lineStart = true;
        string? ident = null, pending = null;
        var arrays = new Stack<string?>();
        var sb = new StringBuilder();
        for (var i = 0; i < t.Length;)
        {
            var ch = t[i];
            var next = i + 1 < t.Length ? t[i + 1] : '\0';
            if (ch == '\n') { line++; lineStart = true; i++; continue; }
            if (char.IsWhiteSpace(ch) || ch == '﻿') { i++; continue; }
            if (lineStart && ch == '#')
            {
                for (; i < t.Length && t[i] != '\n'; i++)
                    if (t[i] == '\\' && (i + 1 < t.Length && t[i + 1] == '\n' || i + 2 < t.Length && t[i + 1] == '\r' && t[i + 2] == '\n'))
                    {
                        i = t.IndexOf('\n', i);
                        line++;
                    }
                continue;
            }
            lineStart = false;
            if (ch == '/' && next == '/')
            {
                while (i < t.Length && t[i] != '\n') i++;
                continue;
            }
            if (ch == '/' && next == '*')
            {
                var end = t.IndexOf("*/", i + 2, StringComparison.Ordinal);
                end = end < 0 ? t.Length : end + 2;
                for (; i < end; i++) if (t[i] == '\n') line++;
                continue;
            }
            if (ch == '"')
            {
                sb.Clear();
                var j = i + 1;
                for (; j < t.Length && t[j] != '\n'; j++)
                {
                    if (cEscapes && t[j] == '\\' && j + 1 < t.Length) { sb.Append(t[++j]); continue; }
                    if (t[j] == '"')
                    {
                        if (!cEscapes && j + 1 < t.Length && t[j + 1] == '"') { sb.Append('"'); j++; continue; }
                        break;
                    }
                    sb.Append(t[j]);
                }
                yield return (sb.ToString(), line, arrays.Count > 0 ? arrays.Peek() : null);
                i = j < t.Length && t[j] == '"' ? j + 1 : j;
                continue;
            }
            if (char.IsLetterOrDigit(ch) || ch == '_')
            {
                var j = i;
                while (j < t.Length && (char.IsLetterOrDigit(t[j]) || t[j] == '_')) j++;
                ident = t[i..j];
                i = j;
                continue;
            }
            if (ch == '[' && next == ']') { pending = ident; i += 2; continue; }
            if (ch == '{') { arrays.Push(pending ?? (arrays.Count > 0 ? arrays.Peek() : null)); pending = null; }
            else if (ch == '}' && arrays.Count > 0) arrays.Pop();
            else if (ch == ';') pending = null;
            i++;
        }
    }

    // Face textures and materials of every LOD of an MLOD p3d: per LOD "P3DM", two versions, the point,
    // normal and face counts and flags (28 bytes), points 16 bytes and normals 12 bytes each, then per face
    // 72 bytes (vertex count, four vertex slots, flags) followed by the texture and the material, then TAGG
    // entries up to #EndOfFile# and the LOD resolution.
    private static List<(string? Lod, string Ref)> Mlod(byte[] d)
    {
        var all = new List<(string?, string)>();
        var nlods = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(8));
        var pos = 12;
        for (var l = 0; l < nlods; l++)
        {
            if (Encoding.ASCII.GetString(d, pos, 4) != "P3DM") throw new InvalidDataException("P3DM");
            var points = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(pos + 12));
            var normals = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(pos + 16));
            var faces = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(pos + 20));
            pos += 28 + points * 16 + normals * 12;
            var refs = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var f = 0; f < faces; f++)
            {
                pos += 72;
                for (var k = 0; k < 2; k++)
                {
                    var s = Z(d, ref pos);
                    if (s.Length > 0) refs.Add(s);
                }
            }
            if (Encoding.ASCII.GetString(d, pos, 4) != "TAGG") throw new InvalidDataException("TAGG");
            pos += 4;
            while (true)
            {
                pos++;
                var tag = Z(d, ref pos);
                pos += 4 + BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(pos));
                if (tag == "#EndOfFile#") break;
            }
            var lod = LodName(BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(pos)));
            pos += 4;
            all.AddRange(refs.Select(r => ((string?)lod, r)));
        }
        return all;
    }

    private static string Z(byte[] d, ref int pos)
    {
        var end = Array.IndexOf(d, (byte)0, pos);
        if (end < 0) throw new InvalidDataException("asciiz");
        var s = Encoding.Latin1.GetString(d, pos, end - pos);
        pos = end + 1;
        return s;
    }

    // Object Builder's names: visual LODs by their resolution, shadow volumes from 10000, the rest by role.
    private static string LodName(float r)
    {
        if (r < 1e4) return "LOD " + r.ToString("0.000", CultureInfo.InvariantCulture);
        if (r < 2e4) return "ShadowVolume " + (r - 1e4).ToString("0.000", CultureInfo.InvariantCulture);
        foreach (var (res, name) in SpecialLods)
            if (Math.Abs(r - res) <= res * 1e-4) return name;
        return "LOD " + r.ToString("0.###E+0", CultureInfo.InvariantCulture);
    }

    // Printable strings between two NULs: how ODOL models and rapified files store names, whatever their
    // version. The leading NUL keeps binary bytes in front of a name from gluing onto it.
    private static IEnumerable<string> Asciiz(byte[] d)
    {
        var start = 0;
        for (var j = 0; j < d.Length; j++)
        {
            var b = d[j];
            if (b == 0)
            {
                if (start >= 0 && j - start >= 4) yield return Encoding.Latin1.GetString(d, start, j - start);
                start = j + 1;
            }
            else if (b < 0x20 || b > 0x7E) start = -1;
        }
    }
}
