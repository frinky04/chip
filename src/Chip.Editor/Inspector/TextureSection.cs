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

    /// <summary>"  (Ctrl+Alt+F or Ctrl+Alt+Num5)": the command's current bindings, so tooltips follow rebinding.</summary>
    static string Keys(EditorContext ctx, string id) =>
        ctx.Commands.Find(id) is { Hotkeys.Length: > 0 } c ? $"  ({string.Join(" or ", c.Hotkeys.Select(h => h.ToString()))})" : "";

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
        if (!Heading("Texture", TextureOps.Plural(faces.Count))) return;

        int explicitCount = stats.ExplicitCount;
        RowLabel("UVs", "Explicit UVs (from unwraps, wraps or the fast texture tool) override the projection and stay on the geometry");
        string uvs = explicitCount == 0 ? "Projection" : explicitCount == faces.Count ? "Explicit" : $"{explicitCount} explicit, {faces.Count - explicitCount} projected";
        const string clear = "Clear";
        float clearW = explicitCount > 0 ? ImGui.CalcTextSize(clear).X + ImGui.GetStyle().FramePadding.X * 2 + ImGui.GetStyle().ItemSpacing.X : 0;
        string shown = Ellipsize(uvs, ImGui.GetContentRegionAvail().X - clearW); // the summary gives way to Clear
        ImGui.Text(shown);
        if (shown != uvs) Tip(uvs);
        if (explicitCount > 0)
        {
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
            // Scale is a multiple of the material's texel density: 1 = "1 m is N px" as set for the level or the texture.
            var densityOf = ctx.Materials.Get(faces[0].Face.Material);
            float k = densityOf.Scale;
            RowLabel("Scale", $"1 = the texture's density: 1 m is {densityOf.TexelsPerMeter:0.##} px");
            if (TaggedFields("##scale", ["U", "V"], UvTags, [m0.ScaleU / k, m0.ScaleV / k], [stats.MixedScaleU, stats.MixedScaleV], "%.3g", out int ci, out float cv))
                ctx.EditMapping("Texture Scale", ci == 0 ? m => m with { ScaleU = cv * k } : m => m with { ScaleV = cv * k });
            RowLabel("Rotate", "Degrees");
            if (TaggedFields("##rot", ["°"], UvTags, [m0.Rotation], [stats.MixedRotation], "%.1f", out _, out float rv))
                ctx.EditMapping("Texture Rotation", m => m with { Rotation = rv });
        }

        RowLabel("Align", "Project the texture again, or fit or flip it");
        CommandRow(ctx.Commands, "align", ("texture.alignGrid", "Grid", Icon.Grid), ("texture.alignFace", "Face", Icon.Face), ("texture.fit", "Fit", Icon.FitBoth),
            ("texture.flipU", "", Icon.FlipU), ("texture.flipV", "", Icon.FlipV));

        RowLabel("Justify", "Line the texture up with the faces' edges or centre");
        CommandRow(ctx.Commands, "justify", ("texture.justifyLeft", "", Icon.JustifyLeft), ("texture.justifyRight", "", Icon.JustifyRight),
            ("texture.justifyTop", "", Icon.JustifyTop), ("texture.justifyBottom", "", Icon.JustifyBottom), ("texture.justifyCenter", "", Icon.JustifyCenter));

        // Fit: which extent one tile fills, then the options that shape fit, justify and moves.
        RowLabel("Fit", $"What Fit{Keys(ctx, "texture.fit")} fills with one tile");
        int fit = ButtonRow("fitmode",
            new RowButton("", "Fit width and height", Icon: Icon.FitBoth, Selected: options.Fit == FitMode.Both),
            new RowButton("", "Fit width (walls: one tile across)", Icon: Icon.FitWidth, Selected: options.Fit == FitMode.Width),
            new RowButton("", "Fit height (walls: floor to ceiling)", Icon: Icon.FitHeight, Selected: options.Fit == FitMode.Height));
        if (fit >= 0) options.Fit = (FitMode)fit;
        RowLabel("Options", "How fit, justify, scale and moves treat the selection");
        int option = ButtonRow("options",
            new RowButton("", "Keep ratio: scale both axes together when fitting", Icon: Icon.KeepRatio, On: options.KeepRatio),
            new RowButton("", "Treat as one: fit, justify, scale and flip all faces as one region", Icon: Icon.TreatAsOne, On: options.TreatAsOne),
            new RowButton("", ctx.Session.TextureLock
                ? "Texture lock is on: projected textures follow moved objects"
                : "Texture lock is off: projected textures stay world-aligned when objects move", Icon: Icon.Lock, On: ctx.Session.TextureLock));
        if (option == 0) options.KeepRatio = !options.KeepRatio;
        if (option == 1) options.TreatAsOne = !options.TreatAsOne;
        if (option == 2) ctx.Commands.Execute("texture.lock");

        FieldStart();
        if (ImGui.Button("Fast texture tool", new Vector2(-1, 0))) ctx.SetTool("fastTexture");
        Tip("Edit UVs in 2D: unwrap, move, scale and rotate them  (Shift+Q)");
    }
}
