using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace PolyLab.PBOGPacker.Core;

public sealed record WorkshopMod(string Name, string Path, string? Target, string? Id, int Keys);

// One -mod entry: Text is what the start script says, Full the folder it points at, WorkshopPath the
// workshop item it resolves to (through the !Workshop junction), if any.
public sealed record ModEntry(string Text, string Full, string Name, bool Exists, string? WorkshopPath);

// A queued mod handed back for saving: Text as it should appear, Alt a plain spelling of the same folder
// (the workshop content\221100\<id> junction target: ASCII, no '!', no '%').
public sealed record ModPick(string Text, string? Alt);

// Current is the -mod list, CurrentServer the -serverMod one; Local are the @folders of the server itself.
public sealed record ModsSetup(string Bat, string WorkshopDir, IReadOnlyList<WorkshopMod> Available,
    IReadOnlyList<ModEntry> Current, string? Error, string? Warning = null, bool Delayed = false, bool ScriptBusy = false,
    IReadOnlyList<ModEntry>? CurrentServer = null, IReadOnlyList<WorkshopMod>? Local = null);

// What the GRAFT host reads from the launch line: both mod lists, the switches it cannot see because
// the quote stands in front of the key, and -profiles= (where its journal goes).
public sealed record LaunchArgs(string Bat, IReadOnlyList<string> Mods, IReadOnlyList<string> ServerMods,
    IReadOnlyList<string> Hidden, string? Profiles, string? Error);

public sealed record ModsSaved(string Bat, string? Backup, int Mods, int KeysCopied, IReadOnlyList<string> NoKeys, int ServerMods = 0);

// Workshop listing, the -mod switch of a server start script, and the mods' .bikey files.
public static class ServerMods
{
    static ServerMods() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private const int CmdLineLimit = 8000; // cmd.exe cuts a line at 8191 characters

    // ── workshop ───────────────────────────────────────────────────────────────
    // The DayZ launcher keeps <library>\steamapps\common\DayZ\!Workshop\@Name junctions to the
    // downloaded items; every Steam library is checked, the fullest one wins.
    public static string? DefaultWorkshopDir()
    {
        var found = Libraries()
            .Select(l => System.IO.Path.Combine(l, "steamapps", "common", "DayZ", "!Workshop"))
            .Where(Directory.Exists)
            .OrderByDescending(d => { try { return Directory.GetDirectories(d, "@*").Length; } catch (Exception) { return 0; } })
            .FirstOrDefault();
        return found == null ? null : Canonical(found);
    }

