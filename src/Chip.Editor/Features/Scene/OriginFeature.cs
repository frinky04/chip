using System.Numerics;
using Chip.Core.Geometry;
using Chip.Core.Scene;
using Chip.Editor.Framework;
using Chip.Editor.UI;
using static Chip.Editor.UI.Widgets;

namespace Chip.Editor.Features.Scene;

/// <summary>
/// Object origins (<see cref="ObjectOrigin"/>): commands that place or clear the origin of the selected meshes and
/// groups, and the inspector's Origin section. An origin is the object's pivot and gizmo axes, and where it exports.
/// </summary>
public sealed class OriginFeature : IEditorFeature
{
    public void Register(EditorContext ctx)
    {
        bool HasTargets() => OriginOps.Targets(ctx.Doc, ctx.Sel).Count > 0;
        var c = ctx.Commands;
        c.Add(new Command
        {
            Id = "origin.bottomCentre", Label = "Origin to bottom centre", Execute = () => Place(ctx, OriginPlacement.BottomCentre),
            Menu = "Mesh", Group = "7-origin", Order = 0, CanExecute = HasTargets,
            Description = "Puts the origin of each selected mesh or group at the middle of its base, where a prop stands",
        });
        c.Add(new Command
        {
            Id = "origin.centre", Label = "Origin to centre", Execute = () => Place(ctx, OriginPlacement.Centre),
            Menu = "Mesh", Group = "7-origin", Order = 1, CanExecute = HasTargets,
            Description = "Puts the origin of each selected mesh or group at the centre of its bounds",
        });
        c.Add(new Command
        {
            Id = "origin.toPivot", Label = "Origin to pivot", Execute = () => ToPivot(ctx),
            Menu = "Mesh", Group = "7-origin", Order = 2, CanExecute = HasTargets,
            Description = "Puts the origin where the gizmo is: the selected vertices, edges or faces, or the temporary pivot (middle-drag the gizmo)",
        });
        c.Add(new Command
        {
            Id = "origin.clear", Label = "Clear origin", Execute = () => Clear(ctx),
            Menu = "Mesh", Group = "7-origin", Order = 3,
            CanExecute = () => OriginOps.Targets(ctx.Doc, ctx.Sel).Any(t => t.Origin != null),
            Description = "Removes the origin: the object pivots on its bounds centre and exports at the world origin again",
        });
        ctx.AddSection(new OriginSection());
    }

    /// <summary>Runs one undo step over the selection's origin targets.</summary>
    internal static void Edit(EditorContext ctx, string label, Func<List<OriginTarget>, string> edit)
    {
        var targets = OriginOps.Targets(ctx.Doc, ctx.Sel);
        if (targets.Count == 0) { ctx.Status("Select a mesh or group to set its origin", error: true); return; }
        string status = "";
        var meshIds = targets.SelectMany(t => t.Meshes(ctx.Doc)).Select(m => m.Id).ToList();
        ctx.Session.Do(label, meshIds, () => status = edit(targets));
        if (status.Length > 0) ctx.Status(status);
    }

    static string Describe(List<OriginTarget> targets) => targets.Count == 1 ? targets[0].Name : $"{targets.Count} objects";

    static void Place(EditorContext ctx, OriginPlacement placement) => Edit(ctx, "Set Origin", targets =>
    {
        OriginOps.Place(ctx.Doc, targets, placement);
        return $"Origin of {Describe(targets)} set to its {(placement == OriginPlacement.BottomCentre ? "bottom centre" : "centre")}";
    });

    static void ToPivot(EditorContext ctx)
    {
        if (ctx.SelectionPivot() is not { } pivot) return;
        Edit(ctx, "Set Origin", targets =>
        {
            OriginOps.MoveTo(targets, pivot);
            return $"Origin of {Describe(targets)} moved to the pivot";
        });
        ctx.PivotOverride = null; // the origin is the pivot now
    }

    static void Clear(EditorContext ctx) => Edit(ctx, "Clear Origin", targets =>
    {
        int n = OriginOps.Clear(targets);
        return n == 1 ? "Origin cleared" : $"Cleared {n} origins";
    });
}

/// <summary>
/// The selected object's origin (object mode): position and rotation, typed in to move the origin without moving the
/// geometry, and the origin commands as buttons.
/// </summary>
public sealed class OriginSection : IInspectorSection
{
    public string Id => "origin";
    public int Order => 102; // with the selection's bounds, before the actions
    public bool Visible(EditorContext ctx) => ctx.Sel.Mode == SelectMode.Object && OriginOps.Targets(ctx.Doc, ctx.Sel).Count > 0;

    public void Draw(EditorContext ctx)
    {
        var targets = OriginOps.Targets(ctx.Doc, ctx.Sel);
        if (targets.Count == 0 || !Heading("Origin")) return;
        if (targets.Count == 1 && targets[0].Origin is { } o)
        {
            var p = o.Position;
            RowLabel("Position", "Where the origin is, in metres. Typing moves the origin, not the geometry.");
            if (TaggedFields("##originPos", ["X", "Y", "Z"], Theme.AxisU32, [p.X, p.Y, p.Z], [false, false, false], "%.3f", out int pi, out float pv))
            {
                if (pi == 0) p.X = pv; else if (pi == 1) p.Y = pv; else p.Z = pv;
                Set(ctx, o with { Position = p });
            }
            var r = EntityOps.ToEuler(o.Rotation);
            RowLabel("Rotation", "The origin's axes in degrees: pitch (X), yaw (Y), roll (Z). The gizmo's local axes follow them. Typing turns the origin, not the geometry.");
            if (TaggedFields("##originRot", ["X", "Y", "Z"], Theme.AxisU32, [r.X, r.Y, r.Z], [false, false, false], "%.1f", out int ri, out float rv))
            {
                if (ri == 0) r.X = rv; else if (ri == 1) r.Y = rv; else r.Z = rv;
                Set(ctx, o with { Rotation = EntityOps.Orientation(r) });
            }
        }
        else if (targets.Count == 1)
            FieldNote("No origin yet: it pivots on its bounds centre and exports at the world origin.");
        else
        {
            int set = targets.Count(t => t.Origin != null);
            FieldNote($"{targets.Count} objects, {set} with an origin. The buttons set each one's own origin.");
        }
        RowLabel("Set to", "Places the origin of each selected mesh or group");
        CommandRow(ctx.Commands, "origin", ("origin.bottomCentre", "Bottom", null), ("origin.centre", "Centre", null));
        FieldStart();
        CommandRow(ctx.Commands, "origin2", ("origin.toPivot", "Pivot", null), ("origin.clear", "Clear", null));
    }

    static void Set(EditorContext ctx, ObjectOrigin origin) =>
        OriginFeature.Edit(ctx, "Set Origin", targets =>
        {
            foreach (var t in targets) t.Origin = origin;
            return "";
        });
}
