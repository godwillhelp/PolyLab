using System.Buffers.Binary;
using System.Text.Json.Serialization;

namespace Pfx.XRay;

public enum XrKind
{
    Effect, Group, Library, Lanims, Config, Sound, Texture, Archive,
    OtherArchive, Script, Interface, Model, TextureInfo, Other,
}

// What the recogniser learned from a file's first bytes; kept in the scan cache so a re-scan does not read it again.
public sealed class XrFacts
{
    // Effect: shader and texture of the sprite; Group: effect names.
    public string? Shader { get; set; }
    public string? Texture { get; set; }
    public List<string>? Names { get; set; }
    // Sound: X-Ray parameters from the Vorbis comment (null = an ogg without them).
    public XrSoundParams? Sound { get; set; }
    // Texture: size and format from the DDS header.
    public int Width { get; set; }
    public int Height { get; set; }
    public string? Format { get; set; }
    // Config: sections in the head that name particles / sounds (zones) or carry inv_name (items).
    public int Zones { get; set; }
    public int Items { get; set; }
}

// Skip reasons are codes the UI words: empty, binary, text, model, thm, otherarchive, xml, script.
public static class XrRecognize
{
    // The recogniser reads this much of each file: whole .pe / .pg / most .ltx files, a DDS header, the first two Ogg
    // packets (the comment packet is small) and enough of a script or XML to tell what it is.
    public const int HeadBytes = 64 * 1024;

    // X-Ray archives (.db / .xdb): the header chunk 0x29A holding an ltx "[header]" (Anomaly's .db0 files), or the file
    // table chunk 1 (compressed bit allowed) of older ones. SQLite and other ".db" files fail this.
    public const uint ArchiveHeaderChunk = 0x29A;
    // Texture descriptions (.thm, editor ETextureParams): the version chunk 0x0810 of two bytes comes first.
    public const uint ThmVersionChunk = 0x0810;
    private static readonly byte[] SevenZip = { 0x37, 0x7A, 0xBC, 0xAF };

    private static readonly string[] ZoneKeys = { "idle_particles", "blowout_particles", "hit_small_particles", "hit_big_particles", "entrance_small_particles",
        "idle_sound", "blowout_sound", "blowout_light" };

    public static (XrKind Kind, XrFacts? Facts, string? Why) Recognize(ReadOnlySpan<byte> head, long size)
    {
        if (size == 0)
            return (XrKind.Other, null, "empty");
        if (XrLibrary.Looks(head))
            return (XrKind.Library, null, null);
        if (XrLanims.Looks(head))
            return (XrKind.Lanims, null, null);
        if (XrSoundParams.IsOgg(head))
            return (XrKind.Sound, new XrFacts { Sound = XrSoundParams.Read(head.ToArray()) }, null);
        if (head.Length >= 128 && head[..4].SequenceEqual("DDS "u8))
        {
            int h = BinaryPrimitives.ReadInt32LittleEndian(head[12..]), w = BinaryPrimitives.ReadInt32LittleEndian(head[16..]);
            uint pfFlags = BinaryPrimitives.ReadUInt32LittleEndian(head[80..]);
            string format = (pfFlags & 4) != 0 ? System.Text.Encoding.ASCII.GetString(head.Slice(84, 4)).TrimEnd('\0') : $"RGB{BinaryPrimitives.ReadUInt32LittleEndian(head[88..])}";
            return (XrKind.Texture, new XrFacts { Width = w, Height = h, Format = format }, null);
        }
        if (head.Length >= 8)
        {
            uint id = BinaryPrimitives.ReadUInt32LittleEndian(head), len = BinaryPrimitives.ReadUInt32LittleEndian(head[4..]);
            if (id == ArchiveHeaderChunk && head.Length > 16 && head[8..16].SequenceEqual("[header]"u8))
                return (XrKind.Archive, null, null);
            if ((id & 0x7FFFFFFF) == 1 && (id & 0x80000000) != 0 && len + 8 <= size)
                return (XrKind.Archive, null, null);
            if (id == 1 && len == 44 && head.Length > 8 && head[8] == 4)
                return (XrKind.Model, null, "model");
            if (id == ThmVersionChunk && len == 2)
                return (XrKind.TextureInfo, null, "thm");
        }
        if (head.Length >= 4 && (head[..4].SequenceEqual("PK\x03\x04"u8) || head[..4].SequenceEqual("Rar!"u8) || head[..4].SequenceEqual(SevenZip)))
            return (XrKind.OtherArchive, null, "otherarchive");
        if (LooksBinary(head))
            return (XrKind.Other, null, "binary");
        string text = XrText.Cp1251.GetString(Whole(head, size));
        string trimmed = text.TrimStart('﻿', ' ', '\t', '\r', '\n');
        if (trimmed.StartsWith('<'))
            return (XrKind.Interface, null, "xml");
        var secs = XrText.ReadIni(text);
        if (secs.TryGetValue("_effect", out var e) && (e.ContainsKey("action_count") || e.ContainsKey("max_particles")))
        {
            var sprite = secs.GetValueOrDefault("sprite");
            return (XrKind.Effect, new XrFacts { Shader = sprite?.GetValueOrDefault("shader"), Texture = sprite?.GetValueOrDefault("texture") }, null);
        }
        if (secs.TryGetValue("_group", out var g) && g.ContainsKey("effects_count"))
        {
            var names = secs.Where(s => s.Key.StartsWith("effect_", StringComparison.Ordinal)).Select(s => s.Value.GetValueOrDefault("effect_name") ?? "").Where(n => n.Length > 0).ToList();
            return (XrKind.Group, new XrFacts { Names = names }, null);
        }
        if (XrLtx.Looks(text, out _))
        {
            var file = XrLtx.Read(text, "");
            int zones = file.Sections.Count(s => ZoneKeys.Any(s.Keys.ContainsKey));
            int items = file.Sections.Count(s => s.Keys.ContainsKey("inv_name"));
            return (XrKind.Config, new XrFacts { Zones = zones, Items = items }, null);
        }
        if (text.Contains("function ", StringComparison.Ordinal) && text.Contains("end", StringComparison.Ordinal))
            return (XrKind.Script, null, "script");
        return (XrKind.Other, null, "text");
    }

    // A head cut inside a line loses that line (text kinds are decided on whole lines).
    private static byte[] Whole(ReadOnlySpan<byte> head, long size)
    {
        if (head.Length >= size)
            return head.ToArray();
        int last = head.LastIndexOf((byte)'\n');
        return (last > 0 ? head[..last] : head).ToArray();
    }

    // Text files of these mods are cp1251 or UTF-8: control bytes other than tab / CR / LF / form feed mean binary.
    private static bool LooksBinary(ReadOnlySpan<byte> head)
    {
        int n = Math.Min(head.Length, 4096), bad = 0;
        for (int i = 0; i < n; i++)
        {
            byte b = head[i];
            if (b == 0)
                return true;
            if (b < 32 && b is not (9 or 10 or 13 or 12))
                bad++;
        }
        return bad > n / 100;
    }
}

// Kinds that count as X-Ray content (a folder holding none of them has nothing usable).
public static class XrKinds
{
    public static bool IsContent(XrKind k) => k is XrKind.Effect or XrKind.Group or XrKind.Library or XrKind.Lanims or XrKind.Config or XrKind.Sound or XrKind.Texture;
}
