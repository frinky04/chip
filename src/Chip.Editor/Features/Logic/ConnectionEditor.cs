using System.Numerics;
using System.Text.Json;
using Chip.Core.Scene;
using Chip.Editor.Features.Scene;
using Chip.Editor.Framework;
using Chip.Editor.UI;
using ImGuiNET;
using static Chip.Editor.UI.Widgets;

namespace Chip.Editor.Features.Logic;

/// <summary>
/// The fields of one connection, under the table that lists it: its output, target, input, arguments, delay, once and
/// condition, each a pick from what that end can take (the class's outputs, the target's inputs and their typed
/// arguments, the project's flags), so a connection can't name an input that doesn't exist. Also the target menu new
/// connections start from, and the time axis of a timeline output. Every committed change is one undo step.
/// </summary>
public sealed class ConnectionEditor
{
    readonly ValueFields _fields = new();
    string _filter = "";
    // A delay being dragged (in the field or on a timeline): its connection and the delay it shows until the mouse lets go.
    (int Entity, int Connection)? _dragging;
    float _dragDelay;

    /// <summary>Edits connection <paramref name="index"/> of <paramref name="e"/> (which may be another entity than the selection).</summary>
    public void Draw(EditorContext ctx, Entity e, int index)
    {
        if (index >= e.Outputs.Count) return;
        var c = e.Outputs[index];
        var cls = EntityClasses.Get(e.ClassName);
        var output = cls.Output(c.On);
        float button = ImGui.GetFrameHeight();
        float gap = ImGui.GetStyle().ItemSpacing.X;
        ImGui.PushID($"io:{e.Id}/{index}");

        Label("Output", OutputTip(e, c.On, output));
        OutputCombo(ctx, e, index, c, cls, output);

        Label("Target", "What this connection acts on");
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - button - gap);
        TargetCombo(ctx, e, index, c);
        ImGui.SameLine(0, gap);
        string owner = $"io:{e.Id}/{index}";
        if (IconButton("pick", Icon.Target, "Pick the target in the viewport", button))
        {
            if (LogicFeature.Picker.Armed(ctx, owner)) LogicFeature.Picker.Cancel(ctx);
            else LogicFeature.Picker.Start(ctx, owner, "an entity with inputs",
                other => other.Id != e.Id && EntityClasses.Get(other.ClassName).Inputs.Count > 0,
                other => Retarget(ctx, e, index, new ConnectionTarget(TargetKind.Entity, other.Id)));
        }

        var inputs = Inputs(ctx, e, c.Target);
        var input = inputs.FirstOrDefault(i => i.Name == c.Do);
        Label("Input", "What the target does");
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
        InputCombo(ctx, e, index, c, inputs, input);
        if (input != null)
            foreach (var p in input.Params) Argument(ctx, e, index, c, output, p);

        Label("Delay", "Seconds after the output fires. 0: in the same frame, after the output's other connections");
        ImGui.SetNextItemWidth(Theme.Px(80));
        float delay = _dragging == (e.Id, index) ? _dragDelay : c.Delay;
        if (ImGui.DragFloat("##delay", ref delay, 0.02f, 0, 3600, "%.2f s", ImGuiSliderFlags.AlwaysClamp)) { _dragging = (e.Id, index); _dragDelay = delay; }
        if (ImGui.IsItemDeactivatedAfterEdit() && _dragging == (e.Id, index))
        {
            _dragging = null;
            float d = MathF.Round(_dragDelay, 3);
            Change(ctx, e, index, "Set Delay", x => x.Outputs[index].Delay = d);
        }
        ImGui.SameLine(0, gap * 2);
        bool once = c.Once;
        if (ImGui.Checkbox("Once", ref once)) Change(ctx, e, index, once ? "Fire Once" : "Fire Every Time", x => x.Outputs[index].Once = once);
        Tip("Fires the first time only; after that this connection does nothing");

