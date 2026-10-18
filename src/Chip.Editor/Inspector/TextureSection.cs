using Chip.Core.Scene;
using Chip.Core.Texturing;
using Chip.Editor.Framework;
using Chip.Editor.UI;
using ImGuiNET;
using static Chip.Editor.UI.Widgets;

namespace Chip.Editor.Inspector;

/// <summary>Planar mapping of the addressed faces: shift, scale, rotation, align/fit/flip. Differing values show "mixed".</summary>
public sealed class TextureSection : IInspectorSection
{
    static readonly uint[] UvTags = [0xFFA8A0A0, 0xFFA8A0A0];

    public string Id => "texture";
    public int Order => 300;
    public bool Visible(EditorContext ctx) => ctx.Sel.FaceSets(ctx.Doc).Count > 0;

    public void Draw(EditorContext ctx)
    {
        var maps = Mappings(ctx);
        if (maps.Count == 0) return;
        int faceCount = ctx.Sel.FaceSets(ctx.Doc).Sum(kv => kv.Value.Count);
        Heading("Texture", faceCount == 1 ? "1 face" : $"{faceCount} faces");

        var m0 = maps[0];
        bool Mixed(Func<TextureMapping, float> f) => maps.Any(m => f(m) != f(m0));

        RowLabel("Shift", "Offset in texels");
        if (TaggedFields("##shift", ["U", "V"], UvTags, [m0.ShiftU, m0.ShiftV], [Mixed(m => m.ShiftU), Mixed(m => m.ShiftV)], "%.1f", out int si, out float sv))
            ctx.EditMapping("Texture Shift", si == 0 ? m => m with { ShiftU = sv } : m => m with { ShiftV = sv });
        // Scale is shown as a multiple of the default density (1 = 512 px per metre).
        float k = TextureMapping.DefaultScale;
        RowLabel("Scale", "1 = 512 px per metre");
        if (TaggedFields("##scale", ["U", "V"], UvTags, [m0.ScaleU / k, m0.ScaleV / k], [Mixed(m => m.ScaleU), Mixed(m => m.ScaleV)], "%.3g", out int ci, out float cv))
            ctx.EditMapping("Texture Scale", ci == 0 ? m => m with { ScaleU = cv * k } : m => m with { ScaleV = cv * k });
        RowLabel("Rotate", "Degrees");
        if (TaggedFields("##rot", ["°"], UvTags, [m0.Rotation], [Mixed(m => m.Rotation)], "%.1f", out _, out float rv))
            ctx.EditMapping("Texture Rotation", m => m with { Rotation = rv });

        ImGui.Dummy(Theme.Px(0, 2));
        float gap = Theme.Px(4);
        float w = (ImGui.GetContentRegionAvail().X - gap * 4) / 5;
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new System.Numerics.Vector2(gap, Theme.Px(4)));
        if (ImGui.Button("Grid", new System.Numerics.Vector2(w, 0))) ctx.Commands.Execute("texture.alignGrid");
        Tip("Align to grid: project from the nearest world axis  (Ctrl+Shift+G)");
        ImGui.SameLine();
        if (ImGui.Button("Face", new System.Numerics.Vector2(w, 0))) ctx.Commands.Execute("texture.alignFace");
        Tip("Align to face: project along the face  (Ctrl+Shift+F)");
        ImGui.SameLine();
        if (ImGui.Button("Fit", new System.Numerics.Vector2(w, 0))) ctx.Commands.Execute("texture.fit");
        Tip("Fit: one tile across the face  (Ctrl+Alt+Num5)");
        ImGui.SameLine();
        if (IconButton("flipu", Icon.FlipU, "Flip horizontally (U)", w)) ctx.Commands.Execute("texture.flipU");
        ImGui.SameLine();
        if (IconButton("flipv", Icon.FlipV, "Flip vertically (V)", w)) ctx.Commands.Execute("texture.flipV");
        ImGui.PopStyleVar();
    }

    /// <summary>Mappings of every addressed face, the active face first.</summary>
    static List<TextureMapping> Mappings(EditorContext ctx)
    {
        var sel = ctx.Sel;
        var result = new List<TextureMapping>();
        if (sel.Mode == SelectMode.Face && sel.Active is { } a && ctx.Doc.Find(a.Mesh) is { } am && a.A < am.Faces.Count)
            result.Add(am.Faces[a.A].Map);
        foreach (var (mesh, faces) in sel.FaceSets(ctx.Doc))
            result.AddRange(faces.Select(f => mesh.Faces[f].Map));
        return result;
    }
}
