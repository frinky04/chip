using System.Numerics;
using System.Text.Json.Nodes;
using Chip.Core.Dressing;
using Chip.Core.Scene;
using Chip.Editor.Framework;
using Chip.Editor.UI;
using ImGuiNET;
using NativeFileDialogSharp;
using static Chip.Editor.UI.Widgets;

namespace Chip.Editor.Features.Dressing;

/// <summary>
/// Tools > Dressing rules (Ctrl+Shift+D): the level's rules in a list (switch, name, what it does, how much it made) and
/// the selected rule's settings beside it. Add rules from recipes, rename, duplicate, delete, pick styles to build on,
/// edit shared values, save the level's rules as a style. Every change is written to the rules file as one undo step, so
/// the file stays the source of truth agents and other tools edit. Hovering a rule highlights what it takes in the viewport.
/// </summary>
public sealed class DressingRulesWindow(DressingFeature feature)
{
    public bool Open;
    bool _wasOpen, _editStyle, _highlightSelected, _highlighting;
    string? _selected;
    const string VarsRow = "\0vars";
    string _renameDraft = "";
    string? _renameFor;
    readonly RuleFields _fields = new(feature);
    IReadOnlyList<string> _levelTags = [];
    int _tagsVersion = -1;

    /// <summary>The selected rule's id (tests and the inspector use it to jump to a rule).</summary>
    public string? Selected { get => _selected; set => _selected = value; }

    /// <summary>Edits go to the style file that defines a rule rather than the level (only with imports).</summary>
    public bool EditStyle { get => _editStyle; set => _editStyle = value; }

    public void Draw(EditorContext ctx)
    {
        if (!Open)
        {
            _wasOpen = false;
            if (_highlighting) { feature.HighlightRule = null; _highlighting = false; }
            return;
        }
        var vp = ctx.ViewportSize;
        ImGui.SetNextWindowPos(ctx.ViewportOrigin + new Vector2(MathF.Max(Theme.Px(10), vp.X - Theme.Px(720)), Theme.Px(56)), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(Theme.Px(680, 520), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(Theme.Px(480, 300), new Vector2(float.MaxValue));
        if (!_wasOpen) ImGui.SetNextWindowFocus();
        _wasOpen = true;
        bool open = true;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Theme.Px(10, 8));
        bool visible = ImGui.Begin("Dressing rules###dressingRules", ref open, ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoScrollbar);
        ImGui.PopStyleVar();
        string? hovered = null;
        if (visible) hovered = DrawContents(ctx);
        ImGui.End();
        if (!open) Open = false;
        var highlight = hovered ?? (_highlightSelected && _selected != VarsRow ? _selected : null);
        if (highlight != null || _highlighting) feature.HighlightRule = highlight;
        _highlighting = highlight != null;
    }

    string? DrawContents(EditorContext ctx)
    {
        if (feature.Workspace() is not { } ws)
        {
            Wrapped("Save the level first: its dressing rules live in a file beside it (level.dressing.json).", Theme.TextDim);
            return null;
        }
        if (!ws.CanEdit)
        {
            Wrapped(ws.MainError!, Theme.Error);
            if (ImGui.Button("Open JSON")) feature.Open();
            return null;
        }
        if (_tagsVersion != ctx.Session.Version)
        {
            _levelTags = TagOps.Usage(ctx.Doc).Select(u => u.Tag).Where(t => !t.StartsWith(Core.Geometry.TagSet.SkipPrefix, StringComparison.Ordinal)).ToList();
            _tagsVersion = ctx.Session.Version;
            DressingFeature.SetTagOrder(_levelTags);
        }
        var level = feature.Current();
        StylesBar(ctx, ws);
        ImGui.Separator();

        if (ws.Rules.Count == 0)
        {
            EmptyState(ctx, ws);
            return null;
        }
        if (_selected == null || (_selected != VarsRow && ws.Find(_selected) == null)) _selected = ws.Rules[0].Id;

        float footer = ImGui.GetFrameHeightWithSpacing();
        float listW = MathF.Min(Theme.Px(250), ImGui.GetContentRegionAvail().X * 0.4f);
        float bodyH = ImGui.GetContentRegionAvail().Y - footer;
        string? hovered = null;
        ImGui.PushStyleColor(ImGuiCol.ChildBg, Theme.Bg);
        if (ImGui.BeginChild("##rules", new Vector2(listW, bodyH))) hovered = RuleList(ctx, ws, level);
        ImGui.EndChild();
        ImGui.PopStyleColor();
        ImGui.SameLine();
        if (ImGui.BeginChild("##details", new Vector2(0, bodyH)))
        {
            ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, Theme.Px(6, 5));
            if (_selected == VarsRow) Vars(ws);
            else if (ws.Find(_selected!) is { } rule) Details(ctx, ws, rule, level);
            ImGui.PopStyleVar();
        }
        ImGui.EndChild();
        Footer(ctx, ws, level);
        return hovered;
    }

