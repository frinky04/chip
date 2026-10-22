using System.Numerics;
using Chip.Core.Scene;
using Chip.Editor.Framework;
using Chip.Editor.Tools.Block;
using Chip.Editor.UI;
using ImGuiNET;
using static Chip.Editor.UI.Widgets;

namespace Chip.Editor.Features.Scene;

/// <summary>
/// Entity tool (Shift+E): click a surface or the grid to place a point entity of the class picked in the Tool section.
/// The point snaps to the grid on the surface's plane, and the entity's box is pushed out of the surface so a light on
/// the floor sits on it and a player start on a wall stands clear of it. The new entity becomes the selection; the tool
/// stays active for the next one.
/// </summary>
public sealed class EntityTool : IViewportTool
{
    readonly ConstructionPlane _plane = new();
    (Vector3 Position, float Yaw, string? Fit)? _cursor;
    string _custom = "";

    public string Id => "entity";
    public string Name => "Entity";
    public Icon Icon => Icon.MapPin;
    public string? Hotkey => "Shift+E";
    public int Order => 15;
    public bool Busy => false;
    public bool HasOptions => true;

    /// <summary>Class of the next entity (a built-in or any custom name).</summary>
    public string ClassName { get; set; } = "info_player_start";
    /// <summary>Yaw of the next entity in degrees.</summary>
    public float Yaw { get; set; }
    /// <summary>Model of the next reference model (a built-in name or a file path, see <see cref="ReferenceModels"/>).</summary>
    public string Model { get; set; } = ReferenceModels.DefaultModel;

    bool PlacingReference => ClassName.Trim() == ReferenceModels.ClassName;
    string Label => PlacingReference ? ReferenceModels.DisplayName(Model) : EntityClasses.Get(ClassName).Label;

    public (string Key, string Action)[] Hints => [("LMB", $"place {Label.ToLowerInvariant()}"), ("Esc", "exit tool")];

    public bool Cancel(EditorContext ctx)
    {
        _cursor = null;
        return false;
    }

    public void Update(EditorContext ctx)
    {
        ctx.Hover = null;
        _cursor = ctx.ViewportHovered ? Placement(ctx) : null;
        if (_cursor is { } c && ImGui.IsMouseClicked(ImGuiMouseButton.Left)) Place(ctx, c.Position, c.Yaw, c.Fit);
    }

    /// <summary>A new entity of the current class (and model) at the point.</summary>
    Entity Probe(Vector3 position, float yaw)
    {
        var e = EntityClasses.Create(ClassName.Trim(), position, yaw);
        if (PlacingReference) e.Properties[ReferenceModels.ModelKey] = Model;
        return e;
    }

    /// <summary>
    /// Where a click would put the entity: the snapped surface point, lifted so the box clears the surface. A reference
    /// model clicked onto a wall turns to face out of it and sits in it or against it (<see cref="ReferencePlacement"/>).
    /// </summary>
    (Vector3, float, string?)? Placement(EditorContext ctx)
    {
        if (PlacingReference && ReferenceModels.Get(Model).Model is { } model && ctx.Picks.RaycastFace(ctx.MouseRay(), ctx.Backfaces) is { } hit
            && ReferencePlacement.IsWall(hit.Normal))
        {
            var n = Vector3.Dot(hit.Normal, ctx.MouseRay().Dir) > 0 ? -hit.Normal : hit.Normal;
            var p = hit.Point;
            if (ctx.Snap)
            {
                p = new Vector3(Core.Geometry.MeshOps.Snap(p.X, ctx.Grid), p.Y, Core.Geometry.MeshOps.Snap(p.Z, ctx.Grid));
                p -= n * Vector3.Dot(p - hit.Point, n); // back onto the wall's plane
            }
            float bottom = hit.Mesh.Faces[hit.Face].Verts.Min(v => hit.Mesh.Positions[v].Y);
            var r = ReferencePlacement.OnWall(ctx.Doc, p, n, bottom, model);
            return (r.Position, r.Yaw, FitText(model, r));
        }
        if (_plane.Pick(ctx, false) is not { } point) return null;
        var normal = _plane.Normal;
        if (Vector3.Dot(normal, ctx.MouseRay().Dir) > 0) normal = -normal; // a back face, or the grid seen from below: stay on the camera's side
        float below = EntityOps.BoxCorners(Probe(Vector3.Zero, Yaw)).Min(c => Vector3.Dot(c, normal));
        return (below < 0 ? point - normal * below : point, Yaw, null);
    }

