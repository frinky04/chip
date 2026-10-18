using System.Numerics;
using Chip.Core.Scene;
using Chip.Editor.Features.Materials;
using Chip.Editor.Framework;
using Chip.Editor.UI;
using ImGuiNET;
using static Chip.Editor.UI.Widgets;

namespace Chip.Editor.Inspector;

/// <summary>
/// The active material (with its tile size, editable as a per-material density override), the selection's materials,
/// favourites, the Shift+Alt slots, Apply, and the browser button.
/// </summary>
public sealed class MaterialSection : IInspectorSection
{
    public string Id => "material";
    public int Order => 200;
    public bool Visible(EditorContext ctx) => true;

    readonly Rendering.FrameCache<List<(string Name, int Count)>> _selectionMaterials = new();
    float _tileEdit;
    string? _tileFor;

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

        DrawTileSize(ctx, active);
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

    /// <summary>"Tiles every" in metres. Editing it sets the material's texel density; the default size clears the override.</summary>
    void DrawTileSize(EditorContext ctx, MaterialInfo active)
    {
        if (!string.Equals(_tileFor, active.Name, StringComparison.OrdinalIgnoreCase) || !ImGui.IsAnyItemActive())
        {
            _tileFor = active.Name;
            _tileEdit = active.TileWidth;
        }
        bool custom = active.TexelDensity != null;
        RowLabel("Tiles", "Metres covered by one repeat. New blocks use it; Texture > Reset scale to material density applies it to faces.");
        float resetW = custom ? Theme.Px(60) + Theme.Px(6) : 0;
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - resetW);
        bool canEdit = active != MaterialLibrary.Missing && active.Width > 0;
        ImGui.BeginDisabled(!canEdit);
        if (ImGui.InputFloat("##tile", ref _tileEdit, 0, 0, custom ? "every %.3g m (custom)" : "every %.3g m", ImGuiInputTextFlags.EnterReturnsTrue)
            && _tileEdit > 1e-3f)
        {
            float density = active.Width / _tileEdit;
            bool isDefault = MathF.Abs(density - Core.Texturing.TextureMapping.TexelsPerMeter) < 1e-3f;
            SetDensity(ctx, active, isDefault ? null : density);
        }
        ImGui.EndDisabled();
        if (custom)
        {
            ImGui.SameLine(0, Theme.Px(6));
            if (ImGui.Button("Reset", new Vector2(Theme.Px(60), 0))) SetDensity(ctx, active, null);
            Tip($"Back to {Core.Texturing.TextureMapping.TexelsPerMeter:0} px per metre (tiles every {Labels.Length(active.Width / Core.Texturing.TextureMapping.TexelsPerMeter)})");
        }
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
            if (m == MaterialLibrary.Missing) m = m with { Name = counts[i].Name };
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
