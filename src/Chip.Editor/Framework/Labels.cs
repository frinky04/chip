using Chip.Core.Scene;

namespace Chip.Editor.Framework;

/// <summary>User-facing names and number formats, so wording stays consistent across panels.</summary>
public static class Labels
{
    public static string Name(this SelectMode m) => m switch
    {
        SelectMode.Vertex => "Vertex", SelectMode.Edge => "Edge", SelectMode.Face => "Face", _ => "Object",
    };

    /// <summary>"3 faces", "1 object".</summary>
    public static string Count(this SelectMode m, int n)
    {
        string noun = m.Name().ToLowerInvariant();
        return n == 1 ? $"1 {noun}" : m == SelectMode.Vertex ? $"{n} vertices" : $"{n} {noun}s";
    }

    /// <summary>"vertices", "edges", "faces", "objects".</summary>
    public static string Plural(this SelectMode m) => m == SelectMode.Vertex ? "vertices" : m.Name().ToLowerInvariant() + "s";

    /// <summary>Lengths for display: centimetres below a metre.</summary>
    public static string Length(float metres) => MathF.Abs(metres) < 1 ? $"{metres * 100:0.##} cm" : $"{metres:0.##} m";
}
