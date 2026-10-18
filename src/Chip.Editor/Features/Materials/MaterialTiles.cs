using System.Numerics;
using Chip.Core.Scene;
using Chip.Editor.Framework;
using Chip.Editor.UI;
using ImGuiNET;

namespace Chip.Editor.Features.Materials;

/// <summary>What happened to a material tile this frame.</summary>
public readonly record struct TileResult(bool Clicked, bool DoubleClicked, bool Hovered, bool Dragging, bool ContextMenu);

/// <summary>Thumbnail tiles shared by the browser, the inspector's Material section and the replace dialog.</summary>
public static class MaterialTiles
{
    static uint U32(Vector4 c) => ImGui.ColorConvertFloat4ToU32(c);

    /// <summary>
    /// A square material tile at the cursor: thumbnail fitted by aspect (placeholder while it loads), orange ring when it
    /// is the active material, a star when it is a favourite, and an optional name caption below.
    /// </summary>
    public static TileResult Tile(EditorContext ctx, MaterialBrowser browser, MaterialInfo m, float size, bool caption, string? badge = null)
    {
        ImGui.PushID(m.Name);
        float captionH = caption ? ImGui.GetTextLineHeight() * (Fonts.SmallSize / Fonts.BodySize) + Theme.Px(4) : 0;
        var p = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton("##tile", new Vector2(size, size + captionH), ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight);
        bool hovered = ImGui.IsItemHovered();
        var result = new TileResult(
            Clicked: ImGui.IsItemClicked(ImGuiMouseButton.Left),
            DoubleClicked: hovered && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left),
            Hovered: hovered,
            Dragging: ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left, Theme.Px(6)),
            ContextMenu: ImGui.IsItemClicked(ImGuiMouseButton.Right));

        if (ImGui.IsItemVisible())
        {
            var dl = ImGui.GetWindowDrawList();
            var a = p;
            var b = p + new Vector2(size);
            dl.AddRectFilled(a, b, U32(Theme.Frame), Theme.Px(3));
            nint tex = browser.Thumbs.Get(ctx, m);
            if (tex != 0)
            {
                float aspect = m.Height > 0 ? (float)m.Width / m.Height : 1;
                var inner = aspect >= 1 ? new Vector2(size, size / aspect) : new Vector2(size * aspect, size);
                var o = a + (new Vector2(size) - inner) * 0.5f;
                dl.AddImageRounded(tex, o, o + inner, Vector2.Zero, Vector2.One, 0xFFFFFFFF, Theme.Px(3));
            }
            bool active = string.Equals(m.Name, ctx.ActiveMaterial, StringComparison.OrdinalIgnoreCase);
            if (active) dl.AddRect(a - new Vector2(Theme.Px(2)), b + new Vector2(Theme.Px(2)), Theme.AccentU32, Theme.Px(4), ImDrawFlags.None, Theme.Px(2));
            else if (hovered) dl.AddRect(a - new Vector2(Theme.Px(1)), b + new Vector2(Theme.Px(1)), U32(Theme.TextDim), Theme.Px(3));

            if (browser.IsFavourite(m.Name))
            {
                float r = MathF.Max(Theme.Px(5), size * 0.08f);
                var c = new Vector2(b.X - r - Theme.Px(4), a.Y + r + Theme.Px(4));
                dl.AddCircleFilled(c, r * 1.45f, U32(Theme.Bg with { W = 0.8f }));
                Star(dl, c, r, filled: true, U32(Theme.Text));
            }
            if (badge != null)
            {
                var ds = ImGui.CalcTextSize(badge) * (Fonts.SmallSize / Fonts.BodySize);
                var bp = b - ds - Theme.Px(6, 3);
                dl.AddRectFilled(bp, b, U32(Theme.Bg with { W = 0.85f }), Theme.Px(3), ImDrawFlags.RoundCornersTopLeft);
                dl.AddText(Fonts.Small, Fonts.SmallSize, bp + Theme.Px(3, 1), U32(Theme.TextDim), badge);
            }
            if (caption)
            {
                string text = Fit(m.Name, size, Fonts.SmallSize / Fonts.BodySize);
                var ts = ImGui.CalcTextSize(text) * (Fonts.SmallSize / Fonts.BodySize);
                var tp = new Vector2(a.X + (size - ts.X) * 0.5f, b.Y + Theme.Px(2));
                dl.AddText(Fonts.Small, Fonts.SmallSize, new Vector2(MathF.Round(tp.X), MathF.Round(tp.Y)), U32(active ? Theme.Text : Theme.TextDim), text);
            }
        }
        if (hovered && !result.Dragging) Tooltip(ctx, m);
        ImGui.PopID();
        return result;
    }

    /// <summary>Name, size, tiling and (for tool materials) what they do on export.</summary>
    public static void Tooltip(EditorContext ctx, MaterialInfo m)
    {
        ImGui.BeginTooltip();
        ImGui.PushFont(Fonts.Strong);
        ImGui.Text(m.Name);
        ImGui.PopFont();
        ImGui.TextColored(Theme.TextDim, $"{m.Width} × {m.Height} px");
        string tiling = $"Tiles every {Labels.Length(m.TileWidth)}" + (m.TileHeight != m.TileWidth ? $" × {Labels.Length(m.TileHeight)}" : "");
        ImGui.TextColored(Theme.TextDim, m.TexelDensity != null ? $"{tiling} (custom density)" : tiling);
        if (m.Folder.Length > 0) ImGui.TextColored(Theme.TextDim, $"Folder: {m.Folder}");
        if (ToolDescription(m.Tool) is { } tool) ImGui.TextColored(Theme.TextDim, tool);
        ImGui.EndTooltip();
    }

    public static string? ToolDescription(ToolMaterial tool) => tool switch
    {
        ToolMaterial.NoDraw => "Tool material: not rendered on export, still solid",
        ToolMaterial.Clip => "Tool material: collision only on export",
        ToolMaterial.Trigger => "Tool material: exported as a trigger volume",
        ToolMaterial.Skip => "Tool material: left out of the export",
        _ => null,
    };

    /// <summary>Truncates with an ellipsis to fit <paramref name="width"/> at the given font scale.</summary>
    public static string Fit(string text, float width, float scale = 1)
    {
        if (ImGui.CalcTextSize(text).X * scale <= width) return text;
        for (int n = text.Length - 1; n > 1; n--)
        {
            var t = text[..n] + "…";
            if (ImGui.CalcTextSize(t).X * scale <= width) return t;
        }
        return "…";
    }

    /// <summary>Five-pointed star (filled from triangles plus the convex inner pentagon, since ImGui fills only convex shapes).</summary>
    public static void Star(ImDrawListPtr dl, Vector2 c, float r, bool filled, uint col)
    {
        Span<Vector2> pts = stackalloc Vector2[10];
        for (int i = 0; i < 10; i++)
        {
            float ang = -MathF.PI / 2 + i * MathF.PI / 5;
            float rad = i % 2 == 0 ? r : r * 0.45f;
            pts[i] = c + new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * rad;
        }
        if (filled)
        {
            for (int i = 0; i < 10; i += 2) dl.AddTriangleFilled(pts[(i + 9) % 10], pts[i], pts[i + 1], col);
            for (int i = 1; i < 9; i += 2) dl.AddTriangleFilled(pts[1], pts[i], pts[i + 2], col);
        }
        else
            for (int i = 0; i < 10; i++) dl.AddLine(pts[i], pts[(i + 1) % 10], col, Theme.Px(1.2f));
    }

    /// <summary>A small star toggle button (favourite on/off) at the cursor.</summary>
    public static bool StarButton(string id, bool on, float size, string tip)
    {
        var p = ImGui.GetCursorScreenPos();
        bool clicked = ImGui.InvisibleButton(id, new Vector2(size));
        bool hovered = ImGui.IsItemHovered();
        var col = on ? Theme.Text : hovered ? Theme.Text : Theme.TextDim;
        Star(ImGui.GetWindowDrawList(), p + new Vector2(size * 0.5f), size * 0.42f, on, U32(col));
        Widgets.Tip(tip);
        return clicked;
    }
}
