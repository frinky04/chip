using System.Numerics;
using Chip.Core.Geometry;
using Chip.Core.Picking;
using Chip.Core.Scene;
using Chip.Core.Texturing;
using Chip.Editor.Features.CutTools;
using Chip.Editor.Features.MeshEditing;
using Chip.Editor.Features.Scene;
using Chip.Editor.Framework;
using Chip.Editor.Tools;
using Chip.Editor.Tools.Block;
using Chip.Editor.Viewport;
using ImGuiNET;

namespace Chip.Editor.Tests;

[Collection("Editor global entity classes")]
public sealed class ToolSnappingTests : IDisposable
{
    readonly nint _imgui = ImGui.CreateContext();

    public unsafe ToolSnappingTests()
    {
        var io = ImGui.GetIO();
        io.NativePtr->IniFilename = null;
        io.DisplaySize = new Vector2(1000);
        io.DeltaTime = 1f / 60;
        io.ConfigInputTrickleEventQueue = false;
        io.Fonts.GetTexDataAsRGBA32(out nint pixels, out int width, out int height);
    }

    static EditorContext Context(Document? doc = null)
    {
        var ctx = RegistryTests.Headless(doc);
        ctx.Grid = 0.25f;
        ctx.Camera.Projection = Projection.Top;
        ctx.Camera.Position = Vector3.Zero;
        ctx.Camera.OrthoHeight = 10;
        ctx.ViewportSize = new Vector2(1000);
        ctx.View = ctx.Camera.View(ctx.ViewportSize);
        ctx.ViewportHovered = true;
        return ctx;
    }

    static void At(EditorContext ctx, Vector3 point)
    {
        Assert.True(ctx.View.Project(point, out var mouse));
        ctx.Mouse = mouse;
    }

    static void Frame(Action update, bool ctrl = false, bool down = false)
    {
        var io = ImGui.GetIO();
        io.AddKeyEvent(ImGuiKey.ModCtrl, ctrl);
        io.AddMouseButtonEvent(0, down);
        ImGui.NewFrame();
        update();
        ImGui.Render();
    }

    static void Near(Vector3 expected, Vector3 actual) =>
        Assert.True(Vector3.Distance(expected, actual) < 1e-3f, $"Expected {expected}, got {actual}");

    [Fact]
    public void Construction_start_base_and_height_bypass_and_restore_snapping()
    {
        var ctx = Context();
        var plane = new ConstructionPlane();
        var raw = new Vector3(1.12f, 0, 2.13f);
        At(ctx, raw);
        Near(new Vector3(1, 0, 2.25f), plane.Pick(ctx, false)!.Value);
        Near(raw, plane.Pick(ctx, false, free: true)!.Value);
        Near(new Vector3(1, 0, 2.25f), plane.Hit(ctx.MouseRay())!.Value);
        Near(raw, plane.Hit(ctx.MouseRay(), free: true)!.Value);
        var heightRay = new Ray(new Vector3(1, 1.12f, 0), -Vector3.UnitX);
        Assert.Equal(1, plane.Height(heightRay, Vector3.Zero)!.Value, 3);
        Assert.Equal(1.12f, plane.Height(heightRay, Vector3.Zero, free: true)!.Value, 3);
        Assert.Equal(1, plane.Height(heightRay, Vector3.Zero)!.Value, 3);
        ctx.Snap = false;
        Near(raw, plane.Pick(ctx, false)!.Value);
    }

    [Theory]
    [InlineData(0.03f, 0.03f, CutSnapKind.Vertex)]
    [InlineData(1.12f, 0.03f, CutSnapKind.Edge)]
    [InlineData(1.12f, 1.12f, CutSnapKind.Surface)]
    public void Cut_points_bypass_vertex_edge_and_grid_snapping(float x, float z, CutSnapKind kind)
    {
        var doc = new Document();
        doc.Add(MeshOps.CreateBox(Vector3.Zero, new Vector3(2), "dev_a", TextureMapping.Default));
        var ctx = Context(doc);
        var raw = new Vector3(x, 2, z);
        At(ctx, raw);
        Assert.Equal(kind, CutSnap.At(ctx, ctx.Mouse)!.Value.Kind);
        var free = CutSnap.At(ctx, ctx.Mouse, free: true)!.Value;
        Assert.Equal(CutSnapKind.Surface, free.Kind);
        Near(raw, free.Position);
        Assert.Equal(kind, CutSnap.At(ctx, ctx.Mouse)!.Value.Kind);
    }

