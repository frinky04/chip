using System.Numerics;
using Chip.Core.Scene;
using Chip.Editor.Features.Materials;
using Chip.Editor.Framework;
using Chip.Editor.UI;
using ImGuiNET;
using static Chip.Editor.UI.Widgets;

namespace Chip.Editor.Inspector;

/// <summary>
/// The selection's materials (click one to make it active) and Apply for the active one. Picking materials, favourites,
/// slots and a texture's own density are in the Materials tab of the dock; the level's density is under Level.
/// </summary>
public sealed class MaterialSection : IInspectorSection
{
    public string Id => "material";
    public int Order => 200;
    public bool Visible(EditorContext ctx) => ctx.Sel.FaceSets(ctx.Doc).Count > 0;

    readonly Rendering.FrameCache<List<(string Name, int Count)>> _selectionMaterials = new();

    public void Draw(EditorContext ctx)
    {
        var browser = MaterialBrowser.For(ctx);
        if (!_selectionMaterials.IsCurrent(ctx.Session))
            _selectionMaterials.Store(ctx.Session, ctx.Sel.FaceSets(ctx.Doc).SelectMany(kv => kv.Value.Select(f => kv.Key.Faces[f].Material))
            .GroupBy(m => m, StringComparer.OrdinalIgnoreCase).Select(g => (Name: g.Key, Count: g.Count())).OrderByDescending(g => g.Count).ToList());
        var counts = _selectionMaterials.Value;
        if (!Heading("Material", counts.Count == 1 ? counts[0].Name : $"{counts.Count} materials")) return;

        float cell = ImGui.GetFrameHeight(), spacing = Theme.Px(4);
        RowLabel("Uses", "Materials on the selected faces. Click one to make it active.");
        int fit = Math.Max(1, (int)((ImGui.GetContentRegionAvail().X + spacing) / (cell + spacing)));
        int show = counts.Count > fit ? fit - 1 : counts.Count;
        for (int i = 0; i < show; i++)
        {
            if (i > 0) ImGui.SameLine(0, spacing);
            var m = ctx.Materials.Get(counts[i].Name);
            if (MaterialLibrary.IsMissing(m)) m = m with { Name = counts[i].Name };
            if (MaterialTiles.Tile(ctx, browser, m, cell, caption: false).Clicked) ctx.ActiveMaterial = counts[i].Name;
        }
        if (show < counts.Count)
        {
            ImGui.SameLine(0, spacing);
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.TextDim, $"+{counts.Count - show}");
        }

        // Apply the active material: what Shift+T does, named so it's clear what lands.
        var active = ctx.Materials.Get(ctx.ActiveMaterial);
        bool already = counts.Count == 1 && string.Equals(counts[0].Name, active.Name, StringComparison.OrdinalIgnoreCase);
        RowLabel("Active", "The material Apply (Shift+T) and new geometry use. Pick it in the Materials tab (T).");
        MaterialTiles.Tile(ctx, browser, active, cell, caption: false);
        ImGui.SameLine(0, spacing);
        ImGui.BeginDisabled(already);
        if (!already) PushPrimary();
        if (ImGui.Button($"Apply {MaterialTiles.Fit(active.Name, ImGui.GetContentRegionAvail().X - Theme.Px(70))}", new Vector2(-1, 0)))
            ctx.Commands.Execute("texture.apply");
        if (!already) PopPrimary();
        ImGui.EndDisabled();
        Tip(already ? "The selection already uses the active material. Pick another in Materials (T)." : "Apply the active material to the selection  (Shift+T)");
    }
}
