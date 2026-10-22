using System.Numerics;
using Chip.Core.Picking;
using Chip.Core.Scene;
using Chip.Editor.UI;
using ImGuiNET;

namespace Chip.Editor.Features.Scene;

/// <summary>World geometry shared across frames and panes until the session changes.</summary>
public sealed record EntityDrawGeometry(Vector3[] Corners, Vector3 Center, Vector3 Tip)
{
    public static EntityDrawGeometry Build(Entity e)
    {
        var cls = EntityClasses.Get(e.ClassName);
        var center = EntityOps.Center(e);
        float len = MathF.Max(0.5f, (cls.BoxMax - cls.BoxMin).Length() * 0.6f);
        return new(EntityOps.BoxCorners(e), center, center + EntityOps.Forward(e) * len);
    }
}

public enum EntityLook { Normal, Hovered, Selected, Ghost }

/// <summary>
/// How entities appear in the viewport: the class box as a wireframe, a forward arrow, and a round screen-space glyph with
/// the class icon at the box centre (the same spot <see cref="Picker.PickEntity"/> tests). Colours identify the class;
/// orange is reserved for the selection.
/// </summary>
public static class EntityDraw
{
    // Icons.Text creates a string on each call. Entity glyphs repeat hundreds of times per frame.
    static readonly Dictionary<Icon, string> Glyphs = new[] { Icon.User, Icon.Bulb, Icon.Lamp, Icon.Target, Icon.Box, Icon.Flag, Icon.MapPin }
        .ToDictionary(icon => icon, Icons.Text);

    static void DrawGlyph(ImDrawListPtr dl, Icon icon, Vector2 center, uint color)
    {
        string text = Glyphs[icon];
        ImGui.PushFont(Fonts.Icons);
        var size = ImGui.CalcTextSize(text);
        ImGui.PopFont();
        var pos = center - size * 0.5f;
        dl.AddText(Fonts.Icons, Fonts.IconSize, new Vector2(MathF.Round(pos.X), MathF.Round(pos.Y)), color, text);
    }

    const uint Shadow = 0xE6100F0E;

    public static (Icon Icon, uint Color) Style(string className) => className switch
    {
        "info_player_start" => (Icon.User, Rgb(0x5BB35B)),
        "light_point" => (Icon.Bulb, Rgb(0xF2C94C)),
        "light_spot" => (Icon.Lamp, Rgb(0xF2C94C)),
        "info_target" => (Icon.Target, Rgb(0xE5484D)),
        ReferenceModels.ClassName => (Icon.Ruler, Rgb(0xA3B1C2)),
        // The project's own scenes, and names no class defines.
        _ => EntityClasses.IsKnown(className) ? (Icon.Box, Rgb(0x3E8EED)) : (Icon.MapPin, Rgb(0xA0A0A8)),
    };

    static uint Rgb(uint hex) => ImGui.ColorConvertFloat4ToU32(Theme.Rgb(hex));

    static uint Fade(uint col, float alpha) => (col & 0x00FFFFFF) | ((uint)(((col >> 24) & 0xFF) * alpha) << 24);

    /// <summary>Draws one entity. <paramref name="occluded"/> dims it (a surface is in front of its centre).</summary>
    public static void Draw(ImDrawListPtr dl, Vector2 origin, ViewInfo v, Entity e, EntityLook look, bool occluded = false, bool locked = false, EntityDrawGeometry? geometry = null)
    {
        var (icon, classCol) = Style(e.ClassName);
        uint col = look switch
        {
            EntityLook.Selected or EntityLook.Ghost => Theme.AccentU32,
            EntityLook.Hovered => Theme.HoverU32,
            _ => classCol,
        };
        float alpha = look == EntityLook.Ghost ? 0.6f : occluded ? 0.4f : locked ? 0.55f : 1f;
        col = Fade(col, alpha);
        float w = Theme.Px(look == EntityLook.Selected ? 2f : 1.5f);

        // Class box.
        geometry ??= EntityDrawGeometry.Build(e);
        var c = geometry.Corners;
        ReadOnlySpan<int> edges = [0, 1, 2, 3, 4, 5, 6, 7, 0, 2, 1, 3, 4, 6, 5, 7, 0, 4, 1, 5, 2, 6, 3, 7];

        // A loaded reference model is drawn by the renderer; here it only gets its bounds when hovered, selected or being
        // placed, its origin (where the engine's node goes) and its label. No arrow: the model shows which way it faces.
        if (ReferenceModels.Get(e).Model != null)
        {
            if (look == EntityLook.Normal) return;
            for (int i = 0; i < edges.Length; i += 2) Line(dl, origin, v, c[edges[i]], c[edges[i + 1]], Fade(col, 0.7f), Theme.Px(1.25f));
            if (!v.Project(e.Position, out var ro)) return;
            dl.AddCircleFilled(origin + ro, Theme.Px(3.5f), Shadow);
            dl.AddCircleFilled(origin + ro, Theme.Px(2.5f), col);
            if (look is EntityLook.Hovered or EntityLook.Selected && v.Project(c[7], out var top))
                Label(dl, origin + new Vector2(ro.X, MathF.Min(ro.Y, top.Y) - Theme.Px(18)), SceneOps.DisplayName(e), col);
            return;
        }

        for (int i = 0; i < edges.Length; i += 2) Line(dl, origin, v, c[edges[i]], c[edges[i + 1]], col, w);

        // Forward arrow from the box centre, at least half a metre so it reads on small markers.
        var center = geometry.Center;
        var tip = geometry.Tip;
        Line(dl, origin, v, center, tip, col, w);
        if (v.Project(center, out var sc) && v.Project(tip, out var st) && Vector2.Distance(sc, st) > Theme.Px(6))
        {
            var dir = Vector2.Normalize(st - sc);
            var side = new Vector2(-dir.Y, dir.X);
            float head = Theme.Px(8);
            dl.AddTriangleFilled(origin + st, origin + st - dir * head + side * head * 0.5f, origin + st - dir * head - side * head * 0.5f, col);
        }

        // Origin dot and glyph.
        if (v.Project(e.Position, out var so)) dl.AddCircleFilled(origin + so, Theme.Px(2.5f), col);
        if (!v.Project(center, out var g)) return;
        g = origin + g;
        float r = Theme.Px(Picker.EntityGlyphRadius);
        dl.AddCircleFilled(g, r, Fade(Shadow, alpha));
        dl.AddCircle(g, r, col, 0, Theme.Px(look == EntityLook.Normal ? 1.5f : 2f));
        DrawGlyph(dl, icon, g, look == EntityLook.Normal ? Fade(classCol, alpha) : col);
        if (look is EntityLook.Hovered or EntityLook.Selected) Label(dl, g + new Vector2(0, r + Theme.Px(3)), SceneOps.DisplayName(e), col);
    }

    static void Label(ImDrawListPtr dl, Vector2 topCenter, string text, uint col)
    {
        var size = ImGui.CalcTextSize(text) * (Fonts.SmallSize / Fonts.BodySize);
        var p = new Vector2(MathF.Round(topCenter.X - size.X * 0.5f), MathF.Round(topCenter.Y));
        dl.AddRectFilled(p - Theme.Px(4, 1), p + size + Theme.Px(4, 1), Shadow, Theme.Px(2));
        dl.AddText(Fonts.Small, Fonts.SmallSize, p, col, text);
    }

    static void Line(ImDrawListPtr dl, Vector2 origin, ViewInfo v, Vector3 a, Vector3 b, uint col, float width)
    {
        if (v.Project(a, out var p) && v.Project(b, out var q)) dl.AddLine(origin + p, origin + q, col, width);
    }
}
