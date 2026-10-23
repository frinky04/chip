using System.Numerics;
using Chip.Core.Scene;
using Chip.Editor.Framework;
using Chip.Editor.UI;
using ImGuiNET;
using static Chip.Editor.UI.Widgets;

namespace Chip.Editor.Features.Tagging;

/// <summary>
/// Inspector block listing the selection's tags as chips that wrap. A chip on only part of the selection shows how much
/// ("wall 2/5"); clicking it adds the tag to the rest. The × removes the tag from the selection; Ctrl+click selects
/// everything with the tag. "+ Tag" opens the Ctrl+T popup.
/// </summary>
public sealed class TagsSection(TagsPopup popup) : IInspectorSection
{
    readonly Rendering.FrameCache<(List<(string Tag, int Count)> Tags, int Targets)> _cache = new();

    public string Id => "tags";
    public int Order => 700; // near the end with Dressing, which selects by tags: the surface comes first
    public bool Visible(EditorContext ctx) => TagOps.CanTag(ctx.Sel.Mode) && !ctx.Sel.IsEmpty;

    public void Draw(EditorContext ctx)
    {
        if (!_cache.IsCurrent(ctx.Session))
            _cache.Store(ctx.Session, (TagOps.OnSelection(ctx.Doc, ctx.Sel), TagOps.TargetCount(ctx.Doc, ctx.Sel)));
        var (tags, targets) = _cache.Value;
        if (!Heading("Tags", tags.Count == 0 ? "none" : null)) return;

        // The chips flow in the field column, wrapping back to it, with "+ Tag" as the last one.
        bool meshes = ctx.Sel.Mode == SelectMode.Object;
        RowLabel(meshes ? "Meshes" : "Faces", meshes ? "Tags on the selected meshes (object mode)" : "Tags on the selected faces");
        string? remove = null, complete = null, selectAll = null;
        float right = ImGui.GetCursorScreenPos().X + ImGui.GetContentRegionAvail().X;
        float gap = Theme.Px(4);
        void Flow(float width, bool first)
        {
            if (first) return;
            ImGui.SameLine(0, gap);
            if (ImGui.GetCursorScreenPos().X + width > right)
            {
                ImGui.NewLine();
                FieldStart();
            }
        }
        for (int i = 0; i < tags.Count; i++)
        {
            var (tag, count) = tags[i];
            var size = ChipSize(tag, count, targets);
            size.X = MathF.Min(size.X, right - ImGui.GetWindowPos().X - LabelColumn); // a long tag gets an ellipsis, never loses its ×
            Flow(size.X, i == 0);
            switch (Chip(tag, count, targets, size))
            {
                case ChipClick.Remove: remove = tag; break;
                case ChipClick.Body when ImGui.GetIO().KeyCtrl: selectAll = tag; break;
                case ChipClick.Body when count < targets: complete = tag; break;
            }
        }
        const string add = "+ Tag";
        Flow(ImGui.CalcTextSize(add).X + ImGui.GetStyle().FramePadding.X * 2, tags.Count == 0);
        if (ImGui.Button(add)) popup.Open();
        Tip("Add or find tags  (Ctrl+T). Dressing rules select faces and meshes by tag.");

        if (remove != null) TagsFeature.Set(ctx, remove, on: false);
        if (complete != null) TagsFeature.Set(ctx, complete, on: true);
        if (selectAll != null) TagsFeature.SelectAll(ctx, selectAll);
    }

    enum ChipClick { None, Body, Remove }

    static string Label(string tag, int count, int targets) => count < targets ? $"{tag} {count}/{targets}" : tag;

    static Vector2 ChipSize(string tag, int count, int targets) =>
        new(ImGui.CalcTextSize(Label(tag, count, targets)).X + Theme.Px(10) + Theme.Px(22), ImGui.GetFrameHeight());

    /// <summary>Rounded neutral chip: label, then a × target. Partial tags draw their label dimmed.</summary>
    static ChipClick Chip(string tag, int count, int targets, Vector2 size)
    {
        var p = ImGui.GetCursorScreenPos();
        float xw = Theme.Px(22);
        ImGui.PushID(tag);
        bool body = ImGui.InvisibleButton("##chip", size - new Vector2(xw, 0));
        bool bodyHovered = ImGui.IsItemHovered();
        if (bodyHovered)
            ImGui.SetTooltip(count < targets
                ? $"On {count} of {targets}. Click to add it to the rest; Ctrl+click selects everything tagged \"{tag}\"."
                : $"Ctrl+click selects everything tagged \"{tag}\".");
        ImGui.SameLine(0, 0);
        bool x = ImGui.InvisibleButton("##remove", new Vector2(xw, size.Y));
        bool xHovered = ImGui.IsItemHovered();
        if (xHovered) ImGui.SetTooltip($"Remove \"{tag}\" from the selection");
        ImGui.PopID();

        var dl = ImGui.GetWindowDrawList();
        uint U(Vector4 c) => ImGui.ColorConvertFloat4ToU32(c);
        dl.AddRectFilled(p, p + size, U(bodyHovered || xHovered ? Theme.FrameHover : Theme.Frame), size.Y * 0.5f);
        float ty = MathF.Round(p.Y + (size.Y - ImGui.GetTextLineHeight()) * 0.5f);
        dl.AddText(new Vector2(p.X + Theme.Px(10), ty), U(count < targets ? Theme.TextDim : Theme.Text),
            Ellipsize(Label(tag, count, targets), size.X - Theme.Px(10) - xw));
        var c = new Vector2(MathF.Round(p.X + size.X - xw * 0.5f - Theme.Px(2)), MathF.Round(p.Y + size.Y * 0.5f));
        float r = Theme.Px(3.5f);
        uint ink = U(xHovered ? Theme.Text : Theme.TextDim);
        dl.AddLine(c - new Vector2(r), c + new Vector2(r), ink, Theme.Px(1.5f));
        dl.AddLine(c + new Vector2(-r, r), c + new Vector2(r, -r), ink, Theme.Px(1.5f));
        return x ? ChipClick.Remove : body ? ChipClick.Body : ChipClick.None;
    }
}
