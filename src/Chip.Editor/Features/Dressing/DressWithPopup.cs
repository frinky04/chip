using System.Numerics;
using Chip.Core.Dressing;
using Chip.Core.Scene;
using Chip.Editor.Framework;
using Chip.Editor.UI;
using ImGuiNET;
using static Chip.Editor.UI.Widgets;

namespace Chip.Editor.Features.Dressing;

/// <summary>
/// Edit > Dress with (Ctrl+Shift+T): a popup at the mouse listing the level's rules and the recipes it doesn't have yet.
/// Picking one tags the selection so the rule takes it (adding the rule from its recipe first when needed), in one undo
/// step; picking a rule the whole selection already has removes the tag. Face mode tags the selected faces; object mode
/// tags the faces of each selected mesh the rule can use (walls, not their tops), or the meshes themselves for railings and
/// stairs. Hovering a row highlights what the rule already takes.
/// </summary>
public sealed class DressWithPopup
{
    const string PopupId = "##dressWith";
    bool _openRequested, _focus;
    Vector2 _at;
    string _query = "";
    int _index;

    readonly record struct Row(string Label, string Detail, RuleEntry? Rule, Recipe? Recipe, int With, int Total);

    public void Open() => _openRequested = true;

    public void Draw(EditorContext ctx, DressingFeature feature)
    {
        if (_openRequested)
        {
            _openRequested = false;
            if (!ImGui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId))
            {
                _at = ImGui.GetMousePos();
                _query = "";
                _index = 0;
                _focus = true;
                ImGui.OpenPopup(PopupId);
            }
        }
        var vp = ImGui.GetMainViewport();
        float width = Theme.Px(400);
        var pos = Vector2.Clamp(_at - new Vector2(width * 0.5f, Theme.Px(16)), vp.WorkPos + Theme.Px(8, 8), vp.WorkPos + vp.WorkSize - new Vector2(width + Theme.Px(8), Theme.Px(380)));
        ImGui.SetNextWindowPos(pos, ImGuiCond.Appearing);
        ImGui.SetNextWindowSize(new Vector2(width, 0));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Theme.Px(8, 8));
        if (!ImGui.BeginPopup(PopupId, ImGuiWindowFlags.NoMove))
        {
            ImGui.PopStyleVar();
            if (feature.HighlightRule != null && !feature.Window.Open) feature.HighlightRule = null;
            return;
        }
        bool justOpened = _focus;
        ImGui.Text("Dress with");
        ImGui.SameLine();
        string scope = ctx.Sel.IsEmpty ? "nothing selected" : ctx.Sel.Mode.Count(TagOps.TargetCount(ctx.Doc, ctx.Sel));
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - ImGui.CalcTextSize(scope).X);
        ImGui.TextColored(Theme.TextDim, scope);
        if (_focus) { ImGui.SetKeyboardFocusHere(); _focus = false; }
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##q", "Type to filter rules and recipes", ref _query, 64);

        var ws = feature.Workspace();
        if (ws == null || !ws.CanEdit)
        {
            ImGui.TextColored(Theme.TextDim, ws == null ? "Save the level first: dressing rules live in a file beside it." : ws.MainError!);
            if (ImGui.IsKeyPressed(ImGuiKey.Escape)) ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
            ImGui.PopStyleVar();
            return;
        }
        var rows = Rows(ctx, ws);
        if (rows.Count > 0)
        {
            int step = (ImGui.IsKeyPressed(ImGuiKey.DownArrow) ? 1 : 0) - (ImGui.IsKeyPressed(ImGuiKey.UpArrow) ? 1 : 0);
            _index = Math.Clamp(_index + step, 0, rows.Count - 1);
        }
        Row? act = rows.Count > 0 && (ImGui.IsKeyPressed(ImGuiKey.Enter) || ImGui.IsKeyPressed(ImGuiKey.KeypadEnter)) ? rows[_index] : null;

        float rowH = ImGui.GetTextLineHeight() + Theme.Px(8);
        string? hovered = null;
        ImGui.Dummy(Theme.Px(0, 2));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, Vector2.Zero);
        ImGui.BeginChild("##rows", new Vector2(0, rowH * Math.Clamp(rows.Count, 1, 12)), ImGuiChildFlags.None, ImGuiWindowFlags.NoBackground);
        if (rows.Count == 0) ImGui.TextColored(Theme.TextDim, $"Nothing matches \"{_query.Trim()}\"");
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var p = ImGui.GetCursorScreenPos();
            float w = ImGui.GetContentRegionAvail().X;
            if (ImGui.InvisibleButton($"##r{i}", new Vector2(w, rowH))) act = row;
            bool hover = ImGui.IsItemHovered();
            if (hover && ImGui.GetIO().MouseDelta != Vector2.Zero) _index = i;
            if (hover && row.Rule != null) hovered = row.Rule.Id;
            bool selected = i == _index;
            var dl = ImGui.GetWindowDrawList();
            if (selected) SelectedRow(dl, p, p + new Vector2(w, rowH), Theme.Px(3));
            float ty = MathF.Round(p.Y + (rowH - ImGui.GetTextLineHeight()) * 0.5f);
            float x = p.X + Theme.Px(8);
            DrawState(dl, new Vector2(x, MathF.Round(p.Y + rowH * 0.5f)), row, selected);
            x += Theme.Px(20);
            dl.AddText(new Vector2(x, ty), RowText(selected, dim: row.Rule is { Enabled: false }), row.Label);
            float dw = ImGui.CalcTextSize(row.Detail).X;
            dl.AddText(new Vector2(p.X + w - dw - Theme.Px(8), ty), RowText(selected, dim: true), row.Detail);
            if (hover)
                ImGui.SetTooltip(row.Recipe != null ? $"{row.Recipe.Description}\nAdds the rule {row.Recipe.Id} and tags the selection {row.Recipe.Tag}."
                    : row.Total == 0 ? "Nothing selected can take this rule (see its faces filter in Dressing rules)"
                    : row.With == row.Total ? "Click to take the selection out of this rule" : $"Click to dress the selection ({row.Total} {(row.Rule != null && ParamJson.PrimaryTag(row.Rule.Raw, ws.Resolve).Mesh ? "mesh" : "face")}{(row.Total == 1 ? "" : "s")})");
        }
        ImGui.EndChild();
        ImGui.PopStyleVar();
        ImGui.Dummy(Theme.Px(0, 4));
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextDim);
        KeyHints([("Enter", "dress or undress"), ("Esc", "close")]);
        ImGui.PopStyleColor();
        feature.HighlightRule = hovered ?? (rows.Count > 0 ? rows[_index].Rule?.Id : null);

        bool toggle = !justOpened && ctx.Commands.Find("dressing.dressWith") is { } self && self.Hotkeys.Any(h => h.IsPressed());
        if (act is { } a)
        {
            if (a.Recipe != null) ApplyRecipe(ctx, feature, ws, a.Recipe);
            else if (a.Rule != null) Apply(ctx, feature, a.Rule, ws);
            ImGui.CloseCurrentPopup();
            feature.HighlightRule = null;
        }
        else if (toggle || ImGui.IsKeyPressed(ImGuiKey.Escape))
        {
            ImGui.CloseCurrentPopup();
            feature.HighlightRule = null;
        }
        ImGui.EndPopup();
        ImGui.PopStyleVar();
    }

    List<Row> Rows(EditorContext ctx, RulesWorkspace ws)
    {
        string q = _query.Trim();
        bool Match(params string[] texts) => q.Length == 0 || texts.Any(t => t.Contains(q, StringComparison.OrdinalIgnoreCase));
        var rows = new List<Row>();
        foreach (var rule in ws.Rules)
        {
            var (tag, mesh) = ParamJson.PrimaryTag(rule.Raw, ws.Resolve);
            if (tag == null || !Match(rule.Id, tag, rule.KindInfo?.DisplayName ?? "")) continue;
            var faces = ParamJson.ReadFaces(ws.Resolve(rule.Raw["faces"]));
            var (with, total) = DressingOps.DressCoverage(ctx.Doc, ctx.Sel, tag, mesh, faces);
            rows.Add(new Row(rule.Id, $"{rule.KindInfo?.DisplayName ?? rule.Kind} · {tag}{(rule.Enabled ? "" : " · off")}", rule, null, with, total));
        }
        foreach (var recipe in Recipes.All)
        {
            if (ws.Find(recipe.Id) != null || !Match(recipe.Label, recipe.Id, recipe.Tag)) continue;
            var faces = ParamJson.ReadFaces(recipe.Rule()["faces"]);
            var (_, total) = DressingOps.DressCoverage(ctx.Doc, ctx.Sel, recipe.Tag, recipe.MeshTag, faces);
            rows.Add(new Row($"Add {recipe.Label}", $"new rule · {recipe.Tag}", null, recipe, 0, total));
        }
        return rows;
    }

    /// <summary>A small box: filled when the whole selection is dressed with the rule, a bar when some is, empty when none.</summary>
    static void DrawState(ImDrawListPtr dl, Vector2 centreLeft, Row row, bool onAccent)
    {
        float s = Theme.Px(12);
        var min = new Vector2(centreLeft.X, MathF.Round(centreLeft.Y - s * 0.5f));
        uint line = onAccent ? Theme.OnAccentU32 : ImGui.ColorConvertFloat4ToU32(Theme.TextDim);
        if (row.Recipe != null)
        {
            float c = s * 0.5f, arm = s * 0.35f;
            dl.AddLine(min + new Vector2(c - arm, c), min + new Vector2(c + arm, c), line, Theme.Px(1.5f));
            dl.AddLine(min + new Vector2(c, c - arm), min + new Vector2(c, c + arm), line, Theme.Px(1.5f));
            return;
        }
        dl.AddRect(min, min + new Vector2(s), line, Theme.Px(2), ImDrawFlags.None, Theme.Px(1));
        if (row.Total > 0 && row.With == row.Total) dl.AddRectFilled(min + Theme.Px(3, 3), min + new Vector2(s) - Theme.Px(3, 3), line, Theme.Px(1));
        else if (row.With > 0) dl.AddRectFilled(min + new Vector2(Theme.Px(3), s * 0.5f - Theme.Px(1)), min + new Vector2(s - Theme.Px(3), s * 0.5f + Theme.Px(1)), line);
    }

    /// <summary>
    /// Tags the selection so <paramref name="rule"/> takes it (or takes it out when it already all is), switching the rule
    /// on if it was off, in one undo step.
    /// </summary>
    public static void Apply(EditorContext ctx, DressingFeature feature, RuleEntry rule, RulesWorkspace ws)
    {
        var (tag, mesh) = ParamJson.PrimaryTag(rule.Raw, ws.Resolve);
        if (tag == null) return;
        var faces = ParamJson.ReadFaces(ws.Resolve(rule.Raw["faces"]));
        var (with, total) = DressingOps.DressCoverage(ctx.Doc, ctx.Sel, tag, mesh, faces);
        if (total == 0)
        {
            ctx.Status(Nothing(ctx, rule.Id, mesh, faces), error: true);
            return;
        }
        bool on = with < total;
        int changed = 0;
        feature.EditRules(on ? $"Dress with {rule.Id}" : $"Undress {rule.Id}", w => !on || rule.Enabled || w.SetEnabled(rule.Id, true),
            ctx.Sel.MeshIds(), () => changed = DressingOps.Dress(ctx.Doc, ctx.Sel, tag, mesh, faces, on));
        string what = mesh ? SelectMode.Object.Count(changed) : SelectMode.Face.Count(changed);
        ctx.Status(on ? $"Dressed {what} with {rule.Id} (tagged {tag}){(rule.Enabled ? "" : "; switched the rule on")}" : $"Took {what} out of {rule.Id} (removed the tag {tag})");
    }

    /// <summary>Adds the recipe's rule to the level and tags the selection for it, in one undo step.</summary>
    public static void ApplyRecipe(EditorContext ctx, DressingFeature feature, RulesWorkspace ws, Recipe recipe)
    {
        var faces = ParamJson.ReadFaces(recipe.Rule()["faces"]);
        var (_, total) = DressingOps.DressCoverage(ctx.Doc, ctx.Sel, recipe.Tag, recipe.MeshTag, faces);
        if (total == 0)
        {
            ctx.Status(Nothing(ctx, recipe.Label, recipe.MeshTag, faces), error: true);
            return;
        }
        string id = ws.UniqueId(recipe.Id);
        int changed = 0;
        if (!feature.EditRules($"Dress with {recipe.Label}", w => { w.Add(id, recipe.Rule()); return true; }, ctx.Sel.MeshIds(),
                () => changed = DressingOps.Dress(ctx.Doc, ctx.Sel, recipe.Tag, recipe.MeshTag, faces, true)))
            return;
        feature.Window.Selected = id;
        ctx.Status($"Added the rule {id} and tagged {(recipe.MeshTag ? SelectMode.Object : SelectMode.Face).Count(changed)} {recipe.Tag}. Tune it in Dressing rules (Ctrl+Shift+D).");
    }

    static string Nothing(EditorContext ctx, string rule, bool mesh, FaceSelector faces) =>
        ctx.Sel.IsEmpty ? "Select faces or meshes to dress first"
        : !mesh && faces.Facing != null ? $"Nothing selected can take {rule}: it wants faces facing {faces.Facing}"
        : $"Nothing selected can take {rule}";
}
