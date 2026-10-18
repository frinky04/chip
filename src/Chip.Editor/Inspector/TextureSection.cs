using System.Numerics;
using Chip.Core.Scene;
using Chip.Core.Texturing;
using Chip.Editor.Features.Texturing;
using Chip.Editor.Framework;
using Chip.Editor.UI;
using ImGuiNET;
using static Chip.Editor.UI.Widgets;

namespace Chip.Editor.Inspector;

/// <summary>
/// Texture of the addressed faces: whether they use the planar projection or explicit UVs, the projection's shift /
/// scale / rotation (differing values show "mixed"), align / fit / flip, justify, fit options and texture lock.
/// </summary>
public sealed class TextureSection : IInspectorSection
{
    static readonly uint[] UvTags = [0xFFA8A0A0, 0xFFA8A0A0];

    readonly Rendering.FrameCache<TextureFrameStats> _faces = new();

    TextureFrameStats Stats(EditorContext ctx)
    {
        if (!_faces.IsCurrent(ctx.Session)) _faces.Store(ctx.Session, TextureFrameStats.Build(TextureOps.Faces(ctx)));
        return _faces.Value;
    }

    public string Id => "texture";
    public int Order => 300;
    public bool Visible(EditorContext ctx) => Stats(ctx).Faces.Count > 0;

