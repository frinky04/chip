using System.Numerics;
using Chip.Core.Geometry;
using Chip.Core.Picking;
using Chip.Editor.UI;
using ImGuiNET;

namespace Chip.Editor.Tools.Block;

/// <summary>Screen-space drawing shared by the creation tools: wire previews, points, and the size readout.</summary>
public static class Overlay
{
    public const uint Shadow = 0xE6100F0E;
    /// <summary>Accent at reduced alpha, for guides (bounds, soft edges, the closing edge of a polygon).</summary>
    public static uint Faint => (Theme.AccentU32 & 0x00FFFFFF) | 0x70000000;

    public static void Line(ImDrawListPtr dl, Vector2 origin, ViewInfo v, Vector3 a, Vector3 b, uint col, float width)
    {
        if (v.Project(a, out var p) && v.Project(b, out var q)) dl.AddLine(origin + p, origin + q, col, width);
    }

    public static void Dot(ImDrawListPtr dl, Vector2 origin, ViewInfo v, Vector3 c, bool hollow = false)
    {
        if (!v.Project(c, out var p)) return;
        dl.AddCircleFilled(origin + p, Theme.Px(4), Shadow);
        if (hollow) dl.AddCircle(origin + p, Theme.Px(3), Theme.AccentU32, 0, Theme.Px(1.5f));
        else dl.AddCircleFilled(origin + p, Theme.Px(2.5f), Theme.AccentU32);
    }

    /// <summary>Every edge of <paramref name="mesh"/>; soft (smoothed) edges are drawn faint so curved shapes read as surfaces.</summary>
    public static void Wire(ImDrawListPtr dl, Vector2 origin, ViewInfo v, PolyMesh mesh)
    {
        float w = Theme.Px(1.5f);
        foreach (var e in mesh.Edges())
        {
            bool soft = mesh.SoftEdges.Contains(e);
            Line(dl, origin, v, mesh.Positions[e.A], mesh.Positions[e.B], soft ? Faint : Theme.AccentU32, soft ? Theme.Px(1) : w);
        }
    }

    public static void Box(ImDrawListPtr dl, Vector2 origin, ViewInfo v, Vector3 min, Vector3 max, uint col, float width)
    {
        Span<Vector3> c = stackalloc Vector3[8];
        for (int i = 0; i < 8; i++)
            c[i] = new Vector3((i & 1) == 0 ? min.X : max.X, (i & 2) == 0 ? min.Y : max.Y, (i & 4) == 0 ? min.Z : max.Z);
        ReadOnlySpan<int> edges = [0, 1, 2, 3, 4, 5, 6, 7, 0, 2, 1, 3, 4, 6, 5, 7, 0, 4, 1, 5, 2, 6, 3, 7];
        for (int i = 0; i < edges.Length; i += 2) Line(dl, origin, v, c[edges[i]], c[edges[i + 1]], col, width);
    }

    /// <summary>A small label next to <paramref name="anchor"/>, clamped inside the viewport.</summary>
    public static void Label(ImDrawListPtr dl, Vector2 origin, ViewInfo v, Vector3 anchor, string text)
    {
        if (!v.Project(anchor, out var a)) return;
        var ts = ImGui.CalcTextSize(text);
        var pad = Theme.Px(6, 3);
        var pos = Vector2.Clamp(a + new Vector2(Theme.Px(10), -ts.Y - Theme.Px(12)), new Vector2(Theme.Px(6)), Vector2.Max(new Vector2(Theme.Px(6)), v.Size - ts - Theme.Px(14, 10)));
        dl.AddRectFilled(origin + pos - pad, origin + pos + ts + pad, 0xE0141416, Theme.Px(2));
        dl.AddText(origin + pos, Theme.AccentU32, text);
    }

    public static string Size(Vector3 size) => $"{size.X:0.###} × {size.Y:0.###} × {size.Z:0.###} m";
}
