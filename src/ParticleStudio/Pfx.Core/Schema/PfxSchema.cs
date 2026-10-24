using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pfx.Core.Schema;

public enum SchemaScope { Effect, Emitor, Material }

public sealed record SchemaRange(double Min, double Max, double? Step)
{
    public static SchemaRange? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            return null;
        double At(int i) => double.Parse(parts[i], CultureInfo.InvariantCulture);
        return new SchemaRange(At(0), At(1), parts.Length > 2 ? At(2) : null);
    }
}

public sealed class SchemaField
{
    [JsonPropertyName("key")] public string Key { get; init; } = "";
    [JsonPropertyName("type")] public string Type { get; init; } = "";
    [JsonPropertyName("default")] public string? Default { get; init; }
    [JsonPropertyName("range")] public string? RangeText { get; init; }
    [JsonPropertyName("editor")] public string? Editor { get; init; }
    [JsonPropertyName("group")] public string? Group { get; init; }
    [JsonPropertyName("desc")] public string? Description { get; init; }
    [JsonPropertyName("values")] public List<string>? Values { get; init; }
    [JsonPropertyName("resource")] public string? Resource { get; init; }
    [JsonPropertyName("code")] public string? Code { get; init; }

    [JsonIgnore] public SchemaScope Scope { get; internal set; }

    // Position in the engine's registration order; Workbench writes keys in this order.
    [JsonIgnore] public int Order { get; internal set; }

    [JsonIgnore] public SchemaRange? Range => SchemaRange.Parse(RangeText);
}

public sealed class PfxSchema
{
    private readonly Dictionary<(SchemaScope, string), SchemaField> _byKey = new();

    private PfxSchema(List<SchemaField> effect, List<SchemaField> emitor, List<SchemaField> material,
        IReadOnlyDictionary<string, string> materialClasses, string sourceSha256)
    {
        Effect = Index(effect, SchemaScope.Effect);
        Emitor = Index(emitor, SchemaScope.Emitor);
        Material = Index(material, SchemaScope.Material);
        MaterialClasses = materialClasses;
        SourceSha256 = sourceSha256;
    }

    public IReadOnlyList<SchemaField> Effect { get; }
    public IReadOnlyList<SchemaField> Emitor { get; }
    public IReadOnlyList<SchemaField> Material { get; }
    public IReadOnlyDictionary<string, string> MaterialClasses { get; }
    public string SourceSha256 { get; }

    public SchemaField? Find(SchemaScope scope, string key) =>
        _byKey.TryGetValue((scope, key.ToLowerInvariant()), out var f) ? f : null;

    private List<SchemaField> Index(List<SchemaField> fields, SchemaScope scope)
    {
        for (int i = 0; i < fields.Count; i++)
        {
            fields[i].Scope = scope;
            fields[i].Order = i;
            _byKey[(scope, fields[i].Key.ToLowerInvariant())] = fields[i];
        }
        return fields;
    }

    private static PfxSchema? _shared;

    public static PfxSchema Shared => _shared ??= Load();

    public static PfxSchema Load()
    {
        using var stream = typeof(PfxSchema).Assembly.GetManifestResourceStream("Pfx.Core.schema.json")
                           ?? throw new InvalidOperationException("schema.json is not embedded");
        var file = JsonSerializer.Deserialize<SchemaFile>(stream) ?? throw new InvalidOperationException("Empty schema");
        var material = new List<SchemaField>(file.Material.Base);
        material.AddRange(file.Material.Particle);
        return new PfxSchema(file.Effect, file.Emitor, material, file.Material.Classes, file.Source.Sha256);
    }

    private sealed class SchemaFile
    {
        [JsonPropertyName("source")] public SourceInfo Source { get; init; } = new();
        [JsonPropertyName("effect")] public List<SchemaField> Effect { get; init; } = new();
        [JsonPropertyName("emitor")] public List<SchemaField> Emitor { get; init; } = new();
        [JsonPropertyName("material")] public MaterialPart Material { get; init; } = new();
    }

    private sealed class SourceInfo
    {
        [JsonPropertyName("sha256")] public string Sha256 { get; init; } = "";
    }

    private sealed class MaterialPart
    {
        [JsonPropertyName("classes")] public Dictionary<string, string> Classes { get; init; } = new();
        [JsonPropertyName("base")] public List<SchemaField> Base { get; init; } = new();
        [JsonPropertyName("particle")] public List<SchemaField> Particle { get; init; } = new();
    }
}