    public void Draw(EditorContext ctx)
    {
        var stats = Stats(ctx);
        var faces = stats.Faces;
        if (faces.Count == 0) return;
        var options = TextureOptions.For(ctx);
        Heading("Texture", TextureOps.Plural(faces.Count));

        int explicitCount = stats.ExplicitCount;
        RowLabel("UVs", "Explicit UVs (from unwraps, wraps or the fast texture tool) override the projection and stay on the geometry");
        ImGui.Text(explicitCount == 0 ? "Projection" : explicitCount == faces.Count ? "Explicit" : $"{explicitCount} explicit, {faces.Count - explicitCount} projected");
        if (explicitCount > 0)
        {
            const string clear = "Clear";
            ImGui.SameLine(ImGui.GetWindowContentRegionMax().X - ImGui.CalcTextSize(clear).X - ImGui.GetStyle().FramePadding.X * 2);
            if (ImGui.Button(clear)) ctx.Commands.Execute("texture.clearUVs");
            Tip("Clear explicit UVs: go back to the planar projection");
        }

        var m0 = stats.First;
        if (explicitCount == faces.Count)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextDim);
            ImGui.TextWrapped("Explicit UVs override the projection. Edit them with the numpad keys or the fast texture tool.");
            ImGui.PopStyleColor();
        }
        else
        {
            RowLabel("Shift", "Offset in texels");
            if (TaggedFields("##shift", ["U", "V"], UvTags, [m0.ShiftU, m0.ShiftV], [stats.MixedShiftU, stats.MixedShiftV], "%.1f", out int si, out float sv))
                ctx.EditMapping("Texture Shift", si == 0 ? m => m with { ShiftU = sv } : m => m with { ShiftV = sv });
            // Scale is shown as a multiple of the default density (1 = 512 px per metre).
            float k = TextureMapping.DefaultScale;
            RowLabel("Scale", "1 = 512 px per metre");
            if (TaggedFields("##scale", ["U", "V"], UvTags, [m0.ScaleU / k, m0.ScaleV / k], [stats.MixedScaleU, stats.MixedScaleV], "%.3g", out int ci, out float cv))
                ctx.EditMapping("Texture Scale", ci == 0 ? m => m with { ScaleU = cv * k } : m => m with { ScaleV = cv * k });
            RowLabel("Rotate", "Degrees");
            if (TaggedFields("##rot", ["°"], UvTags, [m0.Rotation], [stats.MixedRotation], "%.1f", out _, out float rv))
                ctx.EditMapping("Texture Rotation", m => m with { Rotation = rv });
        }

        ImGui.Dummy(Theme.Px(0, 2));
        float gap = Theme.Px(4);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(gap, Theme.Px(4)));
        float w = (ImGui.GetContentRegionAvail().X - gap * 4) / 5;
        if (ImGui.Button("Grid", new Vector2(w, 0))) ctx.Commands.Execute("texture.alignGrid");
        Tip("Align to grid: project from the nearest world axis  (Ctrl+Shift+G)");
        ImGui.SameLine();
        if (ImGui.Button("Face", new Vector2(w, 0))) ctx.Commands.Execute("texture.alignFace");
        Tip("Align to face: project along the face  (Ctrl+Shift+F)");
        ImGui.SameLine();
        if (ImGui.Button("Fit", new Vector2(w, 0))) ctx.Commands.Execute("texture.fit");
        Tip("Fit: one tile across the faces, using the fit options below  (Ctrl+Alt+Num5)");
        ImGui.SameLine();
        if (IconButton("flipu", Icon.FlipU, "Flip horizontally  (Alt+D)", w)) ctx.Commands.Execute("texture.flipU");
        ImGui.SameLine();
        if (IconButton("flipv", Icon.FlipV, "Flip vertically  (Alt+E)", w)) ctx.Commands.Execute("texture.flipV");

        // Justify: five equal icon buttons after the label column.
        RowLabel("Justify", "Line the texture up with the faces' edges or centre");
        float jw = (ImGui.GetContentRegionAvail().X - gap * 4) / 5;
        (Icon Icon, string Id, string Tip)[] justify =
        [
            (Icon.JustifyLeft, "texture.justifyLeft", "Justify left  (Alt+Num4)"), (Icon.JustifyRight, "texture.justifyRight", "Justify right  (Alt+Num6)"),
            (Icon.JustifyTop, "texture.justifyTop", "Justify top  (Alt+Num8)"), (Icon.JustifyBottom, "texture.justifyBottom", "Justify bottom  (Alt+Num2)"),
            (Icon.JustifyCenter, "texture.justifyCenter", "Justify centre  (Alt+Num5)"),
        ];
        for (int i = 0; i < justify.Length; i++)
        {
            if (i > 0) ImGui.SameLine();
            if (IconButton(justify[i].Id, justify[i].Icon, justify[i].Tip, jw)) ctx.Commands.Execute(justify[i].Id);
        }
        ImGui.PopStyleVar();

        // Fit options: which extent Fit fills, then Keep Ratio and Treat as One.
        RowLabel("Fit", "What Fit (Ctrl+Alt+Num5) fills with one tile");
        int fit = SegmentedControl("fitmode",
        [
            (Icon.FitBoth, "", "Fit width and height"), (Icon.FitWidth, "", "Fit width (walls: one tile across)"),
            (Icon.FitHeight, "", "Fit height (walls: floor to ceiling)"),
        ], (int)options.Fit);
        if (fit >= 0) options.Fit = (FitMode)fit;
        ImGui.SameLine(0, Theme.Px(6));
        if (Toggle("keepratio", Icon.KeepRatio, "", "Keep ratio: scale both axes together when fitting", options.KeepRatio)) options.KeepRatio = !options.KeepRatio;
        ImGui.SameLine(0, Theme.Px(2));
        if (Toggle("asone", Icon.TreatAsOne, "", "Treat as one: fit, justify, scale and flip all faces as one region", options.TreatAsOne)) options.TreatAsOne = !options.TreatAsOne;
        ImGui.SameLine(0, Theme.Px(2));
        if (Toggle("lock", Icon.Lock, "", ctx.Session.TextureLock
                ? "Texture lock is on: projected textures follow moved objects"
                : "Texture lock is off: projected textures stay world-aligned when objects move", ctx.Session.TextureLock))
            ctx.Commands.Execute("texture.lock");

        ImGui.Dummy(Theme.Px(0, 2));
        if (ImGui.Button("Edit UVs in the fast texture tool", new Vector2(-1, 0))) ctx.SetTool("fastTexture");
        Tip("Fast texture tool: unwrap, move, scale and rotate UVs in 2D  (Shift+Q)");
    }
}
