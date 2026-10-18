using System.Numerics;
using Chip.Core.Scene;
using Chip.Core.Texturing;
using Chip.Editor.Framework;
using Chip.Editor.UI;
using ImGuiNET;
using static Chip.Editor.UI.Widgets;

namespace Chip.Editor.Inspector;

/// <summary>The active material, a palette of the library, and Apply.</summary>
public sealed class MaterialSection : IInspectorSection
{
    public string Id => "material";
    public int Order => 200;
    public bool Visible(EditorContext ctx) => true;

    public void Draw(EditorContext ctx)
    {
        var active = ctx.Materials.Get(ctx.ActiveMaterial);
        Heading("Material");
        ImGui.Image(ctx.TextureId(active.Name), new Vector2(Theme.Px(44)));
        ImGui.SameLine(0, Theme.Px(10));
        ImGui.BeginGroup();
        ImGui.Text(active.Name);
        ImGui.TextColored(Theme.TextDim, $"{active.Width} px, tiles every {Labels.Length(active.Width * TextureMapping.DefaultScale)}");
        ImGui.EndGroup();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Active material: used by new blocks and by Apply");
        ImGui.Dummy(Theme.Px(0, 2));
        DrawPalette(ctx);

        bool canApply = !ctx.Sel.IsEmpty && ctx.Sel.Mode is SelectMode.Face or SelectMode.Object;
        var used = ctx.Sel.FaceSets(ctx.Doc).SelectMany(kv => kv.Value.Select(f => kv.Key.Faces[f].Material)).Distinct().ToList();
        ImGui.BeginDisabled(!canApply);
        if (ImGui.Button("Apply", new Vector2(Theme.Px(72), 0))) ctx.Commands.Execute("texture.apply");
        ImGui.EndDisabled();
        Tip(canApply ? "Apply the active material to the selection  (Shift+T)" : "Select faces or objects to apply  (Shift+T)");
        if (used.Count > 0)
        {
            ImGui.SameLine(0, Theme.Px(10));
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.TextDim, used.Count == 1 ? $"replaces {used[0]}" : $"replaces {used.Count} materials");
            Tip("The selection's current material");
        }
    }

    static void DrawPalette(EditorContext ctx)
    {
        float cell = Theme.Px(48), spacing = Theme.Px(6);
        int perRow = Math.Max(1, (int)((ImGui.GetContentRegionAvail().X + spacing) / (cell + spacing)));
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, Vector2.Zero);
        var dl = ImGui.GetWindowDrawList();
        var all = ctx.Materials.All;
        for (int i = 0; i < all.Count; i++)
        {
            var m = all[i];
            if (i % perRow != 0) ImGui.SameLine(0, spacing);
            ImGui.PushID(i);
            if (ImGui.ImageButton("##mat", ctx.TextureId(m.Name), new Vector2(cell))) ctx.ActiveMaterial = m.Name;
            ImGui.PopID();
            var (a, b) = (ImGui.GetItemRectMin(), ImGui.GetItemRectMax());
            bool isActive = string.Equals(m.Name, ctx.ActiveMaterial, StringComparison.OrdinalIgnoreCase);
            if (isActive) dl.AddRect(a - new Vector2(Theme.Px(2)), b + new Vector2(Theme.Px(2)), Theme.AccentU32, Theme.Px(4), ImDrawFlags.None, Theme.Px(2));
            else if (ImGui.IsItemHovered()) dl.AddRect(a - new Vector2(Theme.Px(1)), b + new Vector2(Theme.Px(1)), ImGui.ColorConvertFloat4ToU32(Theme.TextDim), Theme.Px(3));
            if (i < 10)
            {
                string digit = ((i + 1) % 10).ToString();
                var ds = ImGui.CalcTextSize(digit) * (Fonts.SmallSize / Fonts.BodySize);
                var badge = b - ds - Theme.Px(6, 3);
                dl.AddRectFilled(badge, b, ImGui.ColorConvertFloat4ToU32(Theme.Bg with { W = 0.85f }), Theme.Px(3), ImDrawFlags.RoundCornersTopLeft);
                dl.AddText(Fonts.Small, Fonts.SmallSize, badge + Theme.Px(3, 1), ImGui.ColorConvertFloat4ToU32(Theme.TextDim), digit);
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip($"{m.Name}\n{m.Width} × {m.Height} px" + (i < 10 ? $"\nShift+Alt+{(i + 1) % 10}" : ""));
        }
        ImGui.PopStyleVar();
    }
}