    static void Wrapped(string text, Vector4 color)
    {
        ImGui.PushTextWrapPos(0);
        ImGui.TextColored(color, text);
        ImGui.PopTextWrapPos();
    }

    // ---- Styles ----

    void StylesBar(EditorContext ctx, RulesWorkspace ws)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.TextDim, "Styles");
        Tip("Rules files this level builds on. Their rules apply here; what you change applies to this level only, unless you edit the style.");
        var imports = ws.Main!.Imports;
        foreach (var (import, i) in imports.Select((s, i) => (s, i)))
        {
            ImGui.SameLine();
            string name = StyleName(import);
            if (ImGui.Button($"{name}  ×##style{i}"))
                feature.EditRules($"Remove style {name}", w => { w.SetImports(imports.Where((_, j) => j != i)); return true; });
            Tip($"{import}\nClick to stop using this style (its rules leave this level)");
        }
        ImGui.SameLine();
        if (ImGui.Button(imports.Count == 0 ? "+ Use a style" : "+")) ImGui.OpenPopup("##styles");
        Tip("Build on a shared rules file, so levels share one look");
        if (ImGui.BeginPopup("##styles"))
        {
            var known = imports.Select(i => RulePaths.Resolve(ws.MainPath, i)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var found = FindStyles(ws.MainPath).Where(p => !known.Contains(RulePaths.Normalize(p))).ToList();
            if (found.Count == 0) ImGui.TextColored(Theme.TextDim, "No other rules files nearby");
            foreach (var path in found)
            {
                if (ImGui.Selectable(StyleName(path))) AddStyle(ws, path);
                Tip(path);
            }
            ImGui.Separator();
            if (ImGui.Selectable("Browse..."))
            {
                var r = Dialog.FileOpen("json", Path.GetDirectoryName(Path.GetFullPath(ws.MainPath)));
                if (r.IsOk) AddStyle(ws, r.Path);
            }
            ImGui.EndPopup();
        }

        float right = ImGui.CalcTextSize("Open JSON").X + ImGui.GetStyle().FramePadding.X * 2;
        float styleW = imports.Count > 0 ? ImGui.CalcTextSize("Edit the style").X + ImGui.GetFrameHeight() + ImGui.GetStyle().ItemInnerSpacing.X + ImGui.GetStyle().ItemSpacing.X : 0;
        ImGui.SameLine(MathF.Max(ImGui.GetCursorPosX(), ImGui.GetWindowContentRegionMax().X - right - styleW));
        if (imports.Count > 0)
        {
            ImGui.Checkbox("Edit the style", ref _editStyle);
            Tip("On: changes to a style's rules go to the style file and apply to every level that uses it. Off: they apply to this level only.");
            ImGui.SameLine();
        }
        else _editStyle = false;
        if (ImGui.Button("Open JSON")) feature.Open();
        Tip($"{ws.MainPath}\nThe rules as a file, for hand edits and agents. Chip reloads it whenever it changes. Check it headlessly: Chip --rules-check <level>.chip");
    }

    void AddStyle(RulesWorkspace ws, string path)
    {
        string name = StyleName(path);
        feature.EditRules($"Use style {name}", w => { w.SetImports(w.Main!.Imports.Append(RulesWorkspace.Relative(ws.MainPath, path))); return true; });
    }

    static string StyleName(string path)
    {
        var name = Path.GetFileName(path);
        return name.EndsWith(".dressing.json", StringComparison.OrdinalIgnoreCase) ? name[..^".dressing.json".Length] : Path.GetFileNameWithoutExtension(name);
    }

    /// <summary>Rules files near the level: its folder, a styles folder in it, and the parent folder and its styles folder.</summary>
    static IEnumerable<string> FindStyles(string mainPath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(mainPath)) ?? "";
        var parent = Path.GetDirectoryName(dir);
        var dirs = new[] { dir, Path.Combine(dir, "styles"), parent, parent == null ? null : Path.Combine(parent, "styles") };
        var self = Path.GetFullPath(mainPath);
        foreach (var d in dirs.OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string[] files;
            try { files = Directory.Exists(d) ? Directory.GetFiles(d, "*.dressing.json") : []; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            foreach (var f in files.Order(StringComparer.OrdinalIgnoreCase))
                if (!string.Equals(Path.GetFullPath(f), self, StringComparison.OrdinalIgnoreCase)) yield return f;
        }
    }

    // ---- Empty ----

    void EmptyState(EditorContext ctx, RulesWorkspace ws)
    {
        ImGui.Dummy(Theme.Px(0, 4));
        Wrapped("No dressing yet. Dressing turns the blockout into the shipped level: trims along walls, bevelled corners, railings from boxes, props on floors. Start with a recipe, or use a style above.", Theme.TextDim);
        ImGui.Dummy(Theme.Px(0, 4));
        if (ImGui.BeginChild("##recipes"))
            foreach (var recipe in Recipes.All)
            {
                ImGui.PushID(recipe.Id);
                if (ImGui.Button("Add", new Vector2(Theme.Px(56), 0))) AddRecipe(ws, recipe);
                ImGui.SameLine();
                ImGui.AlignTextToFramePadding();
                ImGui.Text(recipe.Label);
                ImGui.SameLine();
                ImGui.TextColored(Theme.TextDim, recipe.Description);
                ImGui.PopID();
            }
        ImGui.EndChild();
    }

    void AddRecipe(RulesWorkspace ws, Recipe recipe)
    {
        string id = ws.UniqueId(recipe.Id);
        if (feature.EditRules($"Add rule {recipe.Label}", w => { w.Add(id, recipe.Rule()); return true; })) _selected = id;
    }

    // ---- List ----

    string? RuleList(EditorContext ctx, RulesWorkspace ws, DressedLevel? level)
    {
        string? hovered = null;
        var broken = level?.Rules.Broken ?? [];
        float h = ImGui.GetFrameHeight();
        foreach (var rule in ws.Rules)
        {
            ImGui.PushID(rule.Id);
            var start = ImGui.GetCursorScreenPos();
            float w = ImGui.GetContentRegionAvail().X;
            bool selected = _selected == rule.Id;
            ImGui.SetNextItemAllowOverlap();
            if (ImGui.Selectable("##row", false, ImGuiSelectableFlags.None, new Vector2(w, h))) _selected = rule.Id;
            if (ImGui.IsItemHovered()) hovered = rule.Id;
            var dl = ImGui.GetWindowDrawList();
            if (selected) SelectedRow(dl, start, start + new Vector2(w, h));

            // Switch, name, then what it made (or a problem) on the right.
            ImGui.SetCursorScreenPos(start + new Vector2(Theme.Px(4), 0));
            bool on = rule.Enabled;
            if (ImGui.Checkbox("##on", ref on)) SetEnabled(rule, on);
            Tip(on ? "Switch this rule off" : "Switch this rule on");
            bool isBroken = broken.Contains(rule.Id);
            var stats = level?.Stats.FirstOrDefault(s => s.Rule == rule.Id);
            string right = isBroken ? "error" : !rule.Enabled ? "off" : stats == null ? "" : stats.Outputs == 0 ? "nothing" : stats.Outputs.ToString();
            float rw = ImGui.CalcTextSize(right).X;
            float x = ImGui.GetItemRectMax().X + Theme.Px(6);
            float ty = start.Y + (h - ImGui.GetTextLineHeight()) * 0.5f;
            string name = rule.Id + (rule.ChangedInLevel ? " •" : "");
            dl.PushClipRect(new Vector2(x, start.Y), new Vector2(start.X + w - rw - Theme.Px(12), start.Y + h), true);
            dl.AddText(new Vector2(x, ty), RowText(selected, dim: !rule.Enabled), name);
            float nameW = ImGui.CalcTextSize(name).X;
            if (rule.KindInfo is { } kind)
                dl.AddText(new Vector2(x + nameW + Theme.Px(8), ty), selected ? Theme.OnAccentU32 : ImGui.ColorConvertFloat4ToU32(Theme.TextDisabled), kind.DisplayName);
            dl.PopClipRect();
            uint rightCol = isBroken ? ImGui.ColorConvertFloat4ToU32(Theme.Error) : RowText(selected, dim: true);
            dl.AddText(new Vector2(start.X + w - rw - Theme.Px(6), ty), rightCol, right);
            if (hovered == rule.Id)
                ImGui.SetTooltip((rule.Local ? "" : $"From the style {StyleName(rule.DefinedIn)}{(rule.ChangedInLevel ? ", changed in this level" : "")}\n")
                    + (rule.KindInfo?.Summary ?? "") + (stats is { Enabled: true } ? $"\n{stats.Outputs} mesh{(stats.Outputs == 1 ? "" : "es")} from {stats.Targets} {stats.TargetNoun}{(stats.Targets == 1 ? "" : "s")}" : ""));
            ImGui.SetCursorScreenPos(start + new Vector2(0, h + ImGui.GetStyle().ItemSpacing.Y));
            ImGui.PopID();
        }

        // Shared values and adding, after the rules.
        ImGui.Dummy(Theme.Px(0, 2));
        if (ws.Vars.Count > 0)
        {
            var start = ImGui.GetCursorScreenPos();
            float w = ImGui.GetContentRegionAvail().X;
            if (ImGui.Selectable("##vars", false, ImGuiSelectableFlags.None, new Vector2(w, h))) _selected = VarsRow;
            bool sel = _selected == VarsRow;
            var dl = ImGui.GetWindowDrawList();
            if (sel) SelectedRow(dl, start, start + new Vector2(w, h));
            dl.AddText(start + new Vector2(Theme.Px(8), (h - ImGui.GetTextLineHeight()) * 0.5f), RowText(sel, dim: !sel), $"Shared values ({ws.Vars.Count})");
        }
        PushPrimary();
        if (ImGui.Button("+ Add rule", new Vector2(-1, 0))) ImGui.OpenPopup("##add");
        PopPrimary();
        if (ImGui.BeginPopup("##add"))
        {
            foreach (var recipe in Recipes.All)
            {
                if (ImGui.Selectable($"{recipe.Label}##{recipe.Id}")) AddRecipe(ws, recipe);
                Tip($"{recipe.Description}\n{recipe.Kind.DisplayName}, selects {(recipe.MeshTag ? "meshes" : "faces")} tagged {recipe.Tag}");
            }
            ImGui.EndPopup();
        }
        return hovered;
    }

    void SetEnabled(RuleEntry rule, bool on) =>
        feature.EditRules(on ? $"Switch on {rule.Id}" : $"Switch off {rule.Id}", w => w.SetEnabled(rule.Id, on, _editStyle && !rule.Local));

    // ---- Details ----

    void Details(EditorContext ctx, RulesWorkspace ws, RuleEntry rule, DressedLevel? level)
    {
        bool style = _editStyle && !rule.Local;
        // Name (renames carry the level's per-mesh overrides and skip: tags along).
        if (_renameFor != rule.Id) { _renameFor = rule.Id; _renameDraft = rule.Id; }
        bool canRename = rule.Local || style;
        if (!canRename) ImGui.BeginDisabled();
        ImGui.SetNextItemWidth(MathF.Min(Theme.Px(200), ImGui.GetContentRegionAvail().X * 0.5f));
        ImGui.PushFont(Fonts.Strong);
        ImGui.InputText("##name", ref _renameDraft, Core.Geometry.TagSet.MaxLength + 8);
        ImGui.PopFont();
        if (ImGui.IsItemDeactivatedAfterEdit()) Rename(ctx, rule, style);
        if (!canRename) ImGui.EndDisabled();
        Tip(canRename ? "The rule's name (lowercase letters, digits, - and _). Renaming keeps the level's per-mesh changes."
            : "A style's rule: turn on \"Edit the style\" to rename it (other levels using the style keep the old name)");
        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.TextDim, rule.KindInfo?.DisplayName ?? rule.Kind ?? "?");
        if (!rule.Local)
        {
            ImGui.SameLine();
            ImGui.TextColored(style ? Theme.Accent : Theme.TextDisabled, style ? $"editing {StyleName(rule.DefinedIn)}" : $"from {StyleName(rule.DefinedIn)}");
            Tip(style ? "Changes go to the style file: every level using it changes" : "Changes apply to this level only (orange values; × puts the style's back)");
        }
        float hw = ImGui.CalcTextSize("Highlight").X + ImGui.GetFrameHeight() + ImGui.GetStyle().ItemInnerSpacing.X;
        ImGui.SameLine(MathF.Max(ImGui.GetCursorPosX(), ImGui.GetWindowContentRegionMax().X - hw));
        ImGui.Checkbox("Highlight", ref _highlightSelected);
        Tip("Tint what this rule takes in the viewport (hovering a rule in the list does too)");

        if (rule.KindInfo is { } kind)
        {
            ImGui.PushTextWrapPos(0);
            ImGui.TextColored(Theme.TextDim, kind.Summary);
            ImGui.PopTextWrapPos();
        }
        string note = rule.Note ?? "";
        ImGui.SetNextItemWidth(-1);
        string noteKey = "##note" + rule.Id;
        if (ImGui.InputTextWithHint(noteKey, "Note for people and agents reading the rules", ref note, 512)) _noteDraft = note;
        if (ImGui.IsItemDeactivatedAfterEdit() && _noteDraft != null)
        {
            var text = _noteDraft.Trim();
            _noteDraft = null;
            feature.EditRules($"Note on {rule.Id}", w => w.Set(rule.Id, "note", text.Length == 0 ? null : JsonValue.Create(text), style));
        }
        SectionDivider();

        _fields.Draw(ctx, ws, rule, style, _levelTags);

        // Problems with this rule.
        var problems = level?.Diagnostics.Where(d => d.Rule == rule.Id).ToList() ?? [];
        if (problems.Count > 0)
        {
            SectionDivider();
            foreach (var d in problems.Take(5))
            {
                ImGui.PushTextWrapPos(0);
                ImGui.TextColored(d.IsError ? Theme.Error : Theme.Hover, d.Message);
                if (d.Fix != null) ImGui.TextColored(Theme.TextDim, d.Fix);
                ImGui.PopTextWrapPos();
            }
        }

        SectionDivider();
        Actions(ctx, ws, rule, level, style);
    }

    string? _noteDraft;

    void Actions(EditorContext ctx, RulesWorkspace ws, RuleEntry rule, DressedLevel? level, bool style)
    {
        var (tag, meshTag) = ParamJson.PrimaryTag(rule.Raw, ws.Resolve);
        bool canDress = tag != null && TagOps.CanTag(ctx.Sel.Mode) && !ctx.Sel.IsEmpty;
        if (!canDress) ImGui.BeginDisabled();
        if (ImGui.Button("Dress selection")) DressWithPopup.Apply(ctx, feature, rule, ws);
        if (!canDress) ImGui.EndDisabled();
        Tip(tag == null ? "This rule picks faces without a tag (by facing or material), so there's nothing to add"
            : $"Tag the selection {tag} so this rule takes it{(meshTag ? " (the selected meshes)" : ctx.Sel.Mode == SelectMode.Object ? " (the faces of the selected meshes it can use)" : "")}. Again to remove.");
        SameLineIfFits("Select what it takes");
        if (ImGui.Button("Select what it takes")) SelectTargets(ctx, level, rule);
        Tip("Select every face (or mesh) this rule takes");
        SameLineIfFits("Duplicate");
        if (ImGui.Button("Duplicate"))
        {
            string id = ws.UniqueId(rule.Id);
            if (feature.EditRules($"Duplicate {rule.Id}", w => w.Duplicate(rule.Id, id))) _selected = id;
        }
        Tip("A copy defined in this level, to vary it");
        SameLineIfFits("Delete");
        bool canDelete = rule.Local || style;
        if (!canDelete) ImGui.BeginDisabled();
        if (ImGui.Button("Delete"))
            feature.EditRules($"Delete rule {rule.Id}", w => w.Delete(rule.Id, style), DressingOps.Mentioning(ctx.Doc, rule.Id),
                () => DressingOps.RenameRule(ctx.Doc, rule.Id, null));
        if (!canDelete) ImGui.EndDisabled();
        Tip(canDelete ? "Remove the rule (per-mesh changes to it go too). Ctrl+Z brings it back."
            : "A style's rule: switch it off for this level, or turn on \"Edit the style\" to delete it from the style");
    }

    /// <summary>Keeps the next button on this row when it fits, else starts a new row (narrow windows).</summary>
    static void SameLineIfFits(string label)
    {
        float w = ImGui.CalcTextSize(label).X + ImGui.GetStyle().FramePadding.X * 2;
        float right = ImGui.GetItemRectMax().X + ImGui.GetStyle().ItemSpacing.X + w;
        if (right <= ImGui.GetWindowPos().X + ImGui.GetWindowContentRegionMax().X) ImGui.SameLine();
    }

    void Rename(EditorContext ctx, RuleEntry rule, bool style)
    {
        var id = Core.Geometry.TagSet.Normalize(_renameDraft);
        if (id == null || id == rule.Id) { _renameDraft = rule.Id; return; }
        if (!RulesWorkspace.IsValidId(id)) { ctx.Status($"\"{_renameDraft}\" can't be a rule name: use lowercase letters, digits, - and _", error: true); _renameDraft = rule.Id; return; }
        if (feature.Workspace()?.Find(id) != null) { ctx.Status($"There is already a rule called {id}", error: true); _renameDraft = rule.Id; return; }
        if (feature.EditRules($"Rename {rule.Id} to {id}", w => w.Rename(rule.Id, id, style), DressingOps.Mentioning(ctx.Doc, rule.Id),
                () => DressingOps.RenameRule(ctx.Doc, rule.Id, id)))
        {
            _selected = id;
            _renameFor = id;
        }
        else _renameDraft = rule.Id;
    }

    static void SelectTargets(EditorContext ctx, DressedLevel? level, RuleEntry rule)
    {
        if (level == null) return;
        var targets = level.Targets(rule.Id);
        bool meshes = targets.Count > 0 && targets[0].Face < 0;
        var mode = meshes ? SelectMode.Object : SelectMode.Face;
        var items = targets.Where(t => ctx.Doc.Find(t.Mesh) is { Locked: false })
            .Select(t => meshes ? ElementRef.Object(t.Mesh) : ElementRef.Face(t.Mesh, t.Face)).ToList();
        if (ctx.Sel.Mode != mode) ctx.SetMode(mode);
        SelectionOps.Replace(ctx.Sel, mode, items, null);
        ctx.Status(items.Count == 0 ? $"{rule.Id} takes nothing yet" : $"Selected {mode.Count(items.Count)} that {rule.Id} takes");
    }

    // ---- Shared values ----

    readonly Dictionary<string, string> _varDrafts = new();

    void Vars(RulesWorkspace ws)
    {
        Heading("Shared values");
        Wrapped("Values rules use by name (\"$trim\"). Change one here and every rule using it follows. A style's value changed here applies to this level only.", Theme.TextDim);
        SectionDivider();
        foreach (var (name, (value, definedIn)) in ws.Vars.OrderBy(v => v.Key, StringComparer.Ordinal))
        {
            ImGui.PushID(name);
            bool fromStyle = !string.Equals(definedIn, ws.MainPath, StringComparison.OrdinalIgnoreCase);
            bool overridden = !fromStyle && ws.ImportPaths.Count > 0 && StyleHas(ws, name);
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(overridden ? Theme.Accent : Theme.TextDim, name);
            Tip(fromStyle ? $"From the style {StyleName(definedIn)}" : overridden ? "This level's value (the style has another)" : "Set in this level");
            ImGui.SameLine(Theme.Px(108));
            float w = ImGui.GetContentRegionAvail().X - (overridden ? Theme.Px(24) + ImGui.GetStyle().ItemSpacing.X : 0);
            ImGui.SetNextItemWidth(w);
            string text = ParamJson.Number(value) is { } n ? n.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : ParamJson.Text(value) ?? value?.ToJsonString() ?? "";
            string shown = _varDrafts.TryGetValue(name, out var d) ? d : text;
            if (ImGui.InputText("##v", ref shown, 256)) _varDrafts[name] = shown;
            if (ImGui.IsItemDeactivatedAfterEdit() && _varDrafts.Remove(name, out var typed) && typed != text)
            {
                JsonNode? next = double.TryParse(typed, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var num)
                    ? ParamJson.Num(num) : typed.TrimStart().StartsWith('[') || typed.TrimStart().StartsWith('{') ? TryParse(typed) : JsonValue.Create(typed);
                if (next != null) feature.EditRules($"Shared value {name}", w => { w.SetVar(name, next); return true; });
            }
            Tip(ParamJson.Number(value) != null ? "A number (lengths are metres)" : "Text, a number, or JSON");
            if (overridden)
            {
                ImGui.SameLine();
                if (ImGui.Button("×", new Vector2(Theme.Px(24), 0))) feature.EditRules($"Reset shared value {name}", w => { w.SetVar(name, null); return true; });
                Tip("Back to the style's value");
            }
            ImGui.PopID();
        }
    }

    static bool StyleHas(RulesWorkspace ws, string name) =>
        ws.ImportPaths.Any(p => ws.Doc(p)?.Vars.Any(v => v.Name == name) == true);

    static JsonNode? TryParse(string text)
    {
        try { return JsonNode.Parse(text); }
        catch (System.Text.Json.JsonException) { return null; }
    }

    // ---- Footer ----

    void Footer(EditorContext ctx, RulesWorkspace ws, DressedLevel? level)
    {
        ImGui.AlignTextToFramePadding();
        int errors = level?.ErrorCount ?? 0, warnings = level?.WarningCount ?? 0;
        int on = ws.Rules.Count(r => r.Enabled);
        string summary = $"{on} of {ws.Rules.Count} rule{(ws.Rules.Count == 1 ? "" : "s")} on"
            + (errors > 0 ? $" · {errors} error{(errors == 1 ? "" : "s")}" : "") + (warnings > 0 ? $" · {warnings} warning{(warnings == 1 ? "" : "s")}" : "");
        ImGui.TextColored(errors > 0 ? Theme.Error : Theme.TextDim, summary);
        if (errors + warnings > 0) Tip("Details are under each rule and in the inspector's Dressing section");
        bool canSave = ws.Main!.RuleIds.Any(ws.Main.Defines);
        float sw = ImGui.CalcTextSize("Save as style...").X + ImGui.GetStyle().FramePadding.X * 2;
        ImGui.SameLine(MathF.Max(ImGui.GetCursorPosX(), ImGui.GetWindowContentRegionMax().X - sw));
        if (!canSave) ImGui.BeginDisabled();
        if (ImGui.Button("Save as style...")) SaveAsStyle(ctx, ws);
        if (!canSave) ImGui.EndDisabled();
        Tip("Move this level's own rules into a new style file and use it, so other levels can share them");
    }

    void SaveAsStyle(EditorContext ctx, RulesWorkspace ws)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(ws.MainPath)) ?? "";
        var r = Dialog.FileSave("json", Directory.Exists(Path.Combine(dir, "styles")) ? Path.Combine(dir, "styles") : dir);
        if (!r.IsOk) return;
        var path = r.Path.EndsWith(".dressing.json", StringComparison.OrdinalIgnoreCase) ? r.Path
            : Path.ChangeExtension(r.Path, null).TrimEnd('.') + ".dressing.json";
        if (File.Exists(path)) { ctx.Status($"{Path.GetFileName(path)} already exists: choose a new name (use + to build on an existing style)", error: true); return; }
        var name = StyleName(path);
        name = name.Length > 0 ? char.ToUpperInvariant(name[0]) + name[1..] : "Style";
        if (feature.EditRules($"Save as style {name}", w => w.SaveAsStyle(path, name)))
            ctx.Status($"Saved the rules as {Path.GetFileName(path)}; this level now uses it. Add it to other levels under Styles.");
    }
}