    [Fact]
    public void Cut_points_bypass_ground_grid_snapping()
    {
        var ctx = Context();
        var raw = new Vector3(1.12f, 0, 2.13f);
        At(ctx, raw);
        Near(new Vector3(1, 0, 2.25f), CutSnap.At(ctx, ctx.Mouse)!.Value.Position);
        Near(raw, CutSnap.At(ctx, ctx.Mouse, free: true)!.Value.Position);
    }

    [Theory]
    [InlineData("rectangle")]
    [InlineData("block")]
    [InlineData("polygon")]
    public void Creation_ctrl_bypasses_points_without_turning_the_plane(string id)
    {
        var ctx = Context();
        var tool = ctx.Tools.Single(t => t.Id == id);
        At(ctx, new Vector3(1.12f, 0, 1.12f));
        Frame(() => tool.Update(ctx), ctrl: true, down: true);
        if (id == "polygon")
        {
            Frame(() => tool.Update(ctx), ctrl: true);
            At(ctx, new Vector3(2.37f, 0, 1.12f));
            Frame(() => tool.Update(ctx), ctrl: true, down: true);
            Frame(() => tool.Update(ctx), ctrl: true);
        }
        At(ctx, new Vector3(2.37f, 0, 2.37f));
        if (id == "polygon") Frame(() => tool.Update(ctx), ctrl: true, down: true);
        Frame(() => tool.Update(ctx), ctrl: true);
        if (id != "rectangle")
        {
            ImGui.GetIO().AddKeyEvent(ImGuiKey.Enter, true);
            Frame(() => tool.Update(ctx), ctrl: true);
            if (id == "polygon")
            {
                ImGui.GetIO().AddKeyEvent(ImGuiKey.Enter, false);
                Frame(() => tool.Update(ctx), ctrl: true);
                ImGui.GetIO().AddKeyEvent(ImGuiKey.Enter, true);
                Frame(() => tool.Update(ctx), ctrl: true);
            }
        }
        var bounds = Assert.Single(ctx.Doc.Meshes).Bounds();
        Near(new Vector3(1.12f, 0, 1.12f), bounds.Min);
        Assert.Equal(2.37f, bounds.Max.X, 3);
        Assert.Equal(2.37f, bounds.Max.Z, 3);
    }

    [Theory]
    [InlineData("mesh.inset")]
    [InlineData("mesh.outset")]
    [InlineData("mesh.offset")]
    public void Width_tools_update_when_ctrl_changes_without_mouse_motion(string command)
    {
        var doc = new Document();
        var box = doc.Add(MeshOps.CreateBox(Vector3.Zero, new Vector3(2), "dev_a", TextureMapping.Default));
        var ctx = Context(doc);
        ctx.SetMode(SelectMode.Face);
        ctx.Sel.Add(ElementRef.Face(box.Id, Enumerable.Range(0, 6).Single(f => box.FaceNormal(f).Y > 0.9f)));
        Assert.True(ctx.Commands.Execute(command));
        var tool = ctx.ActiveTool!;
        float Amount() => tool is InsetTool inset ? inset.Amount : ((OffsetTool)tool).Amount;
        ctx.Mouse += new Vector2(12, 0);
        Frame(() => tool.Update(ctx));
        Assert.Equal(0.25f, Amount(), 3);
        Frame(() => tool.Update(ctx), ctrl: true);
        Assert.Equal(0.37f, Amount(), 3);
        Frame(() => tool.Update(ctx));
        Assert.Equal(0.25f, Amount(), 3);
        ctx.Snap = false;
        Frame(() => tool.Update(ctx));
        Assert.Equal(0.37f, Amount(), 3);
        tool.Cancel(ctx);
    }

    [Theory]
    [InlineData(false, 1f)]
    [InlineData(true, 1.12f)]
    public void Entity_placement_uses_ctrl_to_bypass_the_grid(bool ctrl, float expected)
    {
        var ctx = Context();
        var tool = new EntityTool { OnEntities = false };
        At(ctx, new Vector3(1.12f, 0, 1.12f));
        Frame(() => tool.Update(ctx), ctrl: ctrl, down: true);
        var entity = Assert.Single(ctx.Doc.Entities);
        Assert.Equal(expected, entity.Position.X, 3);
        Assert.Equal(expected, entity.Position.Z, 3);
    }

    public void Dispose() => ImGui.DestroyContext(_imgui);
}
