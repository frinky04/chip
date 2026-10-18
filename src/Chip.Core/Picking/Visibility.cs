using System.Numerics;
using Chip.Core.Scene;

namespace Chip.Core.Picking;

/// <summary>
/// Occlusion tests for "visible only" box selection: an element counts when the eye can see it past every other surface.
/// This is a ray-cast depth test on the CPU, so it agrees with picking (hidden meshes don't occlude; back faces occlude only
/// in backface mode, matching what the viewport draws).
/// </summary>
public static class Visibility
{
    /// <summary>True if nothing visible lies between the eye and <paramref name="point"/>.</summary>
    public static bool IsVisible(Document doc, Vector3 eye, Vector3 point, bool backfaces = false)
    {
        var d = point - eye;
        float dist = d.Length();
        if (dist < 1e-5f) return true;
        var hit = Picker.RaycastFace(doc, new Ray(eye, d / dist), backfaces);
        float slack = MathF.Max(1e-3f, dist * 1e-3f); // the element's own faces meet the ray at the element
        return hit is not { } h || h.Distance >= dist - slack;
    }

    /// <summary>
    /// Keeps the marquee results the eye can see. Vertices test their position, edges either end or the midpoint, faces their
    /// centre. Objects pass through unchanged.
    /// </summary>
    public static List<ElementRef> FilterVisible(Document doc, ViewInfo view, IEnumerable<ElementRef> picked, SelectMode mode, bool backfaces = false)
    {
        var result = new List<ElementRef>();
        foreach (var e in picked)
        {
            if (mode == SelectMode.Object || doc.Find(e.Mesh) is not { } m) { result.Add(e); continue; }
            bool Seen(Vector3 p) => IsVisible(doc, view.Eye, p, backfaces);
            bool visible = mode switch
            {
                SelectMode.Vertex => Seen(m.Positions[e.A]),
                SelectMode.Edge => Seen((m.Positions[e.A] + m.Positions[e.B]) * 0.5f) || Seen(m.Positions[e.A]) || Seen(m.Positions[e.B]),
                _ => Seen(m.FaceCenter(e.A)),
            };
            if (visible) result.Add(e);
        }
        return result;
    }
}
