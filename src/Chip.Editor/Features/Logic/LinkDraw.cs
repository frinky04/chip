using System.Numerics;
using Chip.Core.Scene;
using Chip.Editor.Features.Scene;
using Chip.Editor.Framework;
using Chip.Editor.UI;
using Chip.Core.Picking;
using ImGuiNET;

namespace Chip.Editor.Features.Logic;

/// <summary>
/// Lines from the selected entities to what they point at (cyan) and from what points at them (violet), each with an
/// arrowhead on the way and, while few, the property or output it goes through. An entity hovered in a reference list
/// gets a ring (<see cref="Highlight"/>), so you can see which one you're about to pick.
/// </summary>
public static class LinkDraw
{
    /// <summary>Past this many selected entities no links are drawn: a whole-level selection would be a hairball.</summary>
    const int SelectionLimit = 16;
    /// <summary>Labels only while this few links show, so they stay readable.</summary>
    const int LabelLimit = 12;

    public static readonly uint OutColor = ImGui.ColorConvertFloat4ToU32(Theme.Rgb(0x4CC9F0));
    public static readonly uint InColor = ImGui.ColorConvertFloat4ToU32(Theme.Rgb(0xB18CFF));
    const uint Shadow = 0xE6100F0E;

    /// <summary>An entity to ring this frame (set by the inspector while a list row is hovered), or null.</summary>
    public static int? Highlight { get; set; }

    public static void Draw(EditorContext ctx, ImDrawListPtr dl, Vector2 origin, ViewInfo view, IReadOnlyList<Entity> selected,
        IReadOnlyDictionary<int, EntityDrawGeometry> boxes, bool labels)
    {
        if (selected.Count is > 0 and <= SelectionLimit)
        {
            var links = new List<(Link Link, bool Out)>();
            foreach (var e in selected)
            {
                foreach (var l in EntityLinks.From(ctx, e.Id)) links.Add((l, true));
                foreach (var l in EntityLinks.To(ctx, e.Id)) links.Add((l, false));
            }
            bool showLabels = labels && links.Count <= LabelLimit;
            foreach (var (link, isOut) in links)
            {
                if (!boxes.TryGetValue(link.From, out var a) || !boxes.TryGetValue(link.To, out var b)) continue;
                Arrow(dl, origin, view, a.Center, b.Center, isOut ? OutColor : InColor, showLabels ? link.Via : null);
            }
        }
        if (Highlight is { } h && boxes.TryGetValue(h, out var box) && view.Project(box.Center, out var p))
        {
            float r = Theme.Px(Picker.EntityGlyphRadius + 5);
            dl.AddCircle(origin + p, r, Shadow, 0, Theme.Px(4));
            dl.AddCircle(origin + p, r, Theme.HoverU32, 0, Theme.Px(2));
        }
    }

    /// <summary>Clears <see cref="Highlight"/> once every pane has drawn it; the inspector sets it again while still hovered.</summary>
    public static void EndFrame() => Highlight = null;

    static void Arrow(ImDrawListPtr dl, Vector2 origin, ViewInfo v, Vector3 from, Vector3 to, uint col, string? label)
    {
        if (!v.Project(from, out var a) || !v.Project(to, out var b)) return;
        a += origin;
        b += origin;
        float len = Vector2.Distance(a, b);
        if (len < Theme.Px(4)) return;
        var dir = (b - a) / len;
        var side = new Vector2(-dir.Y, dir.X);
        float w = Theme.Px(1.5f);
        dl.AddLine(a, b, Shadow, w + Theme.Px(2));
        dl.AddLine(a, b, col, w);
        // The head sits past the middle, so it reads as "towards" even when the target's glyph is off screen.
        var head = a + dir * len * 0.6f;
        float s = Theme.Px(7);
        dl.AddTriangleFilled(head + dir * s * 0.5f, head - dir * s * 0.5f + side * s * 0.5f, head - dir * s * 0.5f - side * s * 0.5f, col);
        if (label == null) return;
        var size = ImGui.CalcTextSize(label) * (Fonts.SmallSize / Fonts.BodySize);
        var mid = (a + b) * 0.5f + side * Theme.Px(10) - size * 0.5f;
        mid = new Vector2(MathF.Round(mid.X), MathF.Round(mid.Y));
        dl.AddRectFilled(mid - Theme.Px(3, 1), mid + size + Theme.Px(3, 1), Shadow, Theme.Px(2));
        dl.AddText(Fonts.Small, Fonts.SmallSize, mid, col, label);
    }
}
