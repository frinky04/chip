using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Chip.Core.Scene;
using Chip.Editor.Features.Scene;
using Chip.Editor.Framework;
using Chip.Editor.UI;
using ImGuiNET;
using static Chip.Editor.UI.Widgets;

namespace Chip.Editor.Features.Logic;

/// <summary>
/// The selected entity's connections, under each output of its class: what each one does, to whom, after how long, and
/// when. Every field is a pick from what the target can take (its inputs, their typed arguments, the project's flags), so
/// a connection can't name an input that doesn't exist. A timeline output (a sequence's cues) also draws its connections
/// on a time axis, where dragging a cue changes its delay. Every committed change is one undo step.
/// </summary>
public sealed class OutputsSection : IInspectorSection
{
    readonly ValueFields _fields = new();
    string _filter = "";
    // A cue being dragged on a timeline: its connection and the delay it shows until the mouse lets go.
    (int Entity, int Connection)? _dragging;
    float _dragDelay;

    public string Id => "outputs";
    public int Order => 111;

    public bool Visible(EditorContext ctx) => ctx.Sel.Entities(ctx.Doc) is [var e]
        && (EntityClasses.Get(e.ClassName).Outputs.Count > 0 || e.Outputs.Count > 0);

    public void Draw(EditorContext ctx)
    {
        var e = ctx.Sel.Entities(ctx.Doc)[0];
        var cls = EntityClasses.Get(e.ClassName);
        if (!Heading("Outputs", e.Outputs.Count == 0 ? null : $"{e.Outputs.Count}")) return;

        foreach (var output in cls.Outputs) Output(ctx, e, output.Name, output);
        // Connections on names the class no longer has (renamed without an alias, or a level from elsewhere).
        foreach (var name in e.Outputs.Select(c => c.On).Where(n => cls.Output(n) == null).Distinct().ToList()) Output(ctx, e, name, null);

        var problems = LevelProblems.For(ctx, e.Id);
        if (problems.Count > 0)
        {
            ImGui.Dummy(Theme.Px(0, 2));
            // Messages lead with the entity's name for the command line; here it's the selection, so drop it.
            string lead = (e.Name.Length > 0 ? e.Name : $"{e.ClassName} #{e.Id}") + ": ";
            foreach (var p in problems)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, p.Severity == Severity.Error ? Theme.Error : Theme.Hover);
                ImGui.TextWrapped(p.Message.StartsWith(lead, StringComparison.Ordinal) ? p.Message[lead.Length..] : p.Message);
                ImGui.PopStyleColor();
            }
        }
    }

    // ---- One output and its connections ----

    void Output(EditorContext ctx, Entity e, string name, OutputSpec? output)
    {
        ImGui.PushID($"out:{name}");
        var indices = Enumerable.Range(0, e.Outputs.Count).Where(i => e.Outputs[i].On == name).ToList();
        float button = ImGui.GetFrameHeight();
        ImGui.Dummy(Theme.Px(0, 2));
        ImGui.AlignTextToFramePadding();
        string label = output?.Label ?? EntityClass.Humanize(name);
        if (output == null) ImGui.TextColored(Theme.Error, label);
        else if (indices.Count > 0) { ImGui.PushFont(Fonts.Strong); ImGui.Text(label); ImGui.PopFont(); }
        else ImGui.TextColored(Theme.TextDim, label);
        Tip(OutputTip(e, name, output));
        if (output?.Payload is { } payload)
        {
            ImGui.SameLine();
            float room = ImGui.GetContentRegionMax().X - button - ImGui.GetCursorPosX() - ImGui.GetStyle().ItemSpacing.X;
            ImGui.TextColored(Theme.TextDisabled, Ellipsize($"({EntityValues.TypeName(payload)})", room));
            Tip($"It passes a {EntityValues.TypeName(payload)} along, which an argument can take");
        }
        if (output != null)
        {
            ImGui.SameLine(ImGui.GetContentRegionMax().X - button);
            if (IconButton("add", Icon.Plus, $"Connect {label.ToLowerInvariant()} to something", button)) { _filter = ""; ImGui.OpenPopup("##addtarget"); }
            if (TargetMenuPopup(ctx, e, "##addtarget", null) is { } target) Add(ctx, e, name, target);
        }
        else Dim($"{EntityClasses.Get(e.ClassName).Label} has no output called {name}, so these never fire. Remove them, or add the output in {EntityClasses.Engine}");

        if (output is { Timeline: true } && indices.Count > 0) Timeline(ctx, e, indices);
        foreach (int i in output is { Timeline: true } ? indices.OrderBy(i => e.Outputs[i].Delay).ToList() : indices)
        {
            ImGui.PushID(i);
            Connection(ctx, e, output, i);
            ImGui.PopID();
        }
        ImGui.PopID();
    }

    static string OutputTip(Entity e, string name, OutputSpec? output)
    {
        if (output == null) return $"{name}: not an output of {EntityClasses.Get(e.ClassName).Label}";
        var lines = new List<string>();
        if (output.Doc.Length > 0) lines.Add(output.Doc);
        lines.Add(output.Timeline ? $"{name}: each connection fires at its own delay after the start" : $"{name}: fires its connections in order");
        if (output.Required) lines.Add("The game expects this to be connected");
        return string.Join("\n", lines);
    }

    void Add(EditorContext ctx, Entity e, string on, ConnectionTarget target)
    {
        var inputs = Inputs(ctx, e, target);
        var c = new Connection { On = on, Target = target, Do = inputs.FirstOrDefault()?.Name ?? "" };
        // A timeline's next cue starts a beat after its last, so new cues don't stack on zero.
        if (EntityClasses.Get(e.ClassName).Output(on) is { Timeline: true } && e.Outputs.Where(x => x.On == on).Select(x => x.Delay).DefaultIfEmpty(-1).Max() is var last and >= 0)
            c.Delay = MathF.Round(last + 0.5f, 2);
        EntitySection.Edit(ctx, e.Id, "Add Connection", x => { x.Outputs.Add(c); return null; });
    }

    // ---- One connection ----

    void Connection(EditorContext ctx, Entity e, OutputSpec? output, int index)
    {
        var c = e.Outputs[index];
        float button = ImGui.GetFrameHeight();
        float gap = ImGui.GetStyle().ItemSpacing.X;
        float indent = Theme.Px(10);
        var dl = ImGui.GetWindowDrawList();
        var top = ImGui.GetCursorScreenPos();
        dl.ChannelsSplit(2);
        dl.ChannelsSetCurrent(1);
        ImGui.Indent(indent);
        float w = ImGui.GetContentRegionAvail().X;

        // Target, eyedropper, remove.
        ImGui.SetNextItemWidth(w - (button + gap) * 2);
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
        ImGui.SameLine(0, gap);
        if (IconButton("remove", Icon.Trash, "Remove this connection", button))
            Change(ctx, e, index, "Remove Connection", x => x.Outputs.RemoveAt(index));

        // Input, delay, once.
        var inputs = Inputs(ctx, e, c.Target);
        var input = inputs.FirstOrDefault(i => i.Name == c.Do);
        float delayW = Theme.Px(64);
        float onceW = ImGui.GetFrameHeight() + ImGui.CalcTextSize("Once").X + ImGui.GetStyle().ItemInnerSpacing.X;
        ImGui.SetNextItemWidth(w - delayW - onceW - gap * 2);
        InputCombo(ctx, e, index, c, inputs, input);
        ImGui.SameLine(0, gap);
        ImGui.SetNextItemWidth(delayW);
        float delay = _dragging == (e.Id, index) ? _dragDelay : c.Delay;
        if (ImGui.DragFloat("##delay", ref delay, 0.02f, 0, 3600, "%.2f s", ImGuiSliderFlags.AlwaysClamp)) { _dragging = (e.Id, index); _dragDelay = delay; }
        if (ImGui.IsItemDeactivatedAfterEdit() && _dragging == (e.Id, index))
        {
            _dragging = null;
            float d = MathF.Round(_dragDelay, 3);
            Change(ctx, e, index, "Set Delay", x => x.Outputs[index].Delay = d);
        }
        Tip("Seconds after the output fires. 0: straight away, before anything else happens");
        ImGui.SameLine(0, gap);
        bool once = c.Once;
        if (ImGui.Checkbox("Once", ref once)) Change(ctx, e, index, once ? "Fire Once" : "Fire Every Time", x => x.Outputs[index].Once = once);
        Tip("Fires the first time only; after that this connection does nothing");

        if (input != null)
            foreach (var p in input.Params) Argument(ctx, e, index, c, output, p);
        Condition(ctx, e, index, c);
        ImGui.Unindent(indent);

        // A faint card behind the rows, with a rail in the link colour, drawn under them.
        var bottom = ImGui.GetCursorScreenPos();
        dl.ChannelsSetCurrent(0);
        var min = top + new Vector2(Theme.Px(2), -Theme.Px(2));
        var max = new Vector2(top.X + ImGui.GetContentRegionAvail().X, bottom.Y);
        dl.AddRectFilled(min, max, ImGui.ColorConvertFloat4ToU32(Theme.Frame with { W = 0.35f }), Theme.Px(3));
        dl.AddRectFilled(min, new Vector2(min.X + Theme.Px(2), max.Y), LinkDraw.OutColor, Theme.Px(1));
        dl.ChannelsMerge();
        ImGui.Dummy(Theme.Px(0, 3));
    }

    // ---- Target ----

    void TargetCombo(EditorContext ctx, Entity e, int index, Connection c)
    {
        var (text, ok) = TargetText(ctx.Doc, e, c);
        if (LogicFeature.Picker.Armed(ctx, $"io:{e.Id}/{index}")) (text, ok) = ("Click one in the viewport…", true);
        if (!ok) ImGui.PushStyleColor(ImGuiCol.Text, Theme.Error);
        bool open = ImGui.BeginCombo("##target", "→ " + text, ImGuiComboFlags.HeightLarge);
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

    ConnectionTarget? TargetMenuPopup(EditorContext ctx, Entity e, string id, ConnectionTarget? current)
    {
        if (!ImGui.BeginPopup(id)) return null;
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
        foreach (var (o, label) in entities) Item($"{label}##{o.Id}", new(TargetKind.Entity, o.Id), $"{SceneOps.DisplayName(o)}: what its {EntityClasses.Get(o.ClassName).Label.ToLowerInvariant()} can do", o.Id);

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

    static (string Text, bool Ok) TargetText(Document doc, Entity e, Connection c)
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
    void Retarget(EditorContext ctx, Entity e, int index, ConnectionTarget target)
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
    static IReadOnlyList<InputSpec> Inputs(EditorContext ctx, Entity e, ConnectionTarget target) =>
        target is { Kind: TargetKind.Special, Name: "activator" or "caller" }
            ? EntityClasses.Current.Classes.SelectMany(k => k.Inputs).DistinctBy(i => i.Name).OrderBy(i => i.Name, StringComparer.Ordinal).ToList()
            : LogicOps.InputsFor(ctx.Doc, EntityClasses.Current, EntityClasses.Project, e, target);

    void InputCombo(EditorContext ctx, Entity e, int index, Connection c, IReadOnlyList<InputSpec> inputs, InputSpec? input)
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
        float labelW = PropertyColumn;
        bool set = c.Args.TryGetValue(p.Name, out var own);
        bool bound = set && IsPayload(own);
        bool canBind = output?.Payload is { } payload && payload.Type == p.Type && payload.ItemType == p.ItemType;

        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(set ? Theme.Accent : Theme.TextDim, Ellipsize(p.Label, labelW - ImGui.GetCursorPosX() - Theme.Px(4)));
        Tip((p.Doc.Length > 0 ? p.Doc + "\n" : "") + $"{p.Name}: {EntityValues.TypeName(p)}" + (set ? "" : $". Default: {EntityValues.Text(p.Default)}"));
        ImGui.SameLine(labelW);
        float fieldW = ImGui.GetContentRegionAvail().X - (canBind || bound ? button + gap : 0);
        ImGui.SetNextItemWidth(fieldW);
        if (bound)
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(ImGui.ColorConvertU32ToFloat4(LinkDraw.OutColor), $"← {(output != null ? output.Label.ToLowerInvariant() : "the output")}'s {(output?.Payload is { } pp ? EntityValues.TypeName(pp) : "value")}");
            Tip(canBind ? "Takes the value the output passes along" : "The output passes no value of this type any more. Set one instead");
        }
        else
        {
            var value = set ? own : p.Default;
            _fields.Draw(ctx, e, p, value, $"io{index}/{p.Name}", fieldW, v =>
                Change(ctx, e, index, $"Set {p.Label}", x =>
                {
                    if (EntityValues.Coerce(p, v, out _) is not { } typed) return;
                    if (EntityValues.Same(typed, p.Default)) x.Outputs[index].Args.Remove(p.Name);
                    else x.Outputs[index].Args[p.Name] = typed;
                }));
        }
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
        ImGui.PopID();
    }

    static readonly JsonElement Payload = JsonSerializer.SerializeToElement(new Dictionary<string, bool> { ["payload"] = true });

    static bool IsPayload(JsonElement v) =>
        v.ValueKind == JsonValueKind.Object && v.TryGetProperty("payload", out var b) && b.ValueKind == JsonValueKind.True;

    // ---- Condition ----

    /// <summary>"Only when" a flag holds a value (or, for a number, reaches one). Checked when the output fires.</summary>
    void Condition(EditorContext ctx, Entity e, int index, Connection c)
    {
        var flags = EntityClasses.Project?.Flags;
        if (c.When is not { } when)
        {
            if (flags is not { Count: > 0 }) return;
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
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.TextDim, "Only when");
        ImGui.SameLine(PropertyColumn);
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
    /// A timeline output's connections as cues on a time axis, labelled with what they do. Drag a cue to move it in time
    /// (snapped to a twentieth of a second, Shift for free); the list below follows in time order.
    /// </summary>
    void Timeline(EditorContext ctx, Entity e, List<int> indices)
    {
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
            uint col = near || _dragging == (e.Id, i) ? Theme.HoverU32 : LinkDraw.OutColor;
            dl.AddLine(new Vector2(p.X, p.Y), new Vector2(p.X, axisY), col, Theme.Px(1));
            dl.AddCircleFilled(p, Theme.Px(4.5f), col);
        }

        if (hover is { } hi && _dragging == null)
        {
            var c = e.Outputs[hi];
            ImGui.SetTooltip($"{c.Delay:0.00} s  {TargetText(ctx.Doc, e, c).Text} → {EntityClass.Humanize(c.Do).ToLowerInvariant()}\nDrag to move it in time");
            if (c.Target.Kind == TargetKind.Entity) LinkDraw.Highlight = c.Target.Entity;
            if (ImGui.IsItemActivated()) { _dragging = (e.Id, hi); _dragDelay = c.Delay; }
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
        ImGui.Dummy(Theme.Px(0, 2));
    }

    // ---- Shared ----

    /// <summary>One undo step on the entity's connections; abandoned when the connection is gone (another edit removed it).</summary>
    static void Change(EditorContext ctx, Entity e, int index, string label, Action<Entity> edit) =>
        EntitySection.Edit(ctx, e.Id, label, x =>
        {
            if (index >= x.Outputs.Count) return "That connection no longer exists";
            edit(x);
            return null;
        });

    static void Dim(string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextDim);
        ImGui.TextWrapped(text);
        ImGui.PopStyleColor();
    }
}

/// <summary>The level's logic problems (<see cref="LogicOps.Check"/>), worked out once per edit and listed per entity.</summary>
public static class LevelProblems
{
    static readonly ConditionalWeakTable<EditorContext, Rendering.FrameCache<(EntityCatalog Catalog, ILookup<int?, LevelProblem> Problems)>> Cache = new();

    public static IReadOnlyList<LevelProblem> For(EditorContext ctx, int entity)
    {
        var cache = Cache.GetValue(ctx, _ => new Rendering.FrameCache<(EntityCatalog, ILookup<int?, LevelProblem>)>(selection: false));
        // The catalog reloads when the game's manifest changes; problems follow it as well as the level's edits.
        if (!cache.IsCurrent(ctx.Session) || cache.Value.Catalog != EntityClasses.Current)
            cache.Store(ctx.Session, (EntityClasses.Current, LogicOps.Check(ctx.Doc, EntityClasses.Current, EntityClasses.Project).ToLookup(p => p.Entity)));
        return cache.Value.Problems[entity].ToList();
    }
}
