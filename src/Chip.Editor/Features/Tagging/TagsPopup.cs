using System.Numerics;
using Chip.Core.Geometry;
using Chip.Core.Scene;
using Chip.Editor.Framework;
using Chip.Editor.UI;
using ImGuiNET;
using static Chip.Editor.UI.Widgets;

namespace Chip.Editor.Features.Tagging;

/// <summary>
/// Ctrl+T: a popup at the mouse listing the level's tags, filtered as you type. Each row shows whether the selection has
/// the tag (all, some, none) and how much of the level uses it. Enter or click adds the highlighted tag, or removes it if
/// the whole selection has it; typing a new valid name offers to add it. Ctrl+Enter or Ctrl+click selects everything
/// with the tag. Esc closes. Suggestions come from the level, so a typo shows up as a new tag rather than a silent mismatch.
/// </summary>
public sealed class TagsPopup
{
    const string PopupId = "##tagsPopup";

    bool _openRequested, _focus, _scroll;
    Vector2 _at;
    string _query = "";
    int _index, _inputGeneration;
    int _rowsFor = -1;
    string? _rowsQuery;
    List<Row> _rows = new();

    /// <summary>One line in the list: an existing tag with its counts, or the offer to add a new one.</summary>
    readonly record struct Row(string Tag, int With, int Total, TagUse Use, bool IsNew);

    /// <summary>Opens at the mouse next frame (or at <paramref name="query"/>, for captures).</summary>
    public void Open(string query = "")
    {
        _openRequested = true;
        _query = query;
    }

