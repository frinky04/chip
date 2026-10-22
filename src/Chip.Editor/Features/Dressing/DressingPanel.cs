using System.Diagnostics;
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
/// The Dressing tab of the dock (Ctrl+Shift+D), stacked for a tall, narrow panel: the dressed/blockout switch and the
/// rules file's commands, the styles the level builds on, the level's rules in a list (switch, name, kind, how much it
/// made) grouped by the style they come from, and under it the selected rule's settings. Drag the bar between them to
/// give either more room. Add rules from recipes, rename, duplicate, delete, edit shared values, save the level's rules
/// as a style. Every change is written to the rules file as one undo step, so the file stays the source of truth agents
/// and other tools edit. Hovering a rule highlights what it takes in the viewport.
/// </summary>
public sealed class DressingPanel(DressingFeature feature, EditorContext context) : IDockPanel
{
    public const string PanelId = "dressing";
    bool _editStyle, _highlightSelected, _highlighting;
    string? _selected, _hovered;
    const string VarsRow = "\0vars";
    string _renameDraft = "", _filter = "";
    string? _renameFor, _noteDraft;
    float _listH;
    readonly RuleFields _fields = new(feature);

    public string Id => PanelId;
    public string Title => "Dressing";
    public Icon Icon => Icon.Brush;
    public int Order => 30;
    public string? Command => "dressing.rules";

    /// <summary>Whether the tab is on screen; setting it opens the dock on it (or collapses the dock).</summary>
    public bool Open
    {
        get => context.DockShowing(PanelId);
        set
        {
            if (value) context.ShowDock(PanelId);
            else if (Open) context.DockOpen = false;
        }
    }
    IReadOnlyList<string> _levelTags = [];
    int _tagsVersion = -1;

    /// <summary>The selected rule's id (tests and the inspector use it to jump to a rule).</summary>
    public string? Selected { get => _selected; set => _selected = value; }

    /// <summary>Edits to a style's rules go to the style file (every level using it) rather than this level.</summary>
    public bool EditStyle { get => _editStyle; set => _editStyle = value; }

    /// <summary>Opens the window on the shared values.</summary>
    public void ShowSharedValues()
    {
        Open = true;
        _selected = VarsRow;
    }

    /// <summary>Opens the window on one rule.</summary>
    public void ShowRule(string id)
    {
        Open = true;
        _selected = id;
    }

    /// <summary>The dock tab. What the list's hovered row is goes to <see cref="Update"/>, which highlights it.</summary>
    public void Draw(EditorContext ctx) => _hovered = DrawContents(ctx);

    /// <summary>Per frame, after the dock: highlights the hovered (or, with Highlight on, selected) rule while the tab shows.</summary>
    public void Update(EditorContext ctx)
    {
        if (!Open)
        {
            _hovered = null;
            if (_highlighting) { feature.HighlightRule = null; _highlighting = false; }
            return;
        }
        var highlight = _hovered ?? (_highlightSelected && _selected != VarsRow ? _selected : null);
        if (highlight != null || _highlighting) feature.HighlightRule = highlight;
        _highlighting = highlight != null;
        _hovered = null;
    }

