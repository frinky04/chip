using System.Numerics;
using Chip.Core.Scene;
using Chip.Editor.Features.Scene;
using Chip.Editor.Framework;
using Chip.Editor.UI;
using ImGuiNET;
using static Chip.Editor.UI.Widgets;

namespace Chip.Editor.Features.Logic;

/// <summary>
/// The selected entity's wiring as two tables: Outgoing (its own connections: output, target, input, value, delay) and
/// Incoming (every connection that reaches it, from anywhere). Selecting a row edits that connection underneath, wherever
/// it lives; double-clicking jumps to the entity at the other end. A timeline output also shows its cues on a time axis.
/// </summary>
public sealed class EntityIoSection : IInspectorSection
{
    readonly ConnectionEditor _editor = new();
    int _tab;
    int _forEntity = -1;
    (int Entity, int Index)? _out, _in;
    string? _addOn; // the output picked in the Add menu, waiting for its target

    public string Id => "io";
    public int Order => 111;

    public bool Visible(EditorContext ctx) => ctx.Sel.Entities(ctx.Doc) is [var e]
        && (EntityClasses.Get(e.ClassName).Outputs.Count > 0 || e.Outputs.Count > 0 || IoRows.Incoming(ctx, e.Id).Count > 0);

    public void Draw(EditorContext ctx)
    {
        var e = ctx.Sel.Entities(ctx.Doc)[0];
        if (e.Id != _forEntity)
        {
            // A new entity opens on the side that has something: a light with no outputs shows what drives it.
            _forEntity = e.Id;
            _out = _in = null;
            _tab = EntityClasses.Get(e.ClassName).Outputs.Count == 0 && e.Outputs.Count == 0 ? 1 : 0;
        }
        if (IoFocus.Take(e.Id) is { } focus) { _tab = 0; _out = (e.Id, focus); }
        var outgoing = IoRows.Outgoing(ctx, e.Id);
        var incoming = IoRows.Incoming(ctx, e.Id);
        if (!Heading("Entity IO", $"{outgoing.Count} out · {incoming.Count} in")) return;

        float button = ImGui.GetFrameHeight();
        if (TextSegments("##iotab", [
            ($"Outgoing  {outgoing.Count}", "Its connections: what it makes happen when its outputs fire"),
            ($"Incoming  {incoming.Count}", "Connections from anywhere that act on it"),
        ], _tab) is >= 0 and var tab) _tab = tab;
        ImGui.SameLine(ImGui.GetContentRegionMax().X - button);
        if (IconButton("wiring", Icon.ListTree, "Every connection in the level  (Ctrl+Shift+I)", button)) ctx.Commands.Execute("logic.wiring");

        if (_tab == 0) Outgoing(ctx, e, outgoing);
        else Incoming(ctx, e, incoming);
    }

    void Outgoing(EditorContext ctx, Entity e, IReadOnlyList<IoRow> rows)
    {
        var cls = EntityClasses.Get(e.ClassName);
        if (_out is { } s && (s.Entity != e.Id || s.Index >= e.Outputs.Count)) _out = null;

        foreach (var output in cls.Outputs.Where(o => o.Timeline))
        {
            var cues = Enumerable.Range(0, e.Outputs.Count).Where(i => e.Outputs[i].On == output.Name).ToList();
            if (cues.Count == 0) continue;
            ImGui.PushID($"timeline:{output.Name}");
            ImGui.TextColored(Theme.TextDim, output.Label);
            if (_editor.Timeline(ctx, e, cues, _out?.Index) is { } cue) _out = (e.Id, cue);
            ImGui.PopID();
        }

        var clicked = IoTable.Draw("##out", rows, [IoColumn.Output, IoColumn.Target, IoColumn.Input, IoColumn.Value, IoColumn.Delay],
            _out, r => r.Targets.FirstOrDefault()?.Id, out bool activated);
        if (clicked != null)
        {
            _out = (e.Id, clicked.Index);
            if (activated && clicked.Targets.FirstOrDefault(t => t.Id != e.Id) is { } target) EntityRefs.Select(ctx, target);
        }

        Toolbar(ctx, e, cls);
        foreach (var o in cls.Outputs.Where(o => o.Required && e.Outputs.All(c => c.On != o.Name)))
        {
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.Hover);
            ImGui.TextWrapped($"{o.Label} isn't connected; {EntityClasses.Engine} expects it to be.");
            ImGui.PopStyleColor();
        }

