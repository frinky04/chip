using System.Numerics;
using Chip.Core.Scene;
using Chip.Editor.Features.Materials;
using Chip.Editor.Framework;
using Chip.Editor.UI;
using ImGuiNET;
using static Chip.Editor.UI.Widgets;

namespace Chip.Editor.Inspector;

/// <summary>
/// The level's texel density and the active material (with its own density override), the selection's materials,
/// favourites, the Shift+Alt slots, Apply, and the browser button.
/// </summary>
public sealed class MaterialSection : IInspectorSection
{
    public string Id => "material";
    public int Order => 200;
    public bool Visible(EditorContext ctx) => true;

    readonly Rendering.FrameCache<List<(string Name, int Count)>> _selectionMaterials = new();

    public void Draw(EditorContext ctx)
    {
        var browser = MaterialBrowser.For(ctx);
        var active = ctx.Materials.Get(ctx.ActiveMaterial);
        Heading("Material", active.Tool != ToolMaterial.None ? "tool material" : null);

        // Active material: thumbnail, name, size and tiling, favourite star.
        float big = Theme.Px(44);
        var r = MaterialTiles.Tile(ctx, browser, active, big, caption: false);
        if (r.Clicked) browser.Open = true;
        ImGui.SameLine(0, Theme.Px(10));
        ImGui.BeginGroup();
        float star = ImGui.GetTextLineHeight();
        ImGui.Text(MaterialTiles.Fit(active.Name, ImGui.GetContentRegionAvail().X - star - Theme.Px(8)));
        ImGui.SameLine(ImGui.GetWindowContentRegionMax().X - star);
        if (MaterialTiles.StarButton("##fav", browser.IsFavourite(active.Name), star, browser.IsFavourite(active.Name) ? "Remove from favourites" : "Add to favourites"))
            browser.ToggleFavourite(active.Name);
        ImGui.TextColored(Theme.TextDim, $"{active.Width} × {active.Height} px");
        ImGui.EndGroup();

        DrawDensity(ctx, active);
        DrawSelectionMaterials(ctx, browser);
        DrawRow(ctx, browser, "Favourites", browser.Favourites.Where(ctx.Materials.Contains).Select(ctx.Materials.Get).ToList(),
            "Star a material to pin it here", slots: false);
        DrawRow(ctx, browser, "Slots", ctx.Materials.All.Take(10).ToList(), null, slots: true);

        ImGui.Dummy(Theme.Px(0, 2));
        bool canApply = !ctx.Sel.IsEmpty && ctx.Sel.Mode is SelectMode.Face or SelectMode.Object;
        ImGui.BeginDisabled(!canApply);
        if (ImGui.Button("Apply", new Vector2(Theme.Px(72), 0))) ctx.Commands.Execute("texture.apply");
        ImGui.EndDisabled();
        Tip(canApply ? "Apply the active material to the selection  (Shift+T)" : "Select faces or objects to apply  (Shift+T)");
        ImGui.SameLine(0, Theme.Px(6));
        if (ImGui.Button(browser.Open ? "Close browser" : "Browse", new Vector2(Theme.Px(120), 0))) ctx.Commands.Execute("materials.browser");
        Tip("Material browser: search, folders, favourites  (T)");
    }

