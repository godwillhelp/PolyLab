namespace Pfx.Core.Schema;

public enum EditorKind { Unknown, Number, Integer, Toggle, Vector3, Choice, Resource, Texture, Graph, Color, Matrix, Text, Children }

// What the inspector needs to show one key: the control, its unit range and the engine default. Built from the
// schema only, so a key the engine registers can never lack a control (Coverage test in Pfx.Tests).
public sealed record FieldDescriptor(SchemaField Field, EditorKind Editor)
{
    public static FieldDescriptor For(SchemaField f) => f.Type switch
    {
        "float" => new(f, EditorKind.Number),
        "int" => new(f, EditorKind.Integer),
        "bool" => new(f, EditorKind.Toggle),
        "vector" => new(f, EditorKind.Vector3),
        "enum" => new(f, EditorKind.Choice),
        "resource" => new(f, EditorKind.Resource),
        "texture" => new(f, EditorKind.Texture),
        "graph2" => new(f, EditorKind.Graph),
        "graph4" => new(f, EditorKind.Graph),
        "color" => new(f, EditorKind.Color),
        "matrix" => new(f, EditorKind.Matrix),
        "string" => new(f, EditorKind.Text),
        "objects" => new(f, EditorKind.Children),
        _ => new(f, EditorKind.Unknown),
    };
}
