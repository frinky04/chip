using System.Numerics;
using Chip.Core.Geometry.Primitives;
using Chip.Core.Scene;
using Chip.Core.Texturing;
using Chip.Editor.Features.Dimensions;
using Chip.Editor.Framework;
using Chip.Editor.Tools.Block;
using Chip.Editor.UI;
using ImGuiNET;
using static Chip.Editor.UI.Widgets;

namespace Chip.Editor.Tools;

/// <summary>
/// Hammer-style block creation: drag the base on the hovered surface (or the ground), release, move to set the height,
/// click or Enter to create. The dragged box is filled by the chosen shape (box, cylinder, wedge, stairs, arch, sphere,
/// torus, spiral stairs), previewed as a wireframe. Ctrl at the start of the drag stands the base plane up toward the camera.
/// Shape and parameters live on the tool instance, so they are remembered between uses.
/// </summary>
public sealed class BlockTool : IViewportTool
{
    public string Id => "block";
    public string Name => "Block";
    public Icon Icon => Icon.Block;
    public string? Hotkey => "Shift+B";
    public int Order => 10;

    public static readonly PrimitiveShape[] ShapeList = Enum.GetValues<PrimitiveShape>();
    static readonly string[] ShapeLabels = ["Box", "Cylinder", "Wedge", "Stairs", "Arch", "Sphere", "Torus", "Spiral stairs"];

    public static string Label(PrimitiveShape shape) => ShapeLabels[(int)shape];

    public PrimitiveShape Shape { get; set; } = PrimitiveShape.Box;
    public PrimitiveOptions Options { get; } = new();
    /// <summary>Directional shapes (wedge, stairs, arch) run along the other side of the base.</summary>
    public bool Turn { get; set; }
    /// <summary>Stairs: derive the step count from <see cref="StepHeight"/> instead of <see cref="PrimitiveOptions.Steps"/>.</summary>
    public bool StepsByHeight { get; set; }
    public float StepHeight { get; set; } = 0.25f;

    enum Stage { Idle, Base, Height }

    readonly ConstructionPlane _plane = new();
    Stage _stage;
    Vector3 _a, _b;
    float _height, _grid = 0.25f;
    Vector3? _cursor; // where a drag would start, shown before the first click

    bool _frozen; // set by PreviewForCapture: hold the gesture still for headless captures

    public bool Busy => _stage != Stage.Idle;
    public bool HasOptions => true;

    public (string Key, string Action)[] Hints => _stage switch
    {
        Stage.Idle => [("Drag", "draw a base on a surface or the grid"), ("Ctrl+drag", "on a plane facing you"), ("Esc", "exit tool")],
        Stage.Base => [("Release", "set the base")],
        _ => [("Move", "set height"), ("Click/Enter", $"create {Label(Shape).ToLowerInvariant()}"), ("Esc", "cancel")],
    };

    public bool Cancel(EditorContext ctx)
    {
        bool wasBusy = Busy;
        _stage = Stage.Idle;
        _cursor = null;
        _frozen = false;
        return wasBusy;
    }

