using System.Numerics;
using Chip.Core.Hotspots;
using Chip.Editor.Features.Texturing;
using Chip.Editor.Framework;
using Chip.Editor.UI;
using ImGuiNET;
using static Chip.Editor.UI.Widgets;

namespace Chip.Editor.Features.Hotspots;

/// <summary>Inspector block under Texture: which hotspot applies, the apply buttons, and Scythe's apply options.</summary>
public sealed class HotspotSection : IInspectorSection
{
    readonly Rendering.FrameCache<List<string>> _materials = new();
    static readonly string[] MappingLabels = ["Automatic", "Square", "Conforming", "Follow active quads"];

    public string Id => "hotspot";
    public int Order => 310;

    public bool Visible(EditorContext ctx) =>
        ctx.Sel.FaceSets(ctx.Doc).Count > 0 && HotspotState.For(ctx).AllAssets().Count > 0;

    (HotspotAsset Asset, string Material)? ResolveForInspector(EditorContext ctx, HotspotState state)
    {
        if (state.AssetFor(ctx.ActiveMaterial) is { } active) return (active, ctx.ActiveMaterial);
        if (!_materials.IsCurrent(ctx.Session))
            _materials.Store(ctx.Session, TextureOps.Faces(ctx).Select(f => f.Face.Material).Distinct(StringComparer.Ordinal).ToList());
        // Assets and the editor's working copy remain live; only the selected material names are cached.
        foreach (var material in _materials.Value)
            if (state.AssetFor(material) is { } asset) return (asset, material);
        return null;
    }

    public void Draw(EditorContext ctx)
    {
        var state = HotspotState.For(ctx);
        var s = state.Settings;
        var target = ResolveForInspector(ctx, state);
        if (!Heading("Hotspot", target?.Asset.Name)) return;

        RowLabel("Uses", "Hotspots use the active material when it has a hotspot asset, else the selected face's");
        if (target is { } t) ImGui.Text(t.Material);
        else ImGui.TextColored(Theme.TextDim, "None with a hotspot asset");
        if (target == null) Tip($"{ctx.ActiveMaterial} has no hotspot asset. Pick a hotspot material in the browser (T), or make an asset in the hotspot editor");

        float gap = Theme.Px(4);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(gap, Theme.Px(4)));
        float w = (ImGui.GetContentRegionAvail().X - gap * 2) / 3;
        ImGui.BeginDisabled(target == null);
        if (ImGui.Button("Apply", new Vector2(w, 0))) ctx.Commands.Execute("hotspot.apply");
        Tip("Apply hotspot: fit each coplanar region into the best patch  (Shift+H)");
        ImGui.SameLine();
        if (ImGui.Button("Each face", new Vector2(w, 0))) ctx.Commands.Execute("hotspot.applyFaces");
        Tip("Hotspot individual faces: fit every face into its own patch  (Shift+F)");
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("Edit…", new Vector2(w, 0))) HotspotEditor.For(ctx).Show(ctx, target?.Asset);
        Tip("Open the hotspot editor  (Ctrl+Shift+H)");
        ImGui.PopStyleVar();

        RowLabel("Tiling", "Whether tiling patches (full-width strips) are used");
        int tiling = TextSegments("hstiling", [("No", "Never use tiling patches"), ("Allow", "Tiling patches compete with the rest; long strips get them"),
            ("Only", "Use tiling patches only")], (int)s.Tiling);
        if (tiling >= 0) s.Tiling = (HotspotTiling)tiling;

        RowLabel("Mapping", "How each region is flattened before it is fitted");
        int mapping = (int)s.Mapping;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.Combo("##hsmapping", ref mapping, MappingLabels, MappingLabels.Length)) s.Mapping = (HotspotMapping)mapping;
        Tip(s.Mapping switch
        {
            HotspotMapping.Automatic => "Square when the quads are close to square, else conforming",
            HotspotMapping.Square => "Every quad becomes an equal square",
            HotspotMapping.Conforming => "Keep every face's true shape",
            _ => "Straighten quad strips, starting from the active face",
        });

        bool noRandom = s.DisallowRandom;
        if (ImGui.Checkbox("Disallow random", ref noRandom)) s.DisallowRandom = noRandom;
        Tip("Never mirror or turn patches at random, even where their flags allow it");
        ImGui.SameLine(0, Theme.Px(12));
        bool keep = s.DontApplyMaterial;
        if (ImGui.Checkbox("Don't apply material", ref keep)) s.DontApplyMaterial = keep;
        Tip("Write hotspot UVs but keep each face's material");
    }


    /// <summary>A text-only on/off button: neutral raised fill when on (never orange), frame fill on hover.</summary>
    internal static bool TextToggle(string id, string label, string tip, bool on)
    {
        float pad = Theme.Px(8);
        var size = new Vector2(ImGui.CalcTextSize(label).X + pad * 2, ImGui.GetFrameHeight());
        var p = ImGui.GetCursorScreenPos();
        bool clicked = ImGui.InvisibleButton(id, size);
        bool hovered = ImGui.IsItemHovered();
        var dl = ImGui.GetWindowDrawList();
        if (on || hovered) dl.AddRectFilled(p, p + size, ImGui.ColorConvertFloat4ToU32(on ? Theme.FrameHover : Theme.Frame), Theme.Px(3));
        var ts = ImGui.CalcTextSize(label);
        dl.AddText(p + new Vector2(pad, MathF.Round((size.Y - ts.Y) * 0.5f)), ImGui.ColorConvertFloat4ToU32(on || hovered ? Theme.Text : Theme.TextDim), label);
        Tip(tip);
        return clicked;
    }
}