    string? DrawContents(EditorContext ctx)
    {
        var ws = feature.Workspace();
        TopBar(ctx, ws);
        if (ws == null)
        {
            Wrapped("Dressing rules live in a file beside the level (level.dressing.json), so save the level first.", Theme.TextDim);
            ImGui.Dummy(Theme.Px(0, 4));
            if (ImGui.Button("Save level...")) ctx.Commands.Execute("file.save");
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
        StylesBar(ws);
        ImGui.Dummy(Theme.Px(0, 2));

        if (ws.Rules.Count == 0)
        {
            EmptyState(ctx, ws);
            return null;
        }
        if (_selected == null || (_selected != VarsRow && ws.Find(_selected) == null)) _selected = ws.Rules[0].Id;

        // Stacked: the list on top, the bar, then the selected rule filling the rest above a one-line summary.
        float footer = ImGui.GetFrameHeightWithSpacing();
        float bodyH = ImGui.GetContentRegionAvail().Y - footer;
        float minList = Theme.Px(110), minDetails = Theme.Px(160);
        if (_listH <= 0) _listH = bodyH * 0.38f;
        _listH = Math.Clamp(_listH, minList, MathF.Max(minList, bodyH - minDetails));
        string? hovered = ListPane(ctx, ws, level, new Vector2(ImGui.GetContentRegionAvail().X, _listH));
        Splitter();
        ImGui.PushStyleColor(ImGuiCol.ChildBg, Theme.Bg with { W = 0.35f });
        bool details = ImGui.BeginChild("##details", new Vector2(0, MathF.Max(Theme.Px(80), ImGui.GetContentRegionAvail().Y - footer)),
            ImGuiChildFlags.AlwaysUseWindowPadding);
        ImGui.PopStyleColor();
        if (details)
        {
            ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, Theme.Px(6, 5));
            if (_selected == VarsRow) Vars(ctx, ws);
            else if (ws.Find(_selected!) is { } rule)
            {
                // Per-rule ids: a field still being typed in when another rule is picked must not commit into that rule.
                ImGui.PushID(rule.Id);
                Details(ctx, ws, rule, level);
                ImGui.PopID();
            }
            ImGui.PopStyleVar();
        }
        ImGui.EndChild();
        Footer(ctx, ws, level);
        return hovered;
    }

