using System.Runtime.CompilerServices;
using Chip.Editor.Framework;

namespace Chip.Editor.Features.View;

/// <summary>View options that aren't per pane. One per editor context, like <see cref="Viewport.ViewportLayout"/>.</summary>
public sealed class ViewSettings
{
    static readonly ConditionalWeakTable<EditorContext, ViewSettings> Table = new();

    public static ViewSettings Of(EditorContext ctx) => Table.GetValue(ctx, _ => new ViewSettings());

    /// <summary>Scythe: open (boundary) edges drawn dashed in a distinct colour.</summary>
    public bool BoundaryEdges { get; set; } = true;

    /// <summary>Tool surfaces (they don't render in the game) drawn see-through, Source 2 style (View > Translucent tool surfaces).</summary>
    public bool TranslucentTools { get; set; } = true;

    /// <summary>Lengths beside the selection's edges, Scythe style (View > Show dimensions).</summary>
    public bool ShowDimensions { get; set; } = true;

    /// <summary>Each edge, the selection's overall size, or both (View > Dimensions).</summary>
    public Dimensions.DimensionMode DimensionMode { get; set; } = Dimensions.DimensionMode.Both;

    /// <summary>Short corner-normal lines on the selection (stream L, View > Show normals).</summary>
    public bool ShowNormals { get; set; }

    /// <summary>
    /// Tools whose construction grid follows the hovered face. Creation tools from other streams add their ids here
    /// (e.g. in their feature's Register) to get the same behaviour.
    /// </summary>
    public HashSet<string> ConstructionTools { get; } = new(StringComparer.Ordinal) { "block", "rectangle", "polygon" };
}