    public void Draw(EditorContext ctx)
    {
        if (_openRequested)
        {
            _openRequested = false;
            if (!ImGui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId))
            {
                _at = ImGui.GetMousePos();
                ImGui.OpenPopup(PopupId);
                _focus = true;
                _rowsFor = -1;
                _index = 0;
            }
        }
        var vp = ImGui.GetMainViewport();
        float width = Theme.Px(380);
        var pos = Vector2.Clamp(_at - new Vector2(width * 0.5f, Theme.Px(16)), vp.WorkPos + Theme.Px(8, 8), vp.WorkPos + vp.WorkSize - new Vector2(width + Theme.Px(8), Theme.Px(360)));
        ImGui.SetNextWindowPos(pos, ImGuiCond.Appearing);
        ImGui.SetNextWindowSize(new Vector2(width, 0));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Theme.Px(8, 8));
        if (!ImGui.BeginPopup(PopupId, ImGuiWindowFlags.NoMove))
        {
            ImGui.PopStyleVar();
            return;
        }
        bool justOpened = _focus;
        var sel = ctx.Sel;
        int targets = TagOps.TargetCount(ctx.Doc, sel);
        bool canTag = TagOps.CanTag(sel.Mode) && targets > 0;

        ImGui.Text("Tags");
        ImGui.SameLine();
        string scope = canTag ? sel.Mode.Count(targets) : "nothing selected";
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - ImGui.CalcTextSize(scope).X);
        ImGui.TextColored(Theme.TextDim, scope);

        if (_focus) { ImGui.SetKeyboardFocusHere(); _focus = false; }
        ImGui.SetNextItemWidth(-1);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, Theme.Px(8, 6));
        // A new id per cleared query: ImGui keeps its own buffer while a field is active, so clearing _query alone wouldn't show.
        ImGui.InputTextWithHint($"##tag{_inputGeneration}", canTag ? "Type a tag to add or find" : "Type a tag to find", ref _query, TagSet.MaxLength + 8);
        ImGui.PopStyleVar();
        Refresh(ctx);

        string typed = _query.Trim();
        string? error = null;
        if (typed.Length > 0 && TagSet.Normalize(typed) == null)
            TagSet.IsValid(string.Join('-', typed.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)), out error);
        if (error != null) ImGui.TextColored(Theme.Error, $"Can't use \"{typed}\": {error}");

        var io = ImGui.GetIO();
        string? act = null;
        bool select = false;
        int count = _rows.Count;
        if (count > 0)
        {
            int step = (ImGui.IsKeyPressed(ImGuiKey.DownArrow) ? 1 : 0) - (ImGui.IsKeyPressed(ImGuiKey.UpArrow) ? 1 : 0);
            if (step != 0) { _index = Math.Clamp(_index + step, 0, count - 1); _scroll = true; }
            if (ImGui.IsKeyPressed(ImGuiKey.Enter) || ImGui.IsKeyPressed(ImGuiKey.KeypadEnter))
            {
                act = _rows[_index].Tag;
                select = io.KeyCtrl;
            }
        }

        ImGui.Dummy(Theme.Px(0, 2));
        float rowH = ImGui.GetTextLineHeight() + Theme.Px(8);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, Vector2.Zero);
        ImGui.BeginChild("##tagRows", new Vector2(0, rowH * Math.Clamp(count, 1, 10)), ImGuiChildFlags.None, ImGuiWindowFlags.NoBackground);
        if (count == 0 && error == null)
            ImGui.TextColored(Theme.TextDim, typed.Length > 0 ? $"No tags match \"{typed}\"" : canTag ? "No tags in this level yet. Type one to add it." : "No tags in this level yet");
        for (int i = 0; i < count; i++)
            if (DrawRow(_rows[i], i, rowH, canTag))
            {
                act = _rows[i].Tag;
                select = io.KeyCtrl;
            }
        ImGui.EndChild();
        ImGui.PopStyleVar();

        ImGui.Dummy(Theme.Px(0, 4));
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextDim);
        KeyHints(canTag ? [("Enter", "add or remove"), ("Ctrl+Enter", "select all"), ("Esc", "close")] : [("Enter", "select all"), ("Esc", "close")]);
        ImGui.PopStyleColor();

        bool toggle = !justOpened && ctx.Commands.Find("tags.edit") is { } self && self.Hotkeys.Any(h => h.IsPressed());
        bool close = toggle || ImGui.IsKeyPressed(ImGuiKey.Escape);
        if (act != null)
        {
            if (select || !canTag) { TagsFeature.SelectAll(ctx, act); close = true; }
            else
            {
                TagsFeature.Toggle(ctx, act);
                _query = "";
                _inputGeneration++;
                _focus = true; // keep typing the next tag
            }
        }
        if (close) ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
        ImGui.PopStyleVar();
    }

    /// <summary>Rebuilds the rows when the level, selection or query changes: tags on the selection first, then the rest.</summary>
    void Refresh(EditorContext ctx)
    {
        int key = HashCode.Combine(ctx.Session.Version, ctx.Sel.Count, ctx.Sel.Mode, ctx.Sel.Active);
        if (key == _rowsFor && _rowsQuery == _query) return;
        bool queryChanged = _rowsQuery != _query;
        _rowsFor = key;
        _rowsQuery = _query;

        string q = _query.Trim().ToLowerInvariant();
        string? asTag = TagSet.Normalize(_query);
        var rows = new List<Row>();
        foreach (var use in TagOps.Usage(ctx.Doc))
        {
            if (q.Length > 0 && !use.Tag.Contains(q.Replace(' ', '-'), StringComparison.Ordinal)) continue;
            var (with, total) = TagOps.Coverage(ctx.Doc, ctx.Sel, use.Tag);
            rows.Add(new Row(use.Tag, with, total, use, false));
        }
        rows.Sort((a, b) =>
        {
            int onA = a.With > 0 ? 0 : 1, onB = b.With > 0 ? 0 : 1;
            if (onA != onB) return onA - onB;
            // An exact match goes first among its peers, so Enter on a full name picks that name.
            int exA = a.Tag == asTag ? 0 : 1, exB = b.Tag == asTag ? 0 : 1;
            return exA != exB ? exA - exB : string.CompareOrdinal(a.Tag, b.Tag);
        });
        if (asTag != null && !rows.Any(r => r.Tag == asTag) && TagOps.CanTag(ctx.Sel.Mode) && !ctx.Sel.IsEmpty)
            rows.Insert(0, new Row(asTag, 0, 0, default, true));
        _rows = rows;
        if (queryChanged || _index >= rows.Count) { _index = rows.FindIndex(r => r.Tag == asTag) is >= 0 and var i ? i : 0; _scroll = true; }
    }

    /// <summary>State box (all / some / none of the selection), name, level usage on the right. Returns true when clicked.</summary>
    bool DrawRow(Row row, int index, float rowH, bool canTag)
    {
        var p = ImGui.GetCursorScreenPos();
        float w = ImGui.GetContentRegionAvail().X;
        bool clicked = ImGui.InvisibleButton($"##tagRow{index}", new Vector2(w, rowH));
        bool hovered = ImGui.IsItemHovered();
        if (hovered && ImGui.GetIO().MouseDelta != Vector2.Zero) _index = index;
        bool selected = index == _index;
        if (selected && _scroll) { ImGui.SetScrollHereY(); _scroll = false; }

        var dl = ImGui.GetWindowDrawList();
        if (selected) SelectedRow(dl, p, p + new Vector2(w, rowH), Theme.Px(3));
        else if (hovered) dl.AddRectFilled(p, p + new Vector2(w, rowH), ImGui.ColorConvertFloat4ToU32(Theme.Frame), Theme.Px(3));

        float ty = MathF.Round(p.Y + (rowH - ImGui.GetTextLineHeight()) * 0.5f);
        float x = p.X + Theme.Px(8);
        if (canTag)
        {
            DrawState(dl, new Vector2(x, MathF.Round(p.Y + rowH * 0.5f)), row, selected);
            x += Theme.Px(20);
        }
        string name = row.IsNew ? $"Add \"{row.Tag}\"" : row.Tag;
        dl.AddText(new Vector2(x, ty), RowText(selected), name);

        string usage = row.IsNew ? "new tag" : Usage(row.Use);
        float uw = ImGui.CalcTextSize(usage).X;
        dl.AddText(new Vector2(p.X + w - uw - Theme.Px(8), ty), RowText(selected, dim: true), usage);
        if (hovered && !row.IsNew)
            ImGui.SetTooltip(canTag
                ? $"{(row.With == row.Total ? "Click to remove from" : "Click to add to")} the selection. Ctrl+click selects everything tagged \"{row.Tag}\"."
                : $"Click to select everything tagged \"{row.Tag}\".");
        return clicked;
    }

    static string Usage(TagUse u) => (u.Faces, u.Objects) switch
    {
        (> 0, > 0) => $"{SelectMode.Face.Count(u.Faces)}, {SelectMode.Object.Count(u.Objects)}",
        (> 0, _) => SelectMode.Face.Count(u.Faces),
        _ => SelectMode.Object.Count(u.Objects),
    };

    /// <summary>A small box: filled when the whole selection has the tag, a bar when some of it does, empty when none.</summary>
    static void DrawState(ImDrawListPtr dl, Vector2 centreLeft, Row row, bool onAccent)
    {
        float s = Theme.Px(12);
        var min = new Vector2(centreLeft.X, MathF.Round(centreLeft.Y - s * 0.5f));
        var max = min + new Vector2(s);
        uint ink = onAccent ? Theme.OnAccentU32 : Theme.AccentU32;
        uint frame = onAccent ? Theme.OnAccentU32 : ImGui.ColorConvertFloat4ToU32(Theme.TextDim);
        if (row.Total > 0 && row.With == row.Total) dl.AddRectFilled(min, max, ink, Theme.Px(2));
        else
        {
            dl.AddRect(min, max, frame, Theme.Px(2), ImDrawFlags.None, Theme.Px(1));
            if (row.With > 0) dl.AddRectFilled(min + new Vector2(Theme.Px(3), s * 0.5f - Theme.Px(1)), max - new Vector2(Theme.Px(3), s * 0.5f - Theme.Px(1)), ink);
        }
    }
}
