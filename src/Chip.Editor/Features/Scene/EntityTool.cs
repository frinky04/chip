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
    Vector3? _cursor;
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

    string Label => EntityClasses.Get(ClassName).Label;

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
        if (_cursor is { } p && ImGui.IsMouseClicked(ImGuiMouseButton.Left)) Place(ctx, p);
    }

    /// <summary>Where a click would put the entity: the snapped surface point, lifted so the class box clears the surface.</summary>
    Vector3? Placement(EditorContext ctx)
    {
        if (_plane.Pick(ctx, false) is not { } point) return null;
        var n = _plane.Normal;
        if (Vector3.Dot(n, ctx.MouseRay().Dir) > 0) n = -n; // a back face, or the grid seen from below: stay on the camera's side
        var probe = EntityClasses.Create(ClassName, Vector3.Zero, Yaw);
        float below = EntityOps.BoxCorners(probe).Min(c => Vector3.Dot(c, n));
        return below < 0 ? point - n * below : point;
    }

    public void Place(EditorContext ctx, Vector3 position)
    {
        var className = ClassName.Trim();
        if (className.Length == 0) { ctx.Status("Type a class name for the entity first", error: true); return; }
        Entity? placed = null;
        ctx.Session.Do($"Place {Label}", () =>
        {
            placed = ctx.Doc.Add(EntityClasses.Create(className, position, Yaw));
            ctx.Sel.ConvertTo(SelectMode.Object, ctx.Doc);
            ctx.Sel.Clear();
            ctx.Sel.Add(ElementRef.Object(placed.Id));
        });
        ctx.Status($"Placed {className} at {position.X:0.##}, {position.Y:0.##}, {position.Z:0.##}");
    }

    public void Draw(EditorContext ctx, ImDrawListPtr dl, Vector2 origin)
    {
        if (_cursor is not { } p) return;
        EntityDraw.Draw(dl, origin, ctx.View, EntityClasses.Create(ClassName, p, Yaw), EntityLook.Ghost);
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
