using System.Numerics;
using Chip.Core.Scene;
using Chip.Editor.Framework;
using Chip.Editor.UI;
using ImGuiNET;
using static Chip.Editor.UI.Widgets;

namespace Chip.Editor.Features.Logic;

/// <summary>
/// The selected entity's links: what it points at, and what points at it, which no single field shows. A row selects the
/// other entity on click and rings it in the viewport on hover. Shown only when there is a link.
/// </summary>
public sealed class LinksSection : IInspectorSection
{
    public string Id => "links";
    public int Order => 112;

    public bool Visible(EditorContext ctx) => ctx.Sel.Entities(ctx.Doc) is [var e]
        && (EntityLinks.From(ctx, e.Id).Count > 0 || EntityLinks.To(ctx, e.Id).Count > 0);

    public void Draw(EditorContext ctx)
    {
        var e = ctx.Sel.Entities(ctx.Doc)[0];
        var from = EntityLinks.From(ctx, e.Id);
        var to = EntityLinks.To(ctx, e.Id);
        if (!Heading("Links", $"{from.Count + to.Count}")) return;
        if (from.Count > 0) Rows(ctx, "Points at", from, outgoing: true);
        if (to.Count > 0) Rows(ctx, "Referenced by", to, outgoing: false);
    }

    static void Rows(EditorContext ctx, string title, IReadOnlyList<Link> links, bool outgoing)
    {
        ImGui.TextColored(Theme.TextDim, title);
        uint col = outgoing ? LinkDraw.OutColor : LinkDraw.InColor;
        float w = ImGui.GetContentRegionAvail().X;
        for (int i = 0; i < links.Count; i++)
        {
            var link = links[i];
            if (ctx.Doc.FindEntity(outgoing ? link.To : link.From) is not { } other) continue;
            ImGui.PushID($"{title}{i}");
            var start = ImGui.GetCursorScreenPos();
            float h = ImGui.GetFrameHeight();
            if (ImGui.Selectable("##row", false, ImGuiSelectableFlags.None, new Vector2(w, h))) EntityRefs.Select(ctx, other);
            if (ImGui.IsItemHovered()) LinkDraw.Highlight = other.Id;
            Tip($"{(outgoing ? $"{link.Via} points at" : $"Its {link.Via} points here:")} {SceneOps.DisplayName(other)}. Click to select it");
            var dl = ImGui.GetWindowDrawList();
            float y = start.Y + h * 0.5f;
            dl.AddCircleFilled(new Vector2(start.X + Theme.Px(6), y), Theme.Px(3), col);
            var name = SceneOps.DisplayName(other);
            float textY = start.Y + (h - ImGui.GetTextLineHeight()) * 0.5f;
            dl.AddText(new Vector2(start.X + Theme.Px(16), textY), ImGui.GetColorU32(ImGuiCol.Text), name);
            var via = Ellipsize(link.Via, w * 0.45f);
            float viaW = ImGui.CalcTextSize(via).X;
            dl.AddText(new Vector2(start.X + w - viaW - Theme.Px(4), textY), ImGui.ColorConvertFloat4ToU32(Theme.TextDim), via);
            ImGui.PopID();
        }
    }
}
