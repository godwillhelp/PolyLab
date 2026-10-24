using Pfx.Core.Schema;
using Pfx.Core.Text;

namespace Pfx.Core.Model;

// "Particle { .. }", "ParticleSprite { .. }" or the older "material "path": Particle { .. }".
public sealed class EmatDocument : KeyedBlock
{
    private EmatDocument(EnfDocument doc, EnfBlock root, PfxSchema schema) : base(doc, root, SchemaScope.Material, schema)
    {
    }

    public static EmatDocument From(EnfDocument doc, PfxSchema? schema = null)
    {
        var root = doc.Root ?? throw new InvalidDataException("No material block");
        return new EmatDocument(doc, root, schema ?? PfxSchema.Shared);
    }

    public static EmatDocument Load(string path) => From(EnfDocument.Load(path));

    public string ClassName => Block.ClassName;

    public bool IsParticleClass => Schema.MaterialClasses.ContainsKey(ClassName);

    public byte[] ToBytes() => Doc.ToBytes();
}
