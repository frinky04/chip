using System.Numerics;
using Chip.Core.Scene;
using Chip.Editor.Framework;
using Chip.Editor.UI;
using ImGuiNET;
using static Chip.Editor.UI.Widgets;

namespace Chip.Editor.Inspector;

/// <summary>What is selected, with exact bounds entry (min corner and size in metres).</summary>
public sealed class SelectionSection : IInspectorSection
{
    readonly Rendering.FrameCache<(Vector3 Min, Vector3 Max)?> _bounds = new();

    public string Id => "selection";
    public int Order => 100;
    public bool Visible(EditorContext ctx) => !ctx.Sel.IsEmpty;

    public void Draw(EditorContext ctx)
    {
        var sel = ctx.Sel;
        if (!Heading("Selection", sel.Mode.Count(sel.Count))) return;
        if (!_bounds.IsCurrent(ctx.Session)) _bounds.Store(ctx.Session, ctx.SelectionBounds());
        if (_bounds.Value is not { } b) return;
        var min = b.Min;
        var sz = b.Max - b.Min;
        RowLabel("Min", "Bounds minimum corner in metres. Type a value, then Enter.");
        if (TaggedFields("##min", ["X", "Y", "Z"], Theme.AxisU32, [min.X, min.Y, min.Z], [false, false, false], "%.3f", out int ai, out float av))
        {
            if (ai == 0) min.X = av; else if (ai == 1) min.Y = av; else min.Z = av;
            SetBounds(ctx, min, b.Max - b.Min);
        }
        RowLabel("Size", "Extent in metres. Type a value, then Enter.");
        if (TaggedFields("##size", ["X", "Y", "Z"], Theme.AxisU32, [sz.X, sz.Y, sz.Z], [false, false, false], "%.3f", out int si, out float sv))
        {
            if (si == 0) sz.X = sv; else if (si == 1) sz.Y = sv; else sz.Z = sv;
            SetBounds(ctx, b.Min, sz);
        }
    }

    /// <summary>Moves the selection so its bounds start at <paramref name="min"/> and scales it to <paramref name="size"/>.</summary>
    static void SetBounds(EditorContext ctx, Vector3 min, Vector3 size)
    {
        if (ctx.SelectionBounds() is not { } b) return;
        var oldSize = b.Max - b.Min;
        float Ratio(float n, float o) => o < 1e-4f || n < 1e-4f ? 1 : n / o;
        var scale = new Vector3(Ratio(size.X, oldSize.X), Ratio(size.Y, oldSize.Y), Ratio(size.Z, oldSize.Z));
        bool rigid = scale == Vector3.One;
        if (TransformGesture.Start(ctx.Session, rigid ? "Set Position" : "Set Size") is not { } g) return;
        g.Apply(TransformGesture.About(b.Min, Matrix4x4.CreateScale(scale)) * Matrix4x4.CreateTranslation(min - b.Min), rigid);
        g.Commit();
    }
}