    public void Update(EditorContext ctx)
    {
        ctx.Hover = null;
        if (_frozen) return; // capture preview: ignore the (absent) mouse
        _grid = ctx.Grid;
        var ray = ctx.MouseRay();
        switch (_stage)
        {
            case Stage.Idle:
                _cursor = ctx.ViewportHovered ? _plane.Pick(ctx, ImGui.GetIO().KeyCtrl) : null;
                if (_cursor == null || !ImGui.IsMouseClicked(ImGuiMouseButton.Left)) return;
                _a = _b = _cursor.Value;
                _stage = Stage.Base;
                return;

            case Stage.Base:
                if (_plane.Hit(ray) is { } p) _b = p;
                if (ImGui.IsMouseDown(ImGuiMouseButton.Left)) return;
                var d = Vector3.Abs(_b - _a);
                int flat = (d.X < 1e-3f ? 1 : 0) + (d.Y < 1e-3f ? 1 : 0) + (d.Z < 1e-3f ? 1 : 0);
                _stage = flat > 1 ? Stage.Idle : Stage.Height;
                _height = _grid;
                return;

            default:
                if (_plane.Height(ray, _b) is { } h) _height = h;
                bool confirm = (ctx.ViewportHovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                    || ImGui.IsKeyPressed(ImGuiKey.Enter) || ImGui.IsKeyPressed(ImGuiKey.KeypadEnter);
                if (!confirm) return;
                _stage = Stage.Idle;
                Create(ctx, Frame());
                return;
        }
    }

    void Create(EditorContext ctx, PrimitiveFrame frame)
    {
        var mesh = Primitive.Build(Shape, EffectiveOptions(frame), frame, ctx.ActiveMaterial, ctx.Materials.MappingFor(ctx.ActiveMaterial));
        ctx.Session.Do($"Create {Label(Shape)}", () =>
        {
            ctx.Doc.Add(mesh);
            ctx.Sel.ConvertTo(SelectMode.Object, ctx.Doc);
            ctx.Sel.Clear();
            ctx.Sel.Add(ElementRef.Object(mesh.Id));
        });
    }

    PrimitiveFrame Frame() => PrimitiveFrame.FromBlock(_a, _b, _plane.Normal, MathF.Abs(_height) < 1e-4f ? _grid : _height, Turn);

    PrimitiveOptions EffectiveOptions(PrimitiveFrame frame)
    {
        if (!StepsByHeight || Shape is not (PrimitiveShape.Stairs or PrimitiveShape.SpiralStairs)) return Options;
        var o = Options.Clone();
        o.Steps = Primitive.StepsFor(frame.Size.Y, StepHeight);
        return o;
    }

    /// <summary>Puts the tool mid-gesture with a fixed 4 × 3 × 3 m box, so headless captures show the shape preview.</summary>
    public void PreviewForCapture()
    {
        _a = new Vector3(2, 0, 2);
        _b = new Vector3(6, 0, 5);
        _height = 3;
        _stage = Stage.Height;
        _frozen = true;
    }

    public void Draw(EditorContext ctx, ImDrawListPtr dl, Vector2 origin)
    {
        var v = ctx.View;
        switch (_stage)
        {
            case Stage.Idle:
                if (_cursor is { } c) _plane.DrawCursor(dl, origin, v, c);
                return;
            case Stage.Base:
                {
                    var (min, max) = (Vector3.Min(_a, _b), Vector3.Max(_a, _b));
                    Overlay.Box(dl, origin, v, min, max, Theme.AccentU32, Theme.Px(2));
                    DimensionDraw.Box(dl, origin, v, min, max);
                    return;
                }
            default:
                {
                    var frame = Frame();
                    var options = EffectiveOptions(frame);
                    if (Shape == PrimitiveShape.Box) Overlay.Box(dl, origin, v, frame.Min, frame.Max, Theme.AccentU32, Theme.Px(2));
                    else
                    {
                        Overlay.Box(dl, origin, v, frame.Min, frame.Max, Overlay.Faint, Theme.Px(1));
                        Overlay.Wire(dl, origin, v, Primitive.Build(Shape, options, frame, "", TextureMapping.Default));
                    }
                    DimensionDraw.Box(dl, origin, v, frame.Min, frame.Max);
                    if (Shape is PrimitiveShape.Stairs or PrimitiveShape.SpiralStairs)
                        Overlay.Label(dl, origin, v, frame.Max, $"{options.Steps} steps of {Labels.Measure(frame.Size.Y / Math.Max(1, options.Steps))}");
                    return;
                }
        }
    }

    public void DrawOptions(EditorContext ctx)
    {
        RowLabel("Shape");
        int shape = (int)Shape;
        if (ImGui.Combo("##shape", ref shape, ShapeLabels, ShapeLabels.Length)) Shape = (PrimitiveShape)shape;

        var o = Options;
        switch (Shape)
        {
            case PrimitiveShape.Cylinder:
                Int("Sides", "Sides around the cylinder", ref o.Sides);
                break;
            case PrimitiveShape.Stairs:
                StepOptions(ctx);
                break;
            case PrimitiveShape.Arch:
                Int("Curve", "Segments along the curve", ref o.ArchSegments);
                Float("Arc", "Degrees swept by the arch; 180 is a semicircle", ref o.ArchDegrees, 5, "%.0f°");
                Float("Width", "Thickness of the arch band in metres", ref o.ArchThickness, ctx.Grid, "%.3g m");
                break;
            case PrimitiveShape.Sphere:
                Int("Rings", "Bands from pole to pole", ref o.SphereRings);
                Int("Around", "Segments around the equator", ref o.SphereSegments);
                break;
            case PrimitiveShape.Torus:
                Int("Around", "Segments around the ring", ref o.TorusSegments);
                Int("Sides", "Segments around the tube", ref o.TorusSides);
                Percent("Tube", "Tube radius as a share of the outer radius", ref o.TorusTube, 0.05f, 0.5f);
                break;
            case PrimitiveShape.SpiralStairs:
                StepOptions(ctx);
                Float("Turn", "Total rotation in degrees", ref o.SpiralDegrees, 15, "%.0f°");
                Percent("Inner", "Opening in the middle as a share of the radius; 0 makes a solid centre", ref o.SpiralInner, 0, 0.9f);
                RowLabel("");
                ImGui.Checkbox("Solid to floor", ref o.SpiralSolid);
                Tip("Each step reaches the floor instead of floating");
                RowLabel("");
                ImGui.Checkbox("Clockwise", ref o.SpiralClockwise);
                break;
        }
        if (Shape is PrimitiveShape.Wedge or PrimitiveShape.Stairs or PrimitiveShape.Arch)
        {
            RowLabel("");
            bool turn = Turn;
            if (ImGui.Checkbox("Rotate 90°", ref turn)) Turn = turn;
            Tip("Run along the other side of the base. By default the shape runs along the longer side, in the drag direction");
        }
        if (Shape is PrimitiveShape.Cylinder or PrimitiveShape.Arch or PrimitiveShape.Sphere or PrimitiveShape.Torus)
        {
            RowLabel("");
            ImGui.Checkbox("Smooth", ref o.Smooth);
            Tip("Soft edges across the curved surface");
        }
        o.Clamp();
    }

    void StepOptions(EditorContext ctx)
    {
        RowLabel("Steps", "Set the number of steps, or a step height that the rise is divided into");
        int mode = StepsByHeight ? 1 : 0;
        if (ImGui.Combo("##stepmode", ref mode, "By count\0By height\0")) StepsByHeight = mode == 1;
        if (!StepsByHeight) Int("Count", "Number of steps", ref Options.Steps);
        else
        {
            float sh = StepHeight;
            Float("Height", "Target step height; the count is the rise divided by this, rounded", ref sh, ctx.Grid, "%.3g m");
            StepHeight = MathF.Max(sh, 1 / 64f);
        }
    }

    static void Int(string label, string tip, ref int value)
    {
        RowLabel(label, tip);
        ImGui.InputInt($"##{label}", ref value);
    }

    static void Float(string label, string tip, ref float value, float step, string fmt)
    {
        RowLabel(label, tip);
        ImGui.InputFloat($"##{label}", ref value, step, step * 4, fmt);
    }

    static void Percent(string label, string tip, ref float ratio, float min, float max)
    {
        RowLabel(label, tip);
        float pct = ratio * 100;
        if (Widgets.ValueSlider($"##{label}", "", ref pct, min * 100, max * 100, "%.0f%%", p => $"{p:0}%")) ratio = pct / 100;
    }
}