    /// <summary>
    /// Texel density, Scythe style ("1 m is 64 px"): the level's, which every texture follows by default, and the active
    /// texture's own override. Changing the level's rescales the faces that follow it, as one undo step.
    /// </summary>
    void DrawDensity(EditorContext ctx, MaterialInfo active)
    {
        float level = ctx.Doc.TexelDensity;
        RowLabel("Level", "Texel density of this level: how many texture pixels cover a metre. Textures without their own density follow it; changing it rescales their faces.");
        if (DensityField("##levelDensity", level, out float newLevel))
        {
            int faces = 0;
            ctx.Session.Do("Texel Density", () => faces = TexelDensityOps.Change(ctx.Doc, ctx.Materials, newLevel));
            ctx.Status($"Level texel density: 1 m is {newLevel:0.##} px. {(faces == 1 ? "1 face" : $"{faces} faces")} rescaled to match");
        }

        bool custom = active.TexelDensity != null;
        bool canEdit = !MaterialLibrary.IsMissing(active) && active.Width > 0;
        RowLabel("Own", custom
            ? $"{active.Name} has its own density. Reset to follow the level."
            : $"{active.Name} follows the level. Change it to give this texture its own density (saved next to the texture).");
        float resetW = custom ? Theme.Px(60) + Theme.Px(6) : 0;
        ImGui.BeginDisabled(!canEdit);
        // Dimmed while the texture just follows the level; setting a value gives it its own density.
        if (DensityField("##textureDensity", active.TexelsPerMeter, out float own, resetW, dim: !custom))
            SetDensity(ctx, active, MathF.Abs(own - ctx.Materials.DefaultTexelsPerMeter) < 1e-3f ? null : own);
        ImGui.EndDisabled();
        if (custom)
        {
            ImGui.SameLine(0, Theme.Px(6));
            if (ImGui.Button("Reset", new Vector2(Theme.Px(60), 0))) SetDensity(ctx, active, null);
            Tip($"Follow the level again: 1 m is {ctx.Materials.DefaultTexelsPerMeter:0.##} px");
        }
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextDim);
        ImGui.Text($"Tiles every {Labels.Length(active.TileWidth)}{(custom ? " (own density)" : "")}");
        ImGui.PopStyleColor();
    }

    static void SetDensity(EditorContext ctx, MaterialInfo m, float? density)
    {
        bool saved = ctx.Materials.SetTexelDensity(m.Name, density);
        var now = ctx.Materials.Get(m.Name);
        if (!saved)
            ctx.Status($"Couldn't write {MaterialLibrary.SidecarName} in {now.Source}. The new size applies until Chip closes; check the folder is writable.", error: true);
        else
            ctx.Status($"{m.Name} tiles every {Labels.Length(now.TileWidth)}. Texture > Reset scale to material density updates existing faces");
    }

    /// <summary>The materials on the addressed faces; click one to make it active.</summary>
    void DrawSelectionMaterials(EditorContext ctx, MaterialBrowser browser)
    {
        if (!_selectionMaterials.IsCurrent(ctx.Session))
            _selectionMaterials.Store(ctx.Session, ctx.Sel.FaceSets(ctx.Doc).SelectMany(kv => kv.Value.Select(f => kv.Key.Faces[f].Material))
            .GroupBy(m => m, StringComparer.OrdinalIgnoreCase).Select(g => (Name: g.Key, Count: g.Count())).OrderByDescending(g => g.Count).ToList());
        var counts = _selectionMaterials.Value;
        if (counts.Count == 0) return;
        ImGui.Dummy(Theme.Px(0, 2));
        ImGui.TextColored(Theme.TextDim, counts.Count == 1 ? "Selection uses" : $"Selection uses {counts.Count}");
        Tip("Materials on the selected faces. Click one to make it active.");
        float cell = Theme.Px(32);
        float spacing = Theme.Px(6);
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
            ImGui.TextColored(Theme.TextDim, $"+{counts.Count - show}");
        }
    }

    /// <summary>A labelled row of tiles; wraps onto further rows. Slots carry their Shift+Alt digit.</summary>
    static void DrawRow(EditorContext ctx, MaterialBrowser browser, string label, List<MaterialInfo> items, string? empty, bool slots)
    {
        if (items.Count == 0 && empty == null) return;
        ImGui.Dummy(Theme.Px(0, 2));
        ImGui.TextColored(Theme.TextDim, label);
        if (slots)
        {
            ImGui.SameLine();
            ImGui.TextColored(Theme.TextDisabled, "Shift+Alt+1..0");
        }
        if (items.Count == 0)
        {
            ImGui.TextColored(Theme.TextDisabled, empty!);
            return;
        }
        float cell = Theme.Px(48), spacing = Theme.Px(6);
        int perRow = Math.Max(1, (int)((ImGui.GetContentRegionAvail().X + spacing) / (cell + spacing)));
        for (int i = 0; i < items.Count; i++)
        {
            if (i % perRow != 0) ImGui.SameLine(0, spacing);
            var t = MaterialTiles.Tile(ctx, browser, items[i], cell, caption: false, badge: slots ? ((i + 1) % 10).ToString() : null);
            if (t.Clicked) ctx.ActiveMaterial = items[i].Name;
            if (t.DoubleClicked) MaterialBrowser.ApplyToSelection(ctx, items[i].Name);
        }
    }
}