    // Steam stores its path lower-cased; paths written into a start script should read like the rest of it.
    private static string Canonical(string path)
    {
        try
        {
            var full = System.IO.Path.GetFullPath(path);
            var root = System.IO.Path.GetPathRoot(full)!;
            var cur = root.ToUpperInvariant();
            foreach (var seg in full[root.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
                cur = Directory.EnumerateFileSystemEntries(cur, seg).FirstOrDefault() ?? System.IO.Path.Combine(cur, seg);
            return cur;
        }
        catch (Exception) { return path; }
    }

    private static IEnumerable<string> Libraries()
    {
        var steam = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string
                    ?? @"C:\Program Files (x86)\Steam";
        steam = steam.Replace('/', '\\');
        yield return steam;
        var vdf = System.IO.Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf)) yield break;
        string text;
        try { text = File.ReadAllText(vdf); } catch (Exception) { yield break; }
        foreach (Match m in Regex.Matches(text, "\"path\"\\s*\"([^\"]+)\""))
            yield return m.Groups[1].Value.Replace(@"\\", @"\");
    }

    // A renamed item leaves its old @Name junction behind: both point at the same content folder, so
    // one entry per target is kept, preferably the one named like its meta.cpp.
    public static IReadOnlyList<WorkshopMod> ListWorkshop(string dir, List<string>? issues = null)
    {
        var list = new List<(WorkshopMod Mod, bool Named)>();
        if (!Directory.Exists(dir)) return Array.Empty<WorkshopMod>();
        foreach (var d in Directory.GetDirectories(dir))
        {
            var folder = System.IO.Path.GetFileName(d);
            if (folder.StartsWith('!')) continue;
            var target = LinkTarget(d);
            var read = Readable(d, target, issues);
            var meta = ReadMeta(read);
            var name = folder.StartsWith('@') ? folder[1..] : meta.Name ?? folder;
            var named = meta.Name != null && string.Equals(name, meta.Name, StringComparison.OrdinalIgnoreCase);
            list.Add((new WorkshopMod(name, d, target, meta.Id, KeyFiles(read).Count), named));
        }
        return list.GroupBy(x => (x.Mod.Target ?? x.Mod.Path).ToUpperInvariant())
            .Select(g => g.OrderByDescending(x => x.Named).ThenBy(x => x.Mod.Name.Length).First().Mod)
            .OrderBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    // The @folders of the server itself (server-side mods usually live there).
    public static IReadOnlyList<WorkshopMod> ListLocal(string serverDir, List<string>? issues = null)
    {
        var list = new List<WorkshopMod>();
        try
        {
            if (!Directory.Exists(serverDir)) return list;
            foreach (var d in Directory.GetDirectories(serverDir, "@*"))
            {
                var target = LinkTarget(d);
                var read = Readable(d, target, issues);
                list.Add(new WorkshopMod(System.IO.Path.GetFileName(d)[1..], d, target, ReadMeta(read).Id, KeyFiles(read).Count));
            }
        }
        catch (Exception e) { issues?.Add(serverDir + ": " + e.Message); }
        return list.OrderBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    // A junction that cannot be walked through is read at its target instead; when neither opens, the
    // reason is kept so the dialog can say why the keys and ids are missing.
    private static string Readable(string dir, string? target, List<string>? issues)
    {
        var err = Probe(dir);
        if (err == null) return dir;
        if (target != null && Probe(target) == null) return target;
        issues?.Add(System.IO.Path.GetFileName(dir) + ": " + err);
        return dir;
    }

    private static string? Probe(string dir)
    {
        try
        {
            using var e = Directory.EnumerateFileSystemEntries(dir).GetEnumerator();
            e.MoveNext();
            return null;
        }
        catch (Exception e) { return e.Message; }
    }

    private static string? LinkTarget(string dir)
    {
        try
        {
            var t = new DirectoryInfo(dir).LinkTarget;
            if (t == null) return null;
            return System.IO.Path.IsPathRooted(t) ? t : System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(dir)!, t));
        }
        catch (Exception) { return null; }
    }

    private static (string? Name, string? Id) ReadMeta(string dir)
    {
        string? name = null, id = null;
        foreach (var f in new[] { "meta.cpp", "mod.cpp" })
        {
            var p = System.IO.Path.Combine(dir, f);
            if (!File.Exists(p)) continue;
            try
            {
                var t = File.ReadAllText(p);
                name ??= Regex.Match(t, "\\bname\\s*=\\s*\"([^\"]+)\"").Groups[1].Value is { Length: > 0 } n ? n : null;
                id ??= Regex.Match(t, "\\bpublishedid\\s*=\\s*(\\d+)").Groups[1].Value is { Length: > 0 } i ? i : null;
            }
            catch (Exception) { }
        }
        return (name, id);
    }

    // Keys ship in keys\ (sometimes key\ or Keys\) next to addons\.
    public static IReadOnlyList<string> KeyFiles(string modDir)
    {
        var list = new List<string>();
        try
        {
            if (!Directory.Exists(modDir)) return list;
            foreach (var d in Directory.GetDirectories(modDir))
            {
                var n = System.IO.Path.GetFileName(d);
                if (n.Equals("keys", StringComparison.OrdinalIgnoreCase) || n.Equals("key", StringComparison.OrdinalIgnoreCase))
                    list.AddRange(Directory.GetFiles(d, "*.bikey"));
            }
        }
        catch (Exception) { }
        return list;
    }

    // ── start script ───────────────────────────────────────────────────────────
    private sealed class Script
    {
        public required string Path { get; init; }
        public required Encoding Enc { get; init; }
        public required bool Bom { get; init; }
        public required string Text { get; init; }
        public bool Delayed { get; set; }            // setlocal EnableDelayedExpansion: '!' is live even inside quotes
        public bool Chcp65001 { get; set; }
        public int ExeEnd { get; set; } = -1;        // right after the DayZServer exe token of the launch line
        public int LineStart { get; set; }
        public int LineEnd { get; set; }
        public Slot Mod { get; } = new("-mod");
        public Slot Srv { get; } = new("-serverMod");
        public string? Error { get; set; }
        public string? Warning { get; set; }
    }

    // One mod-list switch of the launch line.
    private sealed class Slot(string key)
    {
        public string Key { get; } = key;
        public Regex Find { get; } = new(@"(?<=^|[\s""])" + Regex.Escape(key) + "=", RegexOptions.IgnoreCase);
        public int Start { get; set; } = -1;         // the whole token, when there is one
        public int End { get; set; }
        public string Value { get; set; } = "";
        public string? Var { get; set; }             // -mod=%Var%: the list lives in the last "set Var=" before the launch
        public int SetStart { get; set; } = -1;      // the whole set statement, from "set" to the end of its line
        public int SetEnd { get; set; }
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetOEMCP();

    // cmd reads a script in the console (OEM) code page of the system unless it is UTF-8; the rest of
    // the file is written back exactly as it was read.
    private static Script Load(string bat)
    {
        var bytes = File.ReadAllBytes(bat);
        var bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        Encoding enc = bom || IsUtf8(bytes)
            ? new UTF8Encoding(false, true)
            : Encoding.GetEncoding((int)GetOEMCP(), EncoderFallback.ExceptionFallback, DecoderFallback.ReplacementFallback);
        var text = enc.GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
        var s = new Script { Path = bat, Enc = enc, Bom = bom, Text = text };
        s.Delayed = Regex.IsMatch(text, @"enabledelayedexpansion|/v:on", RegexOptions.IgnoreCase);
        s.Chcp65001 = Regex.IsMatch(text, @"\bchcp\s+65001\b", RegexOptions.IgnoreCase);
        Locate(s);
        return s;
    }

    private static bool IsUtf8(byte[] bytes)
    {
        if (bytes.All(b => b < 0x80)) return false;
        try { _ = new UTF8Encoding(false, true).GetString(bytes); return true; }
        catch (DecoderFallbackException) { return false; }
    }

    private static List<(int Start, int End)> Lines(string t)
    {
        var lines = new List<(int, int)>();
        for (int i = 0, st = 0; i <= t.Length; i++)
            if (i == t.Length || t[i] == '\n') { lines.Add((st, i > st && t[i - 1] == '\r' ? i - 1 : i)); st = i + 1; }
        return lines;
    }

    // One cmd token: a quoted run (quotes kept) or everything up to whitespace.
    private static (int Start, int End, string Text)? NextToken(string line, ref int i)
    {
        while (i < line.Length && char.IsWhiteSpace(line[i])) i++;
        if (i >= line.Length) return null;
        var st = i;
        var q = false;
        while (i < line.Length && (q || !char.IsWhiteSpace(line[i])))
        {
            if (line[i] == '"') q = !q;
            i++;
        }
        return (st, i, line[st..i].Trim('"'));
    }

    // The line really starts the server when DayZServer*.exe is the command itself: first on the line, or
    // after "start ["title"] [/switches]" or "call". taskkill/echo/title/set lines that only name it don't count.
    private static int ExeTokenEnd(string line)
    {
        var i = 0;
        while (i < line.Length && (char.IsWhiteSpace(line[i]) || line[i] == '@')) i++;
        var tok = NextToken(line, ref i);
        if (tok == null) return -1;
        if (tok.Value.Text.Equals("call", StringComparison.OrdinalIgnoreCase)) tok = NextToken(line, ref i);
        else if (tok.Value.Text.Equals("start", StringComparison.OrdinalIgnoreCase))
        {
            var j = i;
            while (j < line.Length && char.IsWhiteSpace(line[j])) j++;
            if (j < line.Length && line[j] == '"') NextToken(line, ref i); // window title
            while ((tok = NextToken(line, ref i)) != null && tok.Value.Text.StartsWith('/'))
                if (tok.Value.Text.Equals("/d", StringComparison.OrdinalIgnoreCase)) NextToken(line, ref i);
        }
        if (tok == null) return -1;
        var name = tok.Value.Text;
        var slash = name.LastIndexOfAny(new[] { '\\', '/' });
        if (slash >= 0) name = name[(slash + 1)..];
        return Regex.IsMatch(name, @"^DayZServer[^\\/]*\.exe$", RegexOptions.IgnoreCase) ? tok.Value.End : -1;
    }

    private static bool IsComment(string line)
    {
        var l = line.TrimStart().TrimStart('@');
        return l.StartsWith("::") || Regex.IsMatch(l, @"^rem(\s|$)", RegexOptions.IgnoreCase);
    }

    private static void Locate(Script s)
    {
        var t = s.Text;
        var lines = Lines(t);
        var launches = new List<(int Start, int End, int Exe)>();
        foreach (var (a, b) in lines)
        {
            var line = t[a..b];
            if (IsComment(line)) continue;
            var exe = ExeTokenEnd(line);
            if (exe >= 0) launches.Add((a, b, a + exe));
        }
        if (launches.Count == 0)
        {
            s.Error = "в батнике нет строки, которая запускает DayZServer_x64.exe";
            return;
        }

        bool HasList(string line) => s.Mod.Find.IsMatch(line) || s.Srv.Find.IsMatch(line);
        var withMod = launches.Where(l => HasList(t[l.Start..l.End])).ToList();
        var launch = withMod.Count > 0 ? withMod[0] : launches[0];
        s.LineStart = launch.Start;
        s.LineEnd = launch.End;
        s.ExeEnd = launch.Exe;
        if (withMod.Count > 1 || (withMod.Count == 1 && launches.Count > 1))
            s.Warning = "в батнике несколько строк запуска сервера — меняется " + (Array.IndexOf(lines.ToArray(), (launch.Start, launch.End)) + 1) + "-я";

        // A list hidden in a variable on the launch line (set ARGS=... -mod=...): not ours to rewrite.
        foreach (Match v in Regex.Matches(t[launch.Start..launch.End], @"%([A-Za-z0-9_]+)%"))
            if (Regex.IsMatch(t, @"^\s*set\s+""?" + Regex.Escape(v.Groups[1].Value) + @"=.*-(server)?mod=", RegexOptions.IgnoreCase | RegexOptions.Multiline))
                s.Error ??= "список модов задан внутри переменной %" + v.Groups[1].Value + "% — поправьте батник вручную";

        Parse(s, s.Mod, lines, launch.Start, launch.End);
        Parse(s, s.Srv, lines, launch.Start, launch.End);
    }

    private static void Parse(Script s, Slot sl, List<(int Start, int End)> lines, int a0, int b0)
    {
        var t = s.Text;
        var m = sl.Find.Match(t, a0, b0 - a0);
        if (!m.Success) return;
        var key = m.Index;
        int start = key, valStart = key + m.Length, valEnd, end;
        if (key > a0 && t[key - 1] == '"')
        {
            start = key - 1;
            valEnd = t.IndexOf('"', valStart);
            if (valEnd < 0 || valEnd > b0) valEnd = b0;
            end = Math.Min(valEnd + 1, b0);
        }
        else if (valStart < b0 && t[valStart] == '"')
        {
            valStart++;
            valEnd = t.IndexOf('"', valStart);
            if (valEnd < 0 || valEnd > b0) valEnd = b0;
            end = Math.Min(valEnd + 1, b0);
        }
        else
        {
            // Unquoted, but a quoted entry inside still groups its spaces: -mod=@CF;"@Mod With Space";@X
            valEnd = valStart;
            var q = false;
            while (valEnd < b0 && (q || !char.IsWhiteSpace(t[valEnd])))
            {
                if (t[valEnd] == '"') q = !q;
                valEnd++;
            }
            end = valEnd;
        }
        sl.Start = start;
        sl.End = end;
        sl.Value = t[valStart..valEnd];

        if (!HasVariable(sl.Value)) return;
        var vm = Regex.Match(sl.Value.Trim().Trim('"'), @"^%([A-Za-z0-9_]+)%$");
        if (!vm.Success)
        {
            s.Error ??= "строка " + sl.Key + " собрана из переменных: " + sl.Value + " — поправьте батник вручную";
            return;
        }
        sl.Var = vm.Groups[1].Value;
        var set = new Regex(@"^(\s*@?)(set\s+(""?)" + Regex.Escape(sl.Var) + @"=)", RegexOptions.IgnoreCase);
        foreach (var (a, b) in lines)
        {
            if (a >= a0) break;
            var sm = set.Match(t[a..b]);
            if (!sm.Success) continue;
            var vs = a + sm.Length;
            var ve = b;
            if (sm.Groups[3].Value == "\"") { var q = t.LastIndexOf('"', b - 1, b - vs); if (q >= vs) ve = q; }
            sl.SetStart = a + sm.Groups[1].Length;
            sl.SetEnd = b;
            sl.Value = t[vs..ve].Trim('"');
        }
        if (sl.SetStart < 0) s.Error ??= "переменная %" + sl.Var + "% из строки " + sl.Key + " не задана выше строки запуска — поправьте батник вручную";
        else if (HasVariable(sl.Value)) s.Error ??= "список модов в %" + sl.Var + "% собран из других переменных — поправьте батник вручную";
    }

    // %% is a literal percent sign in a batch file, not a variable.
    private static bool HasVariable(string v) => v.Replace("%%", "").Contains('%');

    public static ModsSetup Setup(string serverDir, string bat, string? workshopDir)
    {
        var dir = string.IsNullOrWhiteSpace(workshopDir) ? DefaultWorkshopDir() ?? "" : Canonical(workshopDir.Trim().Trim('"'));
        var issues = new List<string>();
        var available = ListWorkshop(dir, issues);
        var local = ListLocal(serverDir, issues);
        var path = System.IO.Path.Combine(serverDir, System.IO.Path.GetFileName(bat));
        var unread = issues.Count == 0 ? null
            : $"не удалось открыть папки модов ({issues.Count}), поэтому у них не видно ключей и id: " + string.Join("; ", issues.Take(3))
              + (issues.Count > 3 ? "; …" : "") + ". Нажмите «Обновить», когда папки станут доступны.";
        if (!File.Exists(path)) return new(path, dir, available, Array.Empty<ModEntry>(), "батник не найден: " + path, unread, Local: local);
        Script s;
        try { s = Load(path); }
        catch (Exception e) { return new(path, dir, available, Array.Empty<ModEntry>(), e.Message, unread, Local: local); }
        var known = available.Concat(local).ToList();
        var current = Split(s.Mod.Value).Select(x => Entry(x, serverDir, known)).ToList();
        var currentServer = Split(s.Srv.Value).Select(x => Entry(x, serverDir, known)).ToList();
        var busy = ServerLaunch.ScriptRunning(path);
        var warning = s.Warning == null ? unread : unread == null ? s.Warning : s.Warning + "\n" + unread;
        return new(path, dir, available, current, s.Error, warning, s.Delayed, busy, currentServer, local);
    }

    public static LaunchArgs Launch(string serverDir, string bat)
    {
        var path = System.IO.Path.Combine(serverDir, System.IO.Path.GetFileName(bat));
        if (!File.Exists(path)) return new(path, [], [], [], null, "батник не найден: " + path);
        Script s;
        try { s = Load(path); }
        catch (Exception e) { return new(path, [], [], [], null, e.Message); }
        var hidden = new List<string>();
        foreach (var sl in new[] { s.Mod, s.Srv })
            if (sl.Start >= 0 && s.Text[sl.Start] == '"' && Split(sl.Value).Any()) hidden.Add(sl.Key);
        string? profiles = null;
        if (s.ExeEnd >= 0)
        {
            var m = Regex.Match(s.Text[s.LineStart..s.LineEnd], @"(?<=^|[\s""])-profiles=(""([^""]*)""|([^\s""]+))", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                var v = m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;
                if (v.Length > 0 && !HasVariable(v)) profiles = v;
            }
        }
        return new(path, Split(s.Mod.Value).ToList(), Split(s.Srv.Value).ToList(), hidden, profiles, s.Error);
    }

    private static IEnumerable<string> Split(string value) =>
        value.Split(';').Select(x => x.Trim().Trim('"').Trim().Replace("%%", "%")).Where(x => x.Length > 0);

    private static ModEntry Entry(string text, string serverDir, IReadOnlyList<WorkshopMod> available)
    {
        string full;
        try { full = System.IO.Path.GetFullPath(System.IO.Path.IsPathRooted(text) ? text : System.IO.Path.Combine(serverDir, text)); }
        catch (Exception) { full = text; }
        var target = LinkTarget(full);
        var ws = available.FirstOrDefault(m => Same(m.Path, full) || (m.Target != null && (Same(m.Target, full) || (target != null && Same(m.Target, target)))));
        var name = ws?.Name ?? System.IO.Path.GetFileName(full.TrimEnd('\\')).TrimStart('@');
        return new ModEntry(text, full, name, Directory.Exists(full), ws?.Path);
    }

    private static bool Same(string a, string b) =>
        string.Equals(a.TrimEnd('\\', '/'), b.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    // How a mod is best written into a script: its own path when cmd reads it as is, else the plain
    // junction target. Null when neither is safe (the caller then tries harder or refuses).
    public static string? Spell(ModPick p, bool delayed)
    {
        bool Plain(string x) => x.All(ch => ch < 0x80) && !x.Contains('%') && !(delayed && x.Contains('!'));
        if (Plain(p.Text)) return p.Text;
        if (p.Alt != null && Plain(p.Alt)) return p.Alt;
        return null;
    }

    // Writes both lists into the start script (a copy of the old one is kept next to it) and copies the
    // .bikey of every -mod entry into <server>\keys; -serverMod mods never reach the clients and need no key.
    // An emptied list loses its switch. How a switch is spelled: see Token.
    public static ModsSaved Save(string serverDir, string bat, IReadOnlyList<ModPick> picks, IReadOnlyList<ModPick>? serverPicks = null)
    {
        var path = System.IO.Path.Combine(serverDir, System.IO.Path.GetFileName(bat));
        // cmd re-reads a running script from a byte offset after every command: a rewrite under a live
        // restart loop makes it jump into the middle of a line.
        if (ServerLaunch.ScriptRunning(path))
            throw new InvalidOperationException("батник сейчас выполняется — закройте его окно и сохраните снова");
        var s = Load(path);
        if (s.Error != null) throw new InvalidOperationException(s.Error);
        if (serverPicks == null)
            serverPicks = Split(s.Srv.Value).Select(x => new ModPick(x, null)).ToList();

        var (texts, names) = Spelled(s, picks);
        var (stexts, _) = Spelled(s, serverPicks);
        if (texts.FirstOrDefault(x => stexts.Any(y => Same(x, y))) is { } both)
            throw new InvalidOperationException("мод указан и в -mod, и в -serverMod — оставьте его в одном списке: " + both);
        if (s.Mod.Var != null && s.Mod.Var.Equals(s.Srv.Var, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("-mod и -serverMod берут список из одной переменной %" + s.Mod.Var + "% — поправьте батник вручную");

        var t = s.Text;
        var edits = new List<(int At, int End, string Text)>();
        var insert = "";
        var expanded = 0;
        foreach (var (sl, list) in new[] { (s.Mod, texts), (s.Srv, stexts) })
        {
            var value = string.Join(";", list.Select(x => x.Replace("%", "%%")));
            if (sl.Var != null)
            {
                edits.Add((sl.Start, sl.End, Token(sl.Key, "%" + sl.Var + "%", NeedsQuotes(value))));
                edits.Add((sl.SetStart, sl.SetEnd, "set \"" + sl.Var + "=" + value + "\""));
                expanded += value.Length;
            }
            else if (sl.Start >= 0)
            {
                if (list.Count > 0) edits.Add((sl.Start, sl.End, Token(sl.Key, value)));
                else
                {
                    var a = sl.Start;
                    while (a > s.LineStart && (t[a - 1] == ' ' || t[a - 1] == '\t')) a--;
                    edits.Add((a, sl.End, ""));
                }
            }
            else if (list.Count > 0) insert += " " + Token(sl.Key, value);
        }
        if (insert.Length > 0) edits.Add((s.ExeEnd, s.ExeEnd, insert));

        // Back to front so earlier offsets stay valid; at one offset the replacement goes before the insertion.
        var launchLen = s.LineEnd - s.LineStart + expanded;
        foreach (var e in edits.OrderByDescending(e => e.At).ThenByDescending(e => e.End))
        {
            if (e.At >= s.LineStart && e.End <= s.LineEnd) launchLen += e.Text.Length - (e.End - e.At);
            t = t[..e.At] + e.Text + t[e.End..];
        }
        if (launchLen > CmdLineLimit)
            throw new InvalidOperationException($"строка запуска получится длиной {launchLen} символов, а cmd режет строки после 8191 — уберите часть модов");

        string? backup = null;
        if (t != s.Text)
        {
            backup = Backup(path);
            var body = s.Enc.GetBytes(t);
            File.WriteAllBytes(path, s.Bom ? new byte[] { 0xEF, 0xBB, 0xBF }.Concat(body).ToArray() : body);
        }

        var keysDir = System.IO.Path.Combine(serverDir, "keys");
        var copied = 0;
        var noKeys = new List<string>();
        for (var i = 0; i < texts.Count; i++)
        {
            var full = System.IO.Path.IsPathRooted(texts[i]) ? texts[i] : System.IO.Path.Combine(serverDir, texts[i]);
            var keys = KeyFiles(Readable(full, LinkTarget(full), null));
            if (keys.Count == 0) { noKeys.Add(names[i]); continue; }
            Directory.CreateDirectory(keysDir);
            foreach (var k in keys)
            {
                var dst = System.IO.Path.Combine(keysDir, System.IO.Path.GetFileName(k));
                if (File.Exists(dst) && File.ReadAllBytes(dst).AsSpan().SequenceEqual(File.ReadAllBytes(k))) continue;
                File.Copy(k, dst, true);
                copied++;
            }
        }
        return new ModsSaved(path, backup, texts.Count, copied, noKeys, stexts.Count);
    }

    // DayZ splits its command line at spaces unless the whole token is quoted: "-mod=C:\a b;..." works,
    // -mod="C:\a b;..." loads no mod at all (the path breaks at the first space). The GRAFT host reads a
    // switch only when it starts after whitespace, so a fully quoted one is invisible to it. Hence: no
    // quotes when the list has no space (both read it), the whole token quoted when it has (DayZ reads it,
    // and plugins belong to mods inside the server folder, which are written relative and space-free).
    public static string Token(string key, string value) => Token(key, value, NeedsQuotes(value));

    private static string Token(string key, string value, bool quote) =>
        quote ? "\"" + key + "=" + value + "\"" : key + "=" + value;

    private static bool NeedsQuotes(string value) => value.IndexOfAny(new[] { ' ', '\t', '&', '(', ')', '^', '|', '<', '>' }) >= 0;

    // A folder inside the server is written relative to it (@MyAddon): the server starts in its own folder
    // and the GRAFT host resolves relative paths from there, so the entry stays short and space-free.
    public static string Written(string serverDir, string text)
    {
        try
        {
            if (!System.IO.Path.IsPathRooted(text)) return text;
            var rel = System.IO.Path.GetRelativePath(System.IO.Path.GetFullPath(serverDir), System.IO.Path.GetFullPath(text));
            return rel.StartsWith("..") || System.IO.Path.IsPathRooted(rel) || rel == "." ? text : rel;
        }
        catch (Exception) { return text; }
    }

    // The entries of one list as they will be written, repeats dropped.
    private static (List<string> Texts, List<string> Names) Spelled(Script s, IReadOnlyList<ModPick> picks)
    {
        var texts = new List<string>();
        var names = new List<string>();
        var serverDir = System.IO.Path.GetDirectoryName(s.Path)!;
        foreach (var p in picks)
        {
            var text = Spell(p, s.Delayed);
            if (text == null)
            {
                var ascii = p.Text.All(ch => ch < 0x80);
                if (s.Delayed && p.Text.Contains('!'))
                    throw new InvalidOperationException("в батнике включён EnableDelayedExpansion, а в пути мода есть «!»: " + p.Text);
                if (!ascii && (s.Enc is UTF8Encoding ? !s.Chcp65001 : !Encodable(s.Enc, p.Text)))
                    throw new InvalidOperationException("путь мода нельзя записать так, чтобы cmd его прочитал (" + s.Enc.WebName + "): " + p.Text);
                text = p.Text;
            }
            if (text.Contains(';') || text.Contains('"')) throw new InvalidOperationException("в пути мода есть ; или кавычка: " + text);
            text = Written(serverDir, text);
            if (texts.Any(x => Same(x, text))) continue;
            texts.Add(text);
            names.Add(System.IO.Path.GetFileName(p.Text.TrimEnd('\\', '/')));
        }
        return (texts, names);
    }

    private static bool Encodable(Encoding enc, string text)
    {
        try { enc.GetBytes(text); return true; }
        catch (EncoderFallbackException) { return false; }
    }

    // <name>.bat.<stamp>.bak next to the script, never overwritten; only the tool's own five newest are
    // kept, hand-made backups are left alone.
    private static string Backup(string path)
    {
        var dst = path + "." + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".bak";
        File.Copy(path, dst, false);
        try
        {
            var dir = System.IO.Path.GetDirectoryName(path)!;
            var own = new Regex("^" + Regex.Escape(System.IO.Path.GetFileName(path)) + @"\.\d{8}-\d{6}(-\d{3})?\.bak$", RegexOptions.IgnoreCase);
            foreach (var old in Directory.GetFiles(dir).Where(f => own.IsMatch(System.IO.Path.GetFileName(f))).OrderByDescending(f => f).Skip(5))
                File.Delete(old);
        }
        catch (Exception) { }
        return dst;
    }
}