    /// <summary>A thin bar between the list and the rule; drag it to give either more room.</summary>
    void Splitter()
    {
        float h = Theme.Px(10);
        ImGui.InvisibleButton("##split", new Vector2(ImGui.GetContentRegionAvail().X, h));
        bool active = ImGui.IsItemActive(), hover = ImGui.IsItemHovered();
        if (active) _listH += ImGui.GetIO().MouseDelta.Y;
        if (active || hover) ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeNS);
        var (a, b) = (ImGui.GetItemRectMin(), ImGui.GetItemRectMax());
        float y = MathF.Round((a.Y + b.Y) * 0.5f);
        var dl = ImGui.GetWindowDrawList();
        float cx = (a.X + b.X) * 0.5f, grip = Theme.Px(14);
        uint col = ImGui.ColorConvertFloat4ToU32(active || hover ? Theme.TextDim : Theme.FrameActive);
        dl.AddLine(new Vector2(cx - grip, y - Theme.Px(1.5f)), new Vector2(cx + grip, y - Theme.Px(1.5f)), col, Theme.Px(1));
        dl.AddLine(new Vector2(cx - grip, y + Theme.Px(1.5f)), new Vector2(cx + grip, y + Theme.Px(1.5f)), col, Theme.Px(1));
    }

    /// <summary>
    /// Dressed / Blockout (what the viewport shows), then the rules file's commands in a ⋯ menu: open the JSON, a new
    /// file, the sample, bake, save as a style.
    /// </summary>
    void TopBar(EditorContext ctx, RulesWorkspace? ws)
    {
        float more = ImGui.GetFrameHeight();
        if (feature.Current() != null)
        {
            int view = TextSegments("##view", [("Dressed", "Show the dressed level; you still edit the blockout  (`)"),
                ("Blockout", "Show the bare blockout. Export still includes the dressing  (`)")], ctx.ShowDressed ? 0 : 1);
            if (view >= 0 && (view == 0) != ctx.ShowDressed) ctx.Commands.Execute("view.dressed");
            ImGui.SameLine(ImGui.GetWindowContentRegionMax().X - more);
        }
        else
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.TextDim, "No rules yet");
            ImGui.SameLine(ImGui.GetWindowContentRegionMax().X - more);
        }
        if (IconButton("more", Icon.Dots, "Tag colours, and the rules file: open the JSON, new file, the sample, bake, save as a style", more))
            ImGui.OpenPopup("##dressingMenu");
        if (ImGui.BeginPopup("##dressingMenu"))
        {
            if (ImGui.MenuItem("Tag colours", null, feature.TagColours)) ctx.Commands.Execute("view.tagColours");
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort)) ImGui.SetTooltip("Tint every tagged face by its tag, so untagged surfaces stand out");
            ImGui.Separator();
            if (ImGui.MenuItem("Open JSON", null, false, ws is { CanEdit: true } || feature.Current() != null)) feature.Open();
            if (ws != null && ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort))
                ImGui.SetTooltip($"{ws.MainPath}\nThe rules as a file, for hand edits and agents. Chip reloads it whenever it changes.\nCheck it headlessly: Chip --rules-check <level>.chip");
            foreach (var id in new[] { "dressing.new", "dressing.sample", "dressing.bake" })
                if (ctx.Commands.Find(id) is { } c && ImGui.MenuItem(c.Label, c.HotkeyText, false, c.Enabled)) c.Execute();
            bool canSave = ws is { CanEdit: true } && ws.Main!.RuleIds.Any(ws.Main.Defines);
            if (ImGui.MenuItem("Save as style...", null, false, canSave)) SaveAsStyle(ctx, ws!);
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort | ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("Move this level's own rules into a new style file and use it, so other levels can share them");
            ImGui.EndPopup();
        }
        ImGui.Dummy(Theme.Px(0, 2));
    }

    static void Wrapped(string text, Vector4 color)
    {
        ImGui.PushTextWrapPos(0);
        ImGui.TextColored(color, text);
        ImGui.PopTextWrapPos();
    }

    // ---- Styles ----

    void StylesBar(RulesWorkspace ws)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.TextDim, "Styles");
        Tip("Shared rules files this level builds on. Their rules apply here; what you change applies to this level only, unless you choose to edit the shared style.");
        var imports = ws.Main!.Imports;
        float right = ImGui.GetWindowContentRegionMax().X;
        foreach (var (import, i) in imports.Select((s, i) => (s, i)))
        {
            string name = StyleName(import);
            float w = ImGui.CalcTextSize(name).X + ImGui.GetStyle().FramePadding.X * 2 + ImGui.GetFrameHeight() + ImGui.GetStyle().ItemSpacing.X * 2;
            ImGui.SameLine();
            if (ImGui.GetCursorPosX() + w > right) { ImGui.NewLine(); ImGui.SetCursorPosX(ImGui.CalcTextSize("Styles").X + ImGui.GetStyle().ItemSpacing.X + ImGui.GetStyle().WindowPadding.X); }
            ImGui.PushID(i);
            if (ImGui.Button(name)) OpenFile(RulePaths.Resolve(ws.MainPath, import));
            Tip($"{import}\nClick to open the style's file");
            ImGui.SameLine(0, Theme.Px(2));
            if (IconButton("remove", Icon.Trash, $"Stop using {name}: its rules leave this level (Ctrl+Z brings them back)", ImGui.GetFrameHeight()))
                feature.EditRules($"Remove style {name}", w2 => { w2.SetImports(imports.Where((_, j) => j != i)); return true; });
            ImGui.PopID();
        }
        string add = imports.Count == 0 ? "+ Use a style" : "+";
        ImGui.SameLine();
        if (ImGui.GetCursorPosX() + ImGui.CalcTextSize(add).X + ImGui.GetStyle().FramePadding.X * 2 > right) ImGui.NewLine();
        if (ImGui.Button(add)) ImGui.OpenPopup("##styles");
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
        if (imports.Count == 0) _editStyle = false;
    }

    void OpenFile(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception) { /* nothing to open with; the path is in the tooltip */ }
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
        Wrapped("No dressing yet. Dressing turns the blockout into the shipped level: trims along walls, bevelled corners, railings from boxes, props on floors.", Theme.Text);
        Wrapped("Quickest start: select some walls and press Ctrl+Shift+T (Dress with). Or add a recipe here, or use a style above.", Theme.TextDim);
        ImGui.Dummy(Theme.Px(0, 6));
        if (ImGui.BeginChild("##recipes"))
            foreach (var recipe in Recipes.All)
            {
                ImGui.PushID(recipe.Id);
                ImGui.AlignTextToFramePadding();
                if (ImGui.Button("Add", new Vector2(Theme.Px(56), 0))) AddRecipe(ws, recipe);
                ImGui.SameLine();
                ImGui.BeginGroup();
                ImGui.Text(recipe.Label);
                ImGui.SameLine();
                ImGui.TextColored(Theme.TextDisabled, $"{recipe.Kind.DisplayName}, {Selects(recipe)}");
                ImGui.PushTextWrapPos(0);
                ImGui.TextColored(Theme.TextDim, recipe.Description);
                ImGui.PopTextWrapPos();
                ImGui.EndGroup();
                ImGui.Dummy(Theme.Px(0, 4));
                ImGui.PopID();
            }
        ImGui.EndChild();
    }

    /// <summary>"upright faces tagged wall", "meshes tagged railing".</summary>
    static string Selects(Recipe recipe)
    {
        if (recipe.MeshTag) return $"meshes tagged {recipe.Tag}";
        string? facing = ParamJson.Text(recipe.Rule()["faces"]?["facing"]);
        return $"{(facing == null ? "" : facing + " ")}faces tagged {recipe.Tag}";
    }

    void AddRecipe(RulesWorkspace ws, Recipe recipe)
    {
        string id = ws.UniqueId(recipe.Id);
        if (feature.EditRules($"Add rule {recipe.Label}", w => { w.Add(id, recipe.Rule()); return true; })) _selected = id;
    }

    // ---- List ----

    string? ListPane(EditorContext ctx, RulesWorkspace ws, DressedLevel? level, Vector2 size)
    {
        string? hovered = null;
        if (ws.Rules.Count > 6)
        {
            ImGui.SetNextItemWidth(size.X);
            ImGui.InputTextWithHint("##filter", "Filter rules", ref _filter, 64);
        }
        else _filter = "";
        float pinned = ImGui.GetFrameHeightWithSpacing();
        float listH = size.Y - pinned - (ws.Rules.Count > 6 ? ImGui.GetFrameHeightWithSpacing() : 0);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, Theme.Bg);
        if (ImGui.BeginChild("##rules", new Vector2(size.X, listH)))
        {
            string? group = "\0";
            var broken = level?.Rules.Broken ?? [];
            bool anyImported = ws.Rules.Any(r => !r.Local);
            foreach (var rule in ws.Rules)
            {
                if (_filter.Length > 0 && !rule.Id.Contains(_filter.Trim(), StringComparison.OrdinalIgnoreCase)
                    && !(rule.KindInfo?.DisplayName.Contains(_filter.Trim(), StringComparison.OrdinalIgnoreCase) ?? false)) continue;
                string? g = rule.Local ? null : rule.DefinedIn;
                if (anyImported && g != group)
                {
                    group = g;
                    GroupHeader(g == null ? "This level" : $"From {StyleName(g)}");
                }
                if (RuleRow(rule, level, broken.Contains(rule.Id))) hovered = rule.Id;
            }
        }
        ImGui.EndChild();
        ImGui.PopStyleColor();

        // Pinned under the list on one row, so they never scroll away.
        float half = ws.Vars.Count > 0 ? MathF.Floor((size.X - ImGui.GetStyle().ItemSpacing.X) * 0.5f) : size.X;
        if (ws.Vars.Count > 0)
        {
            bool sel = _selected == VarsRow;
            if (sel) ImGui.PushStyleColor(ImGuiCol.Button, Theme.Raised);
            if (ImGui.Button($"Shared ({ws.Vars.Count})", new Vector2(half, 0))) _selected = VarsRow;
            if (sel) ImGui.PopStyleColor();
            Tip("Shared values: values rules use by name, e.g. one trim material for every trim rule");
            ImGui.SameLine();
        }
        if (ImGui.Button("+ Add rule", new Vector2(ws.Vars.Count > 0 ? -1 : size.X, 0))) ImGui.OpenPopup("##add");
        Tip("Add a rule from a recipe. Or select geometry and use Dress with (Ctrl+Shift+T), which also tags it.");
        if (ImGui.BeginPopup("##add"))
        {
            foreach (var recipe in Recipes.All)
            {
                ImGui.PushID(recipe.Id);
                var p = ImGui.GetCursorScreenPos();
                if (ImGui.Selectable("##r", false, ImGuiSelectableFlags.None, new Vector2(Theme.Px(320), ImGui.GetTextLineHeight() * 2 + Theme.Px(4)))) AddRecipe(ws, recipe);
                var dl = ImGui.GetWindowDrawList();
                dl.AddText(p + Theme.Px(4, 1), ImGui.ColorConvertFloat4ToU32(Theme.Text), recipe.Label);
                dl.AddText(p + new Vector2(Theme.Px(4), ImGui.GetTextLineHeight() + Theme.Px(2)), ImGui.ColorConvertFloat4ToU32(Theme.TextDim), $"{recipe.Kind.DisplayName}, {Selects(recipe)}");
                Tip(recipe.Description);
                ImGui.PopID();
            }
            ImGui.EndPopup();
        }
        return hovered;
    }

    static void GroupHeader(string text)
    {
        ImGui.Dummy(Theme.Px(0, 2));
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Theme.Px(6));
        ImGui.TextColored(Theme.TextDisabled, text);
    }

    /// <summary>Switch; name over kind; what it made on the right. Returns whether the row is hovered.</summary>
    bool RuleRow(RuleEntry rule, DressedLevel? level, bool broken)
    {
        ImGui.PushID(rule.Id);
        float line = ImGui.GetTextLineHeight();
        float h = line * 2 + Theme.Px(8);
        var start = ImGui.GetCursorScreenPos();
        float w = ImGui.GetContentRegionAvail().X;
        bool selected = _selected == rule.Id;
        ImGui.SetNextItemAllowOverlap();
        if (ImGui.Selectable("##row", false, ImGuiSelectableFlags.None, new Vector2(w, h))) _selected = rule.Id;
        bool hovered = ImGui.IsItemHovered();
        var dl = ImGui.GetWindowDrawList();
        if (selected) SelectedRow(dl, start, start + new Vector2(w, h));

        ImGui.SetCursorScreenPos(start + new Vector2(Theme.Px(4), (h - ImGui.GetFrameHeight()) * 0.5f));
        bool on = rule.Enabled;
        if (ImGui.Checkbox("##on", ref on)) SetEnabled(rule, on);
        Tip(on ? "Switch this rule off" : "Switch this rule on");

        var stats = level?.Stats.FirstOrDefault(s => s.Rule == rule.Id);
        string right = broken ? "error" : !rule.Enabled ? "off" : stats == null ? "" : stats.Outputs == 0 ? "no matches"
            : $"{stats.Outputs} mesh{(stats.Outputs == 1 ? "" : "es")}";
        float rw = ImGui.CalcTextSize(right).X;
        float x = ImGui.GetItemRectMax().X + Theme.Px(8);
        float y = start.Y + Theme.Px(4);
        dl.PushClipRect(new Vector2(x, start.Y), new Vector2(start.X + w - rw - Theme.Px(10), start.Y + h), true);
        dl.AddText(new Vector2(x, y), RowText(selected, dim: !rule.Enabled), rule.Id);
        string sub = (rule.KindInfo?.DisplayName ?? rule.Kind ?? "?") + (rule.ChangedInLevel ? ", changed here" : "");
        dl.AddText(new Vector2(x, y + line), selected ? Theme.OnAccentU32 : ImGui.ColorConvertFloat4ToU32(Theme.TextDim), sub);
        dl.PopClipRect();
        uint rightCol = broken ? ImGui.ColorConvertFloat4ToU32(Theme.Error) : RowText(selected, dim: true);
        dl.AddText(new Vector2(start.X + w - rw - Theme.Px(6), y), rightCol, right);
        if (hovered)
            ImGui.SetTooltip((rule.Local ? "" : $"From the style {StyleName(rule.DefinedIn)}{(rule.ChangedInLevel ? ", changed in this level" : "")}\n")
                + (rule.KindInfo?.Summary ?? "") + (stats is { Enabled: true } ? $"\nMade {stats.Outputs} mesh{(stats.Outputs == 1 ? "" : "es")} from {stats.Targets} {stats.TargetNoun}{(stats.Targets == 1 ? "" : "s")}. Hovering tints what it takes." : ""));
        ImGui.SetCursorScreenPos(start + new Vector2(0, h + Theme.Px(1)));
        ImGui.Dummy(Vector2.Zero);
        ImGui.PopID();
        return hovered;
    }

    void SetEnabled(RuleEntry rule, bool on) =>
        feature.EditRules(on ? $"Switch on {rule.Id}" : $"Switch off {rule.Id}", w => w.SetEnabled(rule.Id, on, _editStyle && !rule.Local));

    // ---- Details ----

    static float LabelW => Theme.Px(92);

    /// <summary>After a row's label: its field beside it, or on the next line in a narrow panel (<see cref="RuleFields.StackLabels"/>).</summary>
    static void AfterLabel()
    {
        if (!RuleFields.StackLabels(RuleFields.ContentWidth)) ImGui.SameLine(LabelW);
    }

    void Details(EditorContext ctx, RulesWorkspace ws, RuleEntry rule, DressedLevel? level)
    {
        bool style = _editStyle && !rule.Local;

        // Header: kind and what it does, then where edits go.
        ImGui.PushFont(Fonts.Strong);
        ImGui.Text(rule.KindInfo?.DisplayName ?? rule.Kind ?? "?");
        ImGui.PopFont();
        float hw = ImGui.CalcTextSize("Highlight").X + ImGui.GetFrameHeight() + ImGui.GetStyle().ItemInnerSpacing.X;
        ImGui.SameLine(MathF.Max(ImGui.GetCursorPosX(), ImGui.GetWindowContentRegionMax().X - hw));
        ImGui.Checkbox("Highlight", ref _highlightSelected);
        Tip("Tint what this rule takes in the viewport (hovering a rule in the list does too)");
        if (rule.KindInfo is { } kind) Wrapped(kind.Summary, Theme.TextDim);
        if (!rule.Local)
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.TextDim, "Changes to");
            AfterLabel();
            int target = TextSegments("##target", [("This level", "Changes apply to this level only. Values that differ from the style show in orange."),
                ("Shared style", $"Changes go to the style file ({StyleName(rule.DefinedIn)}) and apply to every level that uses it.")], style ? 1 : 0);
            if (target >= 0) _editStyle = target == 1;
        }
        ImGui.TextColored(Theme.TextDisabled, $"Saves to {Path.GetFileName(style ? rule.DefinedIn : ws.MainPath)}");

        // Problems first, where they are seen.
        var problems = level?.Diagnostics.Where(d => d.Rule == rule.Id).ToList() ?? [];
        if (problems.Count > 0)
        {
            SectionDivider();
            foreach (var d in problems)
            {
                ImGui.PushTextWrapPos(0);
                ImGui.TextColored(d.IsError ? Theme.Error : Theme.Hover, d.Message);
                if (d.Fix != null) ImGui.TextColored(Theme.TextDim, d.Fix);
                ImGui.PopTextWrapPos();
                if (d.MeshId is { } id && ctx.Doc.Find(id) is { } mesh)
                {
                    if (ImGui.SmallButton($"Select {mesh.Name}##{d.GetHashCode()}")) { ctx.SetMode(SelectMode.Object); ctx.Sel.Clear(); ctx.Sel.Add(ElementRef.Object(id)); }
                }
            }
        }
        SectionDivider();

        // Name (renames carry the level's per-mesh overrides and skip: tags along) and note.
        if (_renameFor != rule.Id) { _renameFor = rule.Id; _renameDraft = rule.Id; }
        bool canRename = rule.Local || style;
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.TextDim, "Name");
        AfterLabel();
        if (!canRename) ImGui.BeginDisabled();
        ImGui.SetNextItemWidth(-Theme.Px(24) - ImGui.GetStyle().ItemSpacing.X);
        ImGui.InputText("##name", ref _renameDraft, Core.Geometry.TagSet.MaxLength + 8);
        if (ImGui.IsItemDeactivatedAfterEdit()) Rename(ctx, rule, style);
        if (!canRename) ImGui.EndDisabled();
        Tip(canRename ? "Lowercase letters, digits, - and _. Renaming keeps the level's per-mesh changes and skip: tags."
            : "A style's rule: choose \"Shared style\" above to rename it (other levels using the style keep the old name)");
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.TextDim, "Note");
        AfterLabel();
        string note = _noteDraft ?? rule.Note ?? "";
        ImGui.SetNextItemWidth(-Theme.Px(24) - ImGui.GetStyle().ItemSpacing.X);
        if (ImGui.InputTextWithHint("##note", "Designer note", ref note, 512)) _noteDraft = note;
        if (ImGui.IsItemDeactivatedAfterEdit() && _noteDraft != null)
        {
            var text = _noteDraft.Trim();
            feature.EditRules($"Note on {rule.Id}", w => w.Set(rule.Id, "note", text.Length == 0 ? null : JsonValue.Create(text), style));
        }
        if (!ImGui.IsItemActive()) _noteDraft = null;
        Tip("For people and agents reading the rules. Chip ignores it.");
        SectionDivider();

        _fields.Draw(ctx, ws, rule, style, _levelTags);

        SectionDivider();
        Actions(ctx, ws, rule, level, style);
    }

    void Actions(EditorContext ctx, RulesWorkspace ws, RuleEntry rule, DressedLevel? level, bool style)
    {
        var (tag, meshTag) = ParamJson.PrimaryTag(rule.Raw, ws.Resolve);
        bool canDress = tag != null && TagOps.CanTag(ctx.Sel.Mode) && !ctx.Sel.IsEmpty;
        if (!canDress) ImGui.BeginDisabled();
        if (ImGui.Button("Dress selection")) DressWithPopup.Apply(ctx, feature, rule, ws);
        if (!canDress) ImGui.EndDisabled();
        Tip(tag == null ? "This rule picks faces without a tag (by facing or material), so there's nothing to add"
            : ctx.Sel.IsEmpty ? "Select faces or meshes first"
            : $"Dress the selection with this rule (tags it {tag}{(meshTag ? ", the meshes" : ctx.Sel.Mode == SelectMode.Object ? ", the faces it can use" : "")}). Again takes it out of this rule only.");
        SameLineIfFits("Select matches");
        if (ImGui.Button("Select matches")) SelectTargets(ctx, level, rule);
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
            : "A style's rule: switch it off for this level, or choose \"Shared style\" above to delete it from the style");
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

    void Vars(EditorContext ctx, RulesWorkspace ws)
    {
        ImGui.PushFont(Fonts.Strong);
        ImGui.Text("Shared values");
        ImGui.PopFont();
        Wrapped("Values rules use by name. Change one here and every rule using it follows. A style's value changed here applies to this level only.", Theme.TextDim);
        SectionDivider();
        foreach (var (name, (value, definedIn)) in ws.Vars.OrderBy(v => v.Key, StringComparer.Ordinal))
        {
            ImGui.PushID(name);
            bool fromStyle = !string.Equals(definedIn, ws.MainPath, StringComparison.OrdinalIgnoreCase);
            bool overridden = !fromStyle && ws.ImportPaths.Count > 0 && StyleHas(ws, name);
            var users = Users(ws, name);
            bool length = users.Count > 0 && users.All(u => u.Length);
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(overridden ? Theme.Accent : Theme.TextDim, name);
            Tip((fromStyle ? $"From the style {StyleName(definedIn)}" : overridden ? "This level's value (the style has another)" : "Set in this level")
                + (users.Count == 0 ? "\nNo rule uses it." : $"\nUsed by {string.Join(", ", users.Select(u => u.Where).Distinct())}"));
            AfterLabel();
            float w = ImGui.GetContentRegionAvail().X - Theme.Px(24) - ImGui.GetStyle().ItemSpacing.X;
            ImGui.SetNextItemWidth(w);
            double? number = ParamJson.Number(value);
            string text = number is { } n ? (length ? $"{n * 100:0.##} cm" : n.ToString(System.Globalization.CultureInfo.InvariantCulture))
                : ParamJson.Text(value) ?? value?.ToJsonString() ?? "";
            string shown = _varDrafts.TryGetValue(name, out var d) ? d : text;
            if (ImGui.InputText("##v", ref shown, 256)) _varDrafts[name] = shown;
            if (ImGui.IsItemDeactivatedAfterEdit() && _varDrafts.Remove(name, out var typed) && typed != text)
            {
                if (Parse(typed, length, number != null) is { } next) feature.EditRules($"Shared value {name}", w2 => { w2.SetVar(name, next); return true; });
                else ctx.Status($"\"{typed}\" isn't a value {name} can take{(length ? " (a length, like 12 cm)" : "")}", error: true);
            }
            Tip(length ? "A length in centimetres" : number != null ? "A number" : "Text, or JSON for a list or object");
            ImGui.SameLine();
            if (overridden)
            {
                if (ImGui.Button("×", new Vector2(Theme.Px(24), 0))) feature.EditRules($"Reset shared value {name}", w2 => { w2.SetVar(name, null); return true; });
                Tip("Back to the style's value");
            }
            else ImGui.Dummy(new Vector2(Theme.Px(24), ImGui.GetFrameHeight()));
            ImGui.PopID();
        }
    }

    /// <summary>Reads a typed shared value: "12 cm" for lengths, numbers, JSON lists and objects, or text. Null if it can't be.</summary>
    static JsonNode? Parse(string typed, bool length, bool numeric)
    {
        var t = typed.Trim();
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        string num = t.EndsWith("cm", StringComparison.OrdinalIgnoreCase) ? t[..^2].Trim() : t.EndsWith('m') ? t[..^1].Trim() : t;
        if (double.TryParse(num, System.Globalization.NumberStyles.Float, inv, out var v))
            return ParamJson.Num(length && !t.EndsWith('m') || t.EndsWith("cm", StringComparison.OrdinalIgnoreCase) ? v / 100 : v);
        if (numeric) return null;
        if (t.StartsWith('[') || t.StartsWith('{'))
        {
            try { return JsonNode.Parse(t); }
            catch (System.Text.Json.JsonException) { return null; }
        }
        return JsonValue.Create(t);
    }

    /// <summary>Where a shared value is used ("baseboard material") and whether that place is a length.</summary>
    static List<(string Where, bool Length)> Users(RulesWorkspace ws, string name)
    {
        var result = new List<(string, bool)>();
        string token = "$" + name;
        foreach (var rule in ws.Rules)
            foreach (var (key, value) in rule.Raw)
            {
                bool uses = value is JsonValue jv && ParamJson.Text(jv) == token
                    || value?.ToJsonString().Contains($"\"{token}\"", StringComparison.Ordinal) == true;
                if (!uses) continue;
                var spec = rule.KindInfo?.Params.FirstOrDefault(p => p.Name == key);
                bool length = spec != null && (spec.Unit == ParamUnit.Length || spec.Type == ParamType.Profile);
                result.Add(($"{rule.Id} {spec?.DisplayLabel.ToLowerInvariant() ?? key}", length));
            }
        return result;
    }

    static bool StyleHas(RulesWorkspace ws, string name) =>
        ws.ImportPaths.Any(p => ws.Doc(p)?.Vars.Any(v => v.Name == name) == true);

    // ---- Footer ----

    void Footer(EditorContext ctx, RulesWorkspace ws, DressedLevel? level)
    {
        ImGui.AlignTextToFramePadding();
        int errors = level?.ErrorCount ?? 0, warnings = level?.WarningCount ?? 0;
        int on = ws.Rules.Count(r => r.Enabled);
        string summary = $"{on} of {ws.Rules.Count} rule{(ws.Rules.Count == 1 ? "" : "s")} on"
            + (errors > 0 ? $", {errors} error{(errors == 1 ? "" : "s")}" : "") + (warnings > 0 ? $", {warnings} warning{(warnings == 1 ? "" : "s")}" : "");
        ImGui.TextColored(errors > 0 ? Theme.Error : Theme.TextDim, summary);
        if (errors + warnings > 0)
        {
            Tip("Click to go to the first rule with a problem");
            if (ImGui.IsItemClicked() && level!.Diagnostics.FirstOrDefault(d => d.Rule != null) is { } first) _selected = first.Rule;
        }
        if (feature.LastSaved is { } saved && feature.Now - saved.At < 3)
        {
            ImGui.SameLine(0, Theme.Px(16));
            ImGui.TextColored(Theme.TextDim, $"Saved {saved.Files}");
        }
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
