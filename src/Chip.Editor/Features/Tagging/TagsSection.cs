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
/// everything with the tag. "Add tag" opens the Ctrl+T popup.
/// </summary>
public sealed class TagsSection(TagsPopup popup) : IInspectorSection
{
    readonly Rendering.FrameCache<(List<(string Tag, int Count)> Tags, int Targets)> _cache = new();

    public string Id => "tags";
    public int Order => 150;
    public bool Visible(EditorContext ctx) => TagOps.CanTag(ctx.Sel.Mode) && !ctx.Sel.IsEmpty;

    public void Draw(EditorContext ctx)
    {
        if (!_cache.IsCurrent(ctx.Session))
            _cache.Store(ctx.Session, (TagOps.OnSelection(ctx.Doc, ctx.Sel), TagOps.TargetCount(ctx.Doc, ctx.Sel)));
        var (tags, targets) = _cache.Value;
        if (!Heading("Tags", ctx.Sel.Mode == SelectMode.Object ? "on meshes" : null)) return;

        string? remove = null, complete = null, selectAll = null;
        float right = ImGui.GetCursorScreenPos().X + ImGui.GetContentRegionAvail().X;
        var spacing = Theme.Px(6, 6);
        for (int i = 0; i < tags.Count; i++)
        {
            var (tag, count) = tags[i];
            var size = ChipSize(tag, count, targets);
            if (i > 0)
            {
                ImGui.SameLine(0, spacing.X);
                if (ImGui.GetCursorScreenPos().X + size.X > right) ImGui.NewLine();
            }
            switch (Chip(tag, count, targets, size))
            {
                case ChipClick.Remove: remove = tag; break;
                case ChipClick.Body when ImGui.GetIO().KeyCtrl: selectAll = tag; break;
                case ChipClick.Body when count < targets: complete = tag; break;
            }
        }
        if (tags.Count == 0)
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.TextDim, "No tags");
            ImGui.SameLine();
        }
        else ImGui.Dummy(new Vector2(0, spacing.Y * 0.5f));
        if (tags.Count == 0) ImGui.SetCursorPosX(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - AddWidth());
        if (ImGui.Button("Add tag", new Vector2(tags.Count == 0 ? AddWidth() : -1, 0))) popup.Open();
        Tip("Add or find tags: Ctrl+T. Dressing rules select faces and meshes by tag.");

        if (remove != null) TagsFeature.Set(ctx, remove, on: false);
        if (complete != null) TagsFeature.Set(ctx, complete, on: true);
        if (selectAll != null) TagsFeature.SelectAll(ctx, selectAll);
    }

    static float AddWidth() => ImGui.CalcTextSize("Add tag").X + ImGui.GetStyle().FramePadding.X * 4;

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
        dl.AddText(new Vector2(p.X + Theme.Px(10), ty), U(count < targets ? Theme.TextDim : Theme.Text), Label(tag, count, targets));
        var c = new Vector2(MathF.Round(p.X + size.X - xw * 0.5f - Theme.Px(2)), MathF.Round(p.Y + size.Y * 0.5f));
        float r = Theme.Px(3.5f);
        uint ink = U(xHovered ? Theme.Text : Theme.TextDim);
        dl.AddLine(c - new Vector2(r), c + new Vector2(r), ink, Theme.Px(1.5f));
        dl.AddLine(c + new Vector2(-r, r), c + new Vector2(r, -r), ink, Theme.Px(1.5f));
        return x ? ChipClick.Remove : body ? ChipClick.Body : ChipClick.None;
    }
}