        Condition(ctx, e, index, c);
        ImGui.PopID();
    }

    static void Label(string text, string tip)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.TextDim, text);
        Tip(tip);
        ImGui.SameLine(PropertyColumn);
    }

    public static string OutputTip(Entity e, string name, OutputSpec? output)
    {
        if (output == null) return $"{EntityClasses.Get(e.ClassName).Label} has no output called {name}, so this never fires. Pick one it has";
        var lines = new List<string>();
        if (output.Doc.Length > 0) lines.Add(output.Doc);
        if (output.Payload is { } payload) lines.Add($"Passes a {EntityValues.TypeName(payload)} along, which an argument can take (←)");
        if (output.Timeline) lines.Add("A timeline: each connection plays at its own delay after the start");
        if (output.Required) lines.Add("The game expects this to be connected");
        return string.Join("\n", lines.DefaultIfEmpty(name));
    }

    void OutputCombo(EditorContext ctx, Entity e, int index, Connection c, EntityClass cls, OutputSpec? output)
    {
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
        if (output == null) ImGui.PushStyleColor(ImGuiCol.Text, Theme.Error);
        bool open = ImGui.BeginCombo("##on", output?.Label ?? EntityClass.Humanize(c.On));
        if (output == null) ImGui.PopStyleColor();
        if (!open) return;
        foreach (var o in cls.Outputs)
        {
            if (ImGui.Selectable(o.Label, o == output) && o != output)
            {
                var name = o.Name;
                // A payload binding means nothing on an output that carries no such value.
                Change(ctx, e, index, "Change Output", x =>
                {
                    x.Outputs[index].On = name;
                    if (o.Payload == null) foreach (var key in x.Outputs[index].Args.Where(a => IsPayload(a.Value)).Select(a => a.Key).ToList()) x.Outputs[index].Args.Remove(key);
                });
            }
            Tip(OutputTip(e, o.Name, o));
        }
        if (cls.Outputs.Count == 0) ImGui.TextColored(Theme.TextDisabled, $"{cls.Label} has no outputs");
        ImGui.EndCombo();
    }

    /// <summary>
    /// Adds a connection on <paramref name="on"/> to <paramref name="target"/>, doing the target's first input, and
    /// returns its index. A timeline's new cue lands half a second after its last, so new cues don't stack on zero.
    /// </summary>
    public static int? Add(EditorContext ctx, Entity e, string on, ConnectionTarget target)
    {
        var inputs = Inputs(ctx, e, target);
        var c = new Connection { On = on, Target = target, Do = inputs.FirstOrDefault()?.Name ?? "" };
        if (EntityClasses.Get(e.ClassName).Output(on) is { Timeline: true } && e.Outputs.Where(x => x.On == on).Select(x => x.Delay).DefaultIfEmpty(-1).Max() is var last and >= 0)
            c.Delay = MathF.Round(last + 0.5f, 2);
        int index = e.Outputs.Count;
        return EntitySection.Edit(ctx, e.Id, "Add Connection", x => { x.Outputs.Add(c); return null; }) ? index : null;
    }

    // ---- Target ----

    void TargetCombo(EditorContext ctx, Entity e, int index, Connection c)
    {
        var (text, ok) = TargetText(ctx.Doc, e, c);
        if (LogicFeature.Picker.Armed(ctx, $"io:{e.Id}/{index}")) (text, ok) = ("Click one in the viewport…", true);
        if (!ok) ImGui.PushStyleColor(ImGuiCol.Text, Theme.Error);
        bool open = ImGui.BeginCombo("##target", text, ImGuiComboFlags.HeightLarge);
        if (!ok) ImGui.PopStyleColor();
        if (!open)
        {
            if (ImGui.IsItemHovered() && c.Target.Kind == TargetKind.Entity) LinkDraw.Highlight = c.Target.Entity;
            Tip(TargetTip(c));
            return;
        }
        if (ImGui.IsWindowAppearing()) _filter = "";
        if (TargetMenu(ctx, e, c.Target) is { } picked && picked != c.Target) Retarget(ctx, e, index, picked);
        ImGui.EndCombo();
    }

    /// <summary>The target menu in a popup, with an option to close it and pick in the viewport instead.</summary>
    public ConnectionTarget? TargetMenuPopup(EditorContext ctx, Entity e, string id, ConnectionTarget? current, Action pickInViewport)
    {
        if (!ImGui.BeginPopup(id)) return null;
        if (ImGui.IsWindowAppearing()) _filter = "";
        if (ImGui.Selectable("Pick target in viewport…"))
        {
            ImGui.CloseCurrentPopup();
            pickInViewport();
            ImGui.EndPopup();
            return null;
        }
        Tip("Use the eyedropper to choose an entity with inputs");
        ImGui.Separator();
        var picked = TargetMenu(ctx, e, current);
        if (picked != null) ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
        return picked;
    }

    /// <summary>
    /// Everything a connection can aim at: itself, whoever started the chain, the entities that have inputs, every
    /// entity carrying a tag, and the project's flags. Searchable; hovering an entity rings it in the viewport.
    /// </summary>
    ConnectionTarget? TargetMenu(EditorContext ctx, Entity e, ConnectionTarget? current)
    {
        ConnectionTarget? picked = null;
        if (ImGui.IsWindowAppearing()) ImGui.SetKeyboardFocusHere();
        ImGui.SetNextItemWidth(Math.Max(Theme.Px(220), ImGui.GetContentRegionAvail().X));
        ImGui.InputTextWithHint("##filter", "Search", ref _filter, 64);
        bool Match(string s) => _filter.Length == 0 || s.Contains(_filter, StringComparison.OrdinalIgnoreCase);
        void Item(string label, ConnectionTarget target, string tip, int? highlight = null)
        {
            if (!Match(label)) return;
            if (ImGui.Selectable(label, target == current)) picked = target;
            if (ImGui.IsItemHovered() && highlight != null) LinkDraw.Highlight = highlight;
            Tip(tip);
        }

        Item("Self", new(TargetKind.Special, Name: "self"), $"This {EntityClasses.Get(e.ClassName).Label.ToLowerInvariant()}");
        Item("Activator", new(TargetKind.Special, Name: "activator"), "Whoever started the chain, usually the player. Known only in the game, so any input name goes");
        Item("Caller", new(TargetKind.Special, Name: "caller"), "The entity whose output fired this connection, when the game forwards a chain. Known only in the game");

        var entities = ctx.Doc.Entities.Where(o => o.Id != e.Id && EntityClasses.Get(o.ClassName).Inputs.Count > 0)
            .Select(o => (Entity: o, Label: Describe(o))).Where(x => Match(x.Label)).ToList();
        if (entities.Count > 0) Header("Entities");
        foreach (var (o, label) in entities)
            Item($"{label}##{o.Id}", new(TargetKind.Entity, o.Id), $"{SceneOps.DisplayName(o)}: what its {EntityClasses.Get(o.ClassName).Label.ToLowerInvariant()} can do", o.Id);

        var tags = ctx.Doc.Entities.SelectMany(o => o.Tags).Distinct().Order(StringComparer.Ordinal).Where(Match).ToList();
        if (tags.Count > 0) Header("Tagged");
        foreach (var tag in tags)
        {
            int n = ctx.Doc.Entities.Count(o => o.Tags.Contains(tag));
            Item($"{tag}  ({n})##tag:{tag}", new(TargetKind.Tag, Name: tag), $"Every entity tagged {tag} when it runs ({n} now). Offers the inputs they all have");
        }

        var flags = EntityClasses.Project?.Flags.Values.Where(f => Match(f.Name)).OrderBy(f => f.Name, StringComparer.Ordinal).ToList() ?? [];
        if (flags.Count > 0) Header("Flags");
        foreach (var f in flags)
            Item($"{f.Name}  ({f.Type.ToString().ToLowerInvariant()})##flag:{f.Name}", new(TargetKind.Flag, Name: f.Name),
                (f.Doc.Length > 0 ? f.Doc + "\n" : "") + $"A {f.Type.ToString().ToLowerInvariant()} flag, kept for the {(f.Scope == FlagScope.Game ? "whole game" : "level")}");
        return picked;
    }

    static void Header(string text)
    {
        ImGui.Dummy(Theme.Px(0, 1));
        ImGui.TextColored(Theme.TextDisabled, text);
    }

    static string Describe(Entity o)
    {
        string label = SceneOps.DisplayName(o);
        return label != o.ClassName ? $"{label}  ({EntityClasses.Get(o.ClassName).Label})" : label;
    }

    /// <summary>The target in words, and whether it names something that exists.</summary>
    public static (string Text, bool Ok) TargetText(Document doc, Entity e, Connection c)
    {
        if (c.UnknownTarget != null) return ("Unknown target", false);
        var t = c.Target;
        return t.Kind switch
        {
            TargetKind.Entity => doc.FindEntity(t.Entity) is { } o ? (SceneOps.DisplayName(o), true) : ($"Missing entity {t.Entity}", false),
            TargetKind.Tag => ($"Tagged {t.Name} ({doc.Entities.Count(o => o.Tags.Contains(t.Name))})", doc.Entities.Any(o => o.Tags.Contains(t.Name))),
            TargetKind.Flag => ($"Flag {t.Name}", EntityClasses.Project?.Flags.ContainsKey(t.Name) == true),
            _ => (EntityClass.Humanize(t.Name), t.Name is "self" or "activator" or "caller"),
        };
    }

    static string TargetTip(Connection c) => c.UnknownTarget != null
        ? $"A target this Chip doesn't know ({c.UnknownTarget.Value.GetRawText()}). It's kept as it is"
        : c.Target.Kind switch
        {
            TargetKind.Tag => "Every entity carrying the tag at the moment it runs",
            TargetKind.Flag => "A project flag (chip.project.json)",
            TargetKind.Special when c.Target.Name == "activator" => "Whoever started the chain, usually the player",
            TargetKind.Special when c.Target.Name == "caller" => "The entity whose output fired this connection",
            _ => "Pick what this connection acts on",
        };

    /// <summary>Points the connection somewhere else, keeping its input and arguments where the new target has them.</summary>
    static void Retarget(EditorContext ctx, Entity e, int index, ConnectionTarget target)
    {
        var inputs = Inputs(ctx, e, target);
        Change(ctx, e, index, "Change Target", x =>
        {
            var c = x.Outputs[index];
            c.Target = target;
            c.UnknownTarget = null;
            var input = inputs.FirstOrDefault(i => i.Name == c.Do) ?? inputs.FirstOrDefault();
            if (input == null) return;
            if (input.Name != c.Do) c.Args.Clear();
            c.Do = input.Name;
            foreach (var key in c.Args.Keys.Where(k => input.Params.All(p => p.Name != k)).ToList()) c.Args.Remove(key);
        });
    }

    // ---- Input and arguments ----

    /// <summary>
    /// The inputs a target offers. Activator and caller are only known in the game, so they offer every input any class
    /// has, the first class's version of each name.
    /// </summary>
    public static IReadOnlyList<InputSpec> Inputs(EditorContext ctx, Entity e, ConnectionTarget target) =>
        target is { Kind: TargetKind.Special, Name: "activator" or "caller" }
            ? EntityClasses.Current.Classes.SelectMany(k => k.Inputs).DistinctBy(i => i.Name).OrderBy(i => i.Name, StringComparer.Ordinal).ToList()
            : LogicOps.InputsFor(ctx.Doc, EntityClasses.Current, EntityClasses.Project, e, target);

    static void InputCombo(EditorContext ctx, Entity e, int index, Connection c, IReadOnlyList<InputSpec> inputs, InputSpec? input)
    {
        bool ok = input != null;
        string preview = input?.Label ?? (c.Do.Length > 0 ? EntityClass.Humanize(c.Do) : inputs.Count == 0 ? "Nothing to do" : "Pick an input");
        if (!ok) ImGui.PushStyleColor(ImGuiCol.Text, Theme.Error);
        bool open = ImGui.BeginCombo("##do", preview, ImGuiComboFlags.HeightLarge);
        if (!ok) ImGui.PopStyleColor();
        if (!open)
        {
            Tip(input != null ? (input.Doc.Length > 0 ? input.Doc + "\n" : "") + $"{input.Name}" + (input.Params.Count > 0 ? $"({string.Join(", ", input.Params.Select(p => p.Name))})" : "")
                : inputs.Count == 0 ? "The target takes no inputs. Pick another target"
                : c.Do.Length > 0 ? $"The target has no input called {c.Do}. Pick one it has" : "Pick what the target does");
            return;
        }
        foreach (var i in inputs)
        {
            if (ImGui.Selectable(i.Label, i == input) && i != input)
            {
                var name = i.Name;
                Change(ctx, e, index, "Change Input", x => { x.Outputs[index].Do = name; x.Outputs[index].Args.Clear(); });
            }
            if (i.Doc.Length > 0) Tip(i.Doc);
        }
        if (inputs.Count == 0) ImGui.TextColored(Theme.TextDisabled, "The target takes no inputs");
        ImGui.EndCombo();
    }

    void Argument(EditorContext ctx, Entity e, int index, Connection c, OutputSpec? output, PropSpec p)
    {
        ImGui.PushID($"arg:{p.Name}");
        float button = ImGui.GetFrameHeight();
        float gap = ImGui.GetStyle().ItemSpacing.X;
        bool set = c.Args.TryGetValue(p.Name, out var own);
        bool bound = set && IsPayload(own);
        bool canBind = output?.Payload is { } payload && payload.Type == p.Type && payload.ItemType == p.ItemType;

        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(set ? Theme.Accent : Theme.TextDim, Ellipsize(p.Label, PropertyColumn - ImGui.GetCursorPosX() - Theme.Px(4)));
        Tip((p.Doc.Length > 0 ? p.Doc + "\n" : "") + $"{p.Name}: {EntityValues.TypeName(p)}" + (set ? "" : $". Default: {EntityValues.Text(p.Default)}"));
        ImGui.SameLine(PropertyColumn);
        float fieldW = ImGui.GetContentRegionAvail().X - (canBind || bound ? button + gap : 0);
        ImGui.SetNextItemWidth(fieldW);
        if (bound)
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(ImGui.ColorConvertU32ToFloat4(LinkDraw.OutColor), Ellipsize("← output's value", fieldW));
            Tip(canBind
                ? $"Takes the {(output?.Payload is { } pp ? EntityValues.TypeName(pp) : "value")} that {output!.Label.ToLowerInvariant()} passes along"
                : "The output passes no value of this type any more. Set one instead");
            ImGui.SameLine(PropertyColumn + fieldW);
        }
        else if (p.Type == PropType.Array) ValueFields.ListCount(set ? own : p.Default);
        else _fields.Draw(ctx, e, p, set ? own : p.Default, $"io{index}/{p.Name}", fieldW, v => SetArgument(ctx, e, index, p, v));
        if (canBind || bound)
        {
            ImGui.SameLine(0, gap);
            if (bound) PushPrimary();
            bool click = ImGui.Button("←", new Vector2(button, 0));
            if (bound) PopPrimary();
            Tip(bound ? "Set a value instead of the output's" : $"Take the value the output passes along ({EntityValues.TypeName(output!.Payload!)})");
            if (click)
                Change(ctx, e, index, bound ? "Unbind Argument" : "Bind Argument", x =>
                {
                    if (bound) x.Outputs[index].Args.Remove(p.Name);
                    else x.Outputs[index].Args[p.Name] = Payload;
                });
        }
        // A list argument's items follow under its row, as a list property's do.
        if (p.Type == PropType.Array && !bound)
        {
            _fields.ListItems(ctx, e, p, set ? own : p.Default, $"io{index}/{p.Name}", PropertyColumn,
                ImGui.GetContentRegionAvail().X - PropertyColumn - button - gap, button, v => SetArgument(ctx, e, index, p, v));
        }
        ImGui.PopID();
    }

    /// <summary>Sets an argument, typed by its param; a value equal to the param's default is left out.</summary>
    static void SetArgument(EditorContext ctx, Entity e, int index, PropSpec p, JsonElement v) =>
        Change(ctx, e, index, $"Set {p.Label}", x =>
        {
            if (EntityValues.Coerce(p, v, out _) is not { } typed) return;
            if (EntityValues.Same(typed, p.Default)) x.Outputs[index].Args.Remove(p.Name);
            else x.Outputs[index].Args[p.Name] = typed;
        });

    static readonly JsonElement Payload = JsonSerializer.SerializeToElement(new Dictionary<string, bool> { ["payload"] = true });

    static bool IsPayload(JsonElement v) =>
        v.ValueKind == JsonValueKind.Object && v.TryGetProperty("payload", out var b) && b.ValueKind == JsonValueKind.True;

    // ---- Condition ----

    /// <summary>"Only when" a flag holds a value (or, for a number, reaches one). Checked when the output fires.</summary>
    static void Condition(EditorContext ctx, Entity e, int index, Connection c)
    {
        var flags = EntityClasses.Project?.Flags;
        if (c.When is not { } when)
        {
            if (flags is not { Count: > 0 }) return;
            ImGui.SetCursorPosX(PropertyColumn);
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextDim);
            bool add = ImGui.SmallButton("+ Only when…");
            ImGui.PopStyleColor();
            Tip("Fire only while a flag has a value. Checked when the output fires, not when the delay ends");
            if (add)
            {
                var first = flags.Values.OrderBy(f => f.Name, StringComparer.Ordinal).First();
                var guard = new Guard(first.Name, Is: first.Type == FlagType.Bool ? EntityValues.Of(true) : first.Default);
                Change(ctx, e, index, "Add Condition", x => x.Outputs[index].When = guard);
            }
            return;
        }

        ImGui.PushID("when");
        float gap = ImGui.GetStyle().ItemSpacing.X;
        float button = ImGui.GetFrameHeight();
        var flag = flags?.GetValueOrDefault(when.Flag);
        Label("Only when", "Fires only while the flag holds; checked when the output fires");
        float rest = ImGui.GetContentRegionAvail().X - button - gap;
        float flagW = MathF.Floor(rest * 0.45f), opW = MathF.Floor(rest * 0.25f), valueW = rest - flagW - opW - gap * 2;

        ImGui.SetNextItemWidth(flagW);
        if (flag == null) ImGui.PushStyleColor(ImGuiCol.Text, Theme.Error);
        bool open = ImGui.BeginCombo("##flag", when.Flag);
        if (flag == null) ImGui.PopStyleColor();
        if (!open) Tip(flag == null ? $"The project declares no flag called {when.Flag}" : flag.Doc.Length > 0 ? flag.Doc : $"A {flag.Type.ToString().ToLowerInvariant()} flag");
        else
        {
            foreach (var f in flags?.Values.OrderBy(f => f.Name, StringComparer.Ordinal) ?? Enumerable.Empty<FlagSpec>())
                if (ImGui.Selectable(f.Name, f.Name == when.Flag) && f.Name != when.Flag)
                {
                    var guard = new Guard(f.Name, Is: f.Type == FlagType.Bool ? EntityValues.Of(true) : f.Default);
                    Change(ctx, e, index, "Change Condition", x => x.Outputs[index].When = guard);
                }
            ImGui.EndCombo();
        }

        ImGui.SameLine(0, gap);
        if (flag?.Type == FlagType.Int)
        {
            bool atLeast = when.AtLeast != null;
            ImGui.SetNextItemWidth(opW);
            if (ImGui.BeginCombo("##op", atLeast ? "≥" : "="))
            {
                if (ImGui.Selectable("= is", !atLeast) && atLeast)
                    Change(ctx, e, index, "Change Condition", x => x.Outputs[index].When = new Guard(when.Flag, Is: EntityValues.Of((long)(when.AtLeast ?? 0))));
                if (ImGui.Selectable("≥ at least", atLeast) && !atLeast)
                    Change(ctx, e, index, "Change Condition", x => x.Outputs[index].When = new Guard(when.Flag, AtLeast: (int)(EntityValues.Number(when.Is ?? EntityValues.Null) ?? 0)));
                ImGui.EndCombo();
            }
            ImGui.SameLine(0, gap);
            int n = atLeast ? when.AtLeast!.Value : (int)(EntityValues.Number(when.Is ?? EntityValues.Null) ?? 0);
            ImGui.SetNextItemWidth(valueW);
            if (ImGui.InputInt("##n", ref n, 0, 0, ImGuiInputTextFlags.EnterReturnsTrue) || ImGui.IsItemDeactivatedAfterEdit())
            {
                int value = n;
                var guard = atLeast ? new Guard(when.Flag, AtLeast: value) : new Guard(when.Flag, Is: EntityValues.Of((long)value));
                if (guard.AtLeast != when.AtLeast || !EntityValues.Same(guard.Is ?? EntityValues.Null, when.Is ?? EntityValues.Null))
                    Change(ctx, e, index, "Change Condition", x => x.Outputs[index].When = guard);
            }
        }
        else
        {
            // A bool flag: "is on" or "is off", one dropdown over both value columns.
            bool on = EntityValues.Bool(when.Is ?? EntityValues.Null) != false;
            ImGui.SetNextItemWidth(opW + gap + valueW);
            if (ImGui.BeginCombo("##is", on ? "is on" : "is off"))
            {
                if (ImGui.Selectable("is on", on) && !on) Change(ctx, e, index, "Change Condition", x => x.Outputs[index].When = new Guard(when.Flag, Is: EntityValues.Of(true)));
                if (ImGui.Selectable("is off", !on) && on) Change(ctx, e, index, "Change Condition", x => x.Outputs[index].When = new Guard(when.Flag, Is: EntityValues.Of(false)));
                ImGui.EndCombo();
            }
        }
        ImGui.SameLine(0, gap);
        if (IconButton("remove", Icon.Trash, "Fire whatever the flags say", button))
            Change(ctx, e, index, "Remove Condition", x => x.Outputs[index].When = null);
        ImGui.PopID();
    }

    // ---- Timeline ----

    /// <summary>
    /// A timeline output's connections as cues on a time axis. Drag a cue to move it in time (snapped to a twentieth of a
    /// second, Shift for free). Returns the cue clicked, so the table can select it.
    /// </summary>
    public int? Timeline(EditorContext ctx, Entity e, IReadOnlyList<int> indices, int? selected)
    {
        int? clicked = null;
        float h = Theme.Px(34);
        float w = ImGui.GetContentRegionAvail().X;
        var origin = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton("##timeline", new Vector2(w, h));
        var dl = ImGui.GetWindowDrawList();
        float pad = Theme.Px(10);
        float left = origin.X + pad, right = origin.X + w - pad;
        float end = MathF.Max(1, indices.Max(i => _dragging == (e.Id, i) ? _dragDelay : e.Outputs[i].Delay));
        end = MathF.Ceiling(end * 1.15f * 2) / 2; // a little room past the last cue, on a half second
        float X(float t) => left + (right - left) * (t / end);
        // Second labels sit under the axis, inside the strip.
        float axisY = origin.Y + h - Fonts.SmallSize - Theme.Px(3);

        dl.AddRectFilled(origin, origin + new Vector2(w, h), ImGui.ColorConvertFloat4ToU32(Theme.Track), Theme.Px(3));
        uint tick = ImGui.ColorConvertFloat4ToU32(Theme.TextDisabled);
        float step = end <= 4 ? 0.5f : end <= 10 ? 1 : end <= 30 ? 5 : 10;
        for (float t = 0; t <= end + 1e-3f; t += step)
        {
            bool whole = MathF.Abs(t - MathF.Round(t)) < 1e-3f;
            dl.AddLine(new Vector2(X(t), axisY - Theme.Px(whole ? 4 : 2)), new Vector2(X(t), axisY), tick, 1);
            if (!whole) continue;
            string label = $"{t:0}s";
            float labelW = ImGui.CalcTextSize(label).X * (Fonts.SmallSize / Fonts.BodySize);
            // Labels sit right of their tick, except where that would run out of the strip.
            float lx = X(t) + Theme.Px(2) + labelW > origin.X + w ? X(t) - Theme.Px(2) - labelW : X(t) + Theme.Px(2);
            dl.AddText(Fonts.Small, Fonts.SmallSize, new Vector2(lx, axisY + Theme.Px(1)), tick, label);
        }
        dl.AddLine(new Vector2(left, axisY), new Vector2(right, axisY), tick, 1);

        var mouse = ImGui.GetMousePos();
        int? hover = null;
        foreach (int i in indices)
        {
            var c = e.Outputs[i];
            float t = _dragging == (e.Id, i) ? _dragDelay : c.Delay;
            var p = new Vector2(X(t), axisY - Theme.Px(12));
            bool near = ImGui.IsItemHovered() && Vector2.Distance(mouse, p) < Theme.Px(7);
            if (near) hover = i;
            uint col = near || _dragging == (e.Id, i) ? Theme.HoverU32 : i == selected ? Theme.AccentU32 : LinkDraw.OutColor;
            dl.AddLine(new Vector2(p.X, p.Y), new Vector2(p.X, axisY), col, Theme.Px(1));
            dl.AddCircleFilled(p, Theme.Px(4.5f), col);
        }

        if (hover is { } hi && _dragging == null)
        {
            var c = e.Outputs[hi];
            ImGui.SetTooltip($"{c.Delay:0.00} s  {TargetText(ctx.Doc, e, c).Text} → {EntityClass.Humanize(c.Do).ToLowerInvariant()}\nDrag to move it in time");
            if (c.Target.Kind == TargetKind.Entity) LinkDraw.Highlight = c.Target.Entity;
            if (ImGui.IsItemActivated())
            {
                _dragging = (e.Id, hi);
                _dragDelay = c.Delay;
                clicked = hi;
            }
        }
        if (_dragging is { } d && d.Entity == e.Id && indices.Contains(d.Connection))
        {
            if (ImGui.IsItemActive())
            {
                float t = Math.Clamp((mouse.X - left) / (right - left) * end, 0, 3600);
                _dragDelay = ImGui.GetIO().KeyShift ? MathF.Round(t, 3) : MathF.Round(t * 20) / 20;
                ImGui.SetTooltip($"{_dragDelay:0.00} s");
            }
            else
            {
                int at = d.Connection;
                float delay = _dragDelay;
                _dragging = null;
                if (delay != e.Outputs[at].Delay) Change(ctx, e, at, "Move Cue", x => x.Outputs[at].Delay = delay);
            }
        }
        return clicked;
    }

    // ---- Shared ----

    /// <summary>One undo step on an entity's connections; abandoned when the connection is gone (another edit removed it).</summary>
    public static void Change(EditorContext ctx, Entity e, int index, string label, Action<Entity> edit) =>
        EntitySection.Edit(ctx, e.Id, label, x =>
        {
            if (index >= x.Outputs.Count) return "That connection no longer exists";
            edit(x);
            return null;
        });
}