    /// <summary>How the model sits in the wall, for the status bar: "12.5 cm deep in a 20 cm wall".</summary>
    static string FitText(ReferenceModel model, ReferencePlacement.Result r)
    {
        string depth = Length(model.Size.Z);
        if (r.Thickness is not { } t) return $"{depth} deep, against a solid surface";
        if (r.Inset)
        {
            float gap = t - model.Size.Z;
            return MathF.Abs(gap) < 0.0005f ? $"{depth} deep, flush in a {Length(t)} wall"
                : gap > 0 ? $"{depth} deep in a {Length(t)} wall, {Length(gap / 2)} short of each face"
                : $"{depth} deep in a {Length(t)} wall, standing {Length(-gap / 2)} proud of each face";
        }
        return $"{depth} deep, too deep for the {Length(t)} wall, so it stands against it";
    }

    static string Length(float m) => MathF.Abs(m) >= 1 ? $"{m:0.###} m" : $"{m * 100:0.##} cm";

    public void Place(EditorContext ctx, Vector3 position, float? yaw = null, string? fit = null)
    {
        var className = ClassName.Trim();
        if (className.Length == 0) { ctx.Status("Type a class name for the entity first", error: true); return; }
        Entity? placed = null;
        var probe = Probe(position, yaw ?? Yaw);
        ctx.Session.Do($"Place {Label}", () =>
        {
            placed = ctx.Doc.Add(probe);
            ctx.Sel.ConvertTo(SelectMode.Object, ctx.Doc);
            ctx.Sel.Clear();
            ctx.Sel.Add(ElementRef.Object(placed.Id));
        });
        ctx.Status(fit != null ? $"Placed {Label}: {fit}" : $"Placed {(PlacingReference ? Label : className)} at {position.X:0.##}, {position.Y:0.##}, {position.Z:0.##}");
    }

    public void Draw(EditorContext ctx, ImDrawListPtr dl, Vector2 origin)
    {
        if (_cursor is not { } c) return;
        EntityDraw.Draw(dl, origin, ctx.View, Probe(c.Position, c.Yaw), EntityLook.Ghost);
    }

    public void DrawOptions(EditorContext ctx)
    {
        foreach (var c in EntityClasses.BuiltIn)
        {
            var (icon, col) = EntityDraw.Style(c.Name);
            bool active = ClassName == c.Name;
            var p = ImGui.GetCursorScreenPos();
            if (ImGui.Selectable($"##{c.Name}", active, ImGuiSelectableFlags.None, new Vector2(0, ImGui.GetFrameHeight())))
                ClassName = c.Name;
            Tip(c.Name);
            var dl = ImGui.GetWindowDrawList();
            float h = ImGui.GetFrameHeight();
            Icons.Draw(dl, icon, p + new Vector2(Theme.Px(4) + Fonts.IconSize * 0.5f, h * 0.5f), col);
            float textY = MathF.Round(p.Y + (h - ImGui.GetTextLineHeight()) * 0.5f);
            dl.AddText(new Vector2(p.X + Theme.Px(10) + Fonts.IconSize, textY), ImGui.ColorConvertFloat4ToU32(Theme.Text), c.Label);
        }

        ImGui.Dummy(Theme.Px(0, 2));
        if (PlacingReference)
        {
            RowLabel("Model", "The model to place. Click a wall to fit it into or against the wall");
            if (References.ReferencePicker.Draw("##toolModel", Model, out var model)) Model = model;
            References.ReferencePicker.Summary(Model);
        }
        RowLabel("Class", "Any class name your engine understands; a name that isn't listed above is a generic point entity");
        if (!EntityClasses.IsBuiltIn(ClassName) && _custom.Length == 0) _custom = ClassName;
        if (ImGui.InputTextWithHint("##customClass", "custom_class", ref _custom, 64) && _custom.Trim().Length > 0)
            ClassName = _custom.Trim();
        if (ImGui.IsItemActivated() && _custom.Trim().Length > 0) ClassName = _custom.Trim();

        RowLabel("Yaw", "Facing of new entities in degrees (0 faces -Z)");
        float yaw = Yaw;
        if (ImGui.DragFloat("##yaw", ref yaw, 1, -180, 180, "%.0f°")) Yaw = yaw;
    }
}