        if (_out is { } sel && sel.Entity == e.Id)
        {
            SectionDivider();
            _editor.Draw(ctx, e, sel.Index);
        }
    }

    /// <summary>Add (pick the output, then the target), Duplicate and Remove, for the selected row.</summary>
    void Toolbar(EditorContext ctx, Entity e, EntityClass cls)
    {
        float w = (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X * 2) / 3;
        ImGui.BeginDisabled(cls.Outputs.Count == 0);
        if (ImGui.Button("+ Add", new Vector2(w, 0)))
        {
            if (cls.Outputs.Count == 1) { _addOn = cls.Outputs[0].Name; ImGui.OpenPopup("##addtarget"); }
            else ImGui.OpenPopup("##addoutput");
        }
        ImGui.EndDisabled();
        Tip(cls.Outputs.Count == 0 ? $"{cls.Label} has no outputs to connect" : "Connect one of its outputs to something");

        if (ImGui.BeginPopup("##addoutput"))
        {
            ImGui.TextColored(Theme.TextDisabled, "When it…");
            foreach (var o in cls.Outputs)
            {
                if (ImGui.Selectable(o.Label)) { _addOn = o.Name; ImGui.CloseCurrentPopup(); }
                Tip(ConnectionEditor.OutputTip(e, o.Name, o));
            }
            ImGui.EndPopup();
            if (_addOn != null) ImGui.OpenPopup("##addtarget");
        }
        // Capture the output before the popup closes and clears the pending menu state.
        var addOn = _addOn;
        if (addOn != null && _editor.TargetMenuPopup(ctx, e, "##addtarget", null, () =>
            LogicFeature.Picker.Start(ctx, $"io:{e.Id}/add", "an entity with inputs",
                other => other.Id != e.Id && EntityClasses.Get(other.ClassName).Inputs.Count > 0,
                other =>
                {
                    if (ConnectionEditor.Add(ctx, e, addOn, new ConnectionTarget(TargetKind.Entity, other.Id)) is { } added)
                        _out = (e.Id, added);
                })) is { } target)
        {
            if (ConnectionEditor.Add(ctx, e, addOn, target) is { } added) _out = (e.Id, added);
            _addOn = null;
        }
        else if (_addOn != null && !ImGui.IsPopupOpen("##addtarget")) _addOn = null;

        ImGui.SameLine();
        ImGui.BeginDisabled(_out == null);
        if (ImGui.Button("Duplicate", new Vector2(w, 0)) && _out is { } dup)
        {
            int at = dup.Index;
            if (EntitySection.Edit(ctx, e.Id, "Duplicate Connection", x => { x.Outputs.Insert(at + 1, x.Outputs[at].Clone()); return null; }))
                _out = (e.Id, at + 1);
        }
        Tip("Add a copy of the selected connection below it");
        ImGui.SameLine();
        if (ImGui.Button("Remove", new Vector2(w, 0)) && _out is { } rem)
        {
            int at = rem.Index;
            ConnectionEditor.Change(ctx, e, at, "Remove Connection", x => x.Outputs.RemoveAt(at));
            _out = e.Outputs.Count == 0 ? null : (e.Id, Math.Min(at, e.Outputs.Count - 1));
        }
        Tip("Remove the selected connection");
        ImGui.EndDisabled();
    }

    void Incoming(EditorContext ctx, Entity e, IReadOnlyList<IoRow> rows)
    {
        if (_in is { } s && !rows.Any(r => (r.Source.Id, r.Index) == s)) _in = null;
        var clicked = IoTable.Draw("##in", rows, [IoColumn.Source, IoColumn.Output, IoColumn.Input, IoColumn.Value, IoColumn.Delay],
            _in, r => r.Source.Id, out bool activated);
        if (clicked != null)
        {
            _in = (clicked.Source.Id, clicked.Index);
            if (activated) IoFocus.Show(ctx, clicked.Source, clicked.Index);
        }
        if (rows.Count == 0) Dim(EntityClasses.Get(e.ClassName).Inputs.Count == 0
            ? $"{EntityClasses.Get(e.ClassName).Label} takes no inputs, so nothing can connect to it"
            : "Nothing is connected to it yet. Connect to it from another entity's Outgoing tab");

        if (_in is { } sel && ctx.Doc.FindEntity(sel.Entity) is { } source)
        {
            SectionDivider();
            // The connection lives on its source; say so, with a way there, then edit it in place.
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.TextDim, $"On {SceneOps.DisplayName(source)}");
            float button = ImGui.GetFrameHeight();
            ImGui.SameLine(ImGui.GetContentRegionMax().X - button);
            if (IconButton("goto", Icon.ChevronRight, $"Select {SceneOps.DisplayName(source)} and its connection", button)) IoFocus.Show(ctx, source, sel.Index);
            _editor.Draw(ctx, source, sel.Index);
        }
    }

    static void Dim(string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextDim);
        ImGui.TextWrapped(text);
        ImGui.PopStyleColor();
    }
}

/// <summary>Jumps to a connection: selects its source entity and has the Entity IO section open the connection there.</summary>
public static class IoFocus
{
    static (int Entity, int Index)? _pending;

    public static void Show(EditorContext ctx, Entity source, int index)
    {
        EntityRefs.Select(ctx, source);
        _pending = (source.Id, index);
    }

    /// <summary>The connection to open on <paramref name="entity"/>, once; null when none is waiting for it.</summary>
    public static int? Take(int entity)
    {
        if (_pending is not { } p || p.Entity != entity) return null;
        _pending = null;
        return p.Index;
    }
}
