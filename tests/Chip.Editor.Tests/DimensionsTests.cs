using System.Numerics;
using Chip.Core.Geometry;
using Chip.Core.Picking;
using Chip.Core.Scene;
using Chip.Core.Texturing;
using Chip.Editor.Features.Dimensions;
using Chip.Editor.Framework;
using Chip.Editor.Viewport;

namespace Chip.Editor.Tests;

/// <summary>Edge measurements, the drag readout, and typed values during a drag.</summary>
public class DimensionsTests
{
    static (Document Doc, PolyMesh Box) BoxDoc()
    {
        var doc = new Document();
        var box = doc.Add(MeshOps.CreateBox(Vector3.Zero, new Vector3(1, 2, 3), "dev_a", TextureMapping.Default));
        return (doc, box);
    }

    static Selection Select(SelectMode mode, params ElementRef[] items)
    {
        var sel = new Selection();
        if (mode != SelectMode.Object) sel.ConvertTo(mode, new Document());
        foreach (var i in items) sel.Add(i);
        return sel;
    }

    static float[] Sizes(List<Dim> dims) =>
        dims.GroupBy(d => d.Group).Select(g => MathF.Round(g.First().Length, 4)).Order().ToArray();

    [Fact]
    public void One_face_shows_its_width_and_height_once_each()
    {
        var (doc, box) = BoxDoc();
        int top = Enumerable.Range(0, 6).Single(f => box.FaceNormal(f).Y > 0.9f);
        var dims = Dimensions.ForSelection(doc, Select(SelectMode.Face, ElementRef.Face(box.Id, top)), null);
        Assert.Equal(4, dims.Count);
        Assert.Equal([1f, 3f], Sizes(dims));
        Assert.Equal([0, 2], dims.Select(d => d.Axis).Distinct().Order());
    }

    [Fact]
    public void A_whole_box_of_faces_shows_its_three_sizes_and_a_split_floor_only_its_outline()
    {
        var (doc, box) = BoxDoc();
        var all = Enumerable.Range(0, 6).Select(f => ElementRef.Face(box.Id, f)).ToArray();
        Assert.Equal([1f, 2f, 3f], Sizes(Dimensions.ForSelection(doc, Select(SelectMode.Face, all), null)));

        // Two coplanar quads side by side: the edge between them isn't part of the outline.
        var floor = new PolyMesh();
        floor.Positions.AddRange([new(0, 0, 0), new(0, 0, 1), new(1, 0, 1), new(1, 0, 0), new(2, 0, 1), new(2, 0, 0)]);
        floor.Faces.Add(new Face([0, 1, 2, 3], "dev_a", default));
        floor.Faces.Add(new Face([3, 2, 4, 5], "dev_a", default));
        var d2 = new Document();
        d2.Add(floor);
        var dims = Dimensions.ForSelection(d2, Select(SelectMode.Face, ElementRef.Face(floor.Id, 0), ElementRef.Face(floor.Id, 1)), null);
        Assert.Equal(6, dims.Count);
        Assert.DoesNotContain(dims, d => d.A.X == 1 && d.B.X == 1);
    }

    [Fact]
    public void Objects_edges_and_vertex_pairs_measure_what_is_selected()
    {
        var (doc, box) = BoxDoc();
        Assert.Equal([1f, 2f, 3f], Sizes(Dimensions.ForSelection(doc, Select(SelectMode.Object, ElementRef.Object(box.Id)), box.Bounds())));

        var uprights = box.Edges().Where(e => Dimensions.ForSelection(doc, Select(SelectMode.Edge, ElementRef.EdgeOf(box.Id, e)), null)[0].Axis == 1).ToList();
        Assert.Equal(4, uprights.Count);
        var dims = Dimensions.ForSelection(doc, Select(SelectMode.Edge, uprights.Select(e => ElementRef.EdgeOf(box.Id, e)).ToArray()), null);
        Assert.Single(dims.Select(d => d.Group).Distinct()); // four equal uprights, one label

        var pair = Dimensions.ForSelection(doc, Select(SelectMode.Vertex, ElementRef.Vertex(box.Id, 0), ElementRef.Vertex(box.Id, 7)), null);
        Assert.Equal(MathF.Sqrt(14), Assert.Single(pair).Length, 4);
        Assert.Equal(-1, pair[0].Axis);
    }

    [Fact]
    public void Two_stacked_wall_faces_measure_as_one_wall_overall()
    {
        // A 12.5 m wall split at 2.75 m: faces 0-2.75 and 2.75-3.25 high, facing -Z.
        var wall = new PolyMesh();
        wall.Positions.AddRange([new(0, 0, 0), new(0, 2.75f, 0), new(12.5f, 2.75f, 0), new(12.5f, 0, 0), new(0, 3.25f, 0), new(12.5f, 3.25f, 0)]);
        wall.Faces.Add(new Face([0, 1, 2, 3], "dev_a", default));
        wall.Faces.Add(new Face([1, 4, 5, 2], "dev_a", default));
        var doc = new Document();
        doc.Add(wall);
        var both = Select(SelectMode.Face, ElementRef.Face(wall.Id, 0), ElementRef.Face(wall.Id, 1));
        var e = Dimensions.Overall(doc, both)!.Value;
        Assert.Equal([12.5f, 3.25f], e.Sides.Select(s => MathF.Round(s.Length(), 4)).ToArray());
        // The edges say 12.5, 2.75 and 50 cm: only the 3.25 m height is new.
        Assert.Equal([0.5f, 2.75f, 12.5f], Sizes(Dimensions.ForSelection(doc, both, null)));

        Assert.Null(Dimensions.Overall(doc, Select(SelectMode.Face, ElementRef.Face(wall.Id, 0)))); // one face: its edges say it all
    }

    [Fact]
    public void Faces_on_a_slope_are_measured_along_the_slope()
    {
        // A roof rising 1 m over 1 m (45°), 4 m along X, in two strips.
        var roof = new PolyMesh();
        roof.Positions.AddRange([new(0, 0, 0), new(0, 0.5f, 0.5f), new(4, 0.5f, 0.5f), new(4, 0, 0), new(0, 1, 1), new(4, 1, 1)]);
        roof.Faces.Add(new Face([0, 1, 2, 3], "dev_a", default));
        roof.Faces.Add(new Face([1, 4, 5, 2], "dev_a", default));
        var doc = new Document();
        doc.Add(roof);
        var e = Dimensions.Overall(doc, Select(SelectMode.Face, ElementRef.Face(roof.Id, 0), ElementRef.Face(roof.Id, 1)))!.Value;
        Assert.Equal([4f, MathF.Round(MathF.Sqrt(2), 4)], e.Sides.Select(s => MathF.Round(s.Length(), 4)).ToArray());
    }

    [Fact]
    public void Dimension_modes_are_radio_items_that_turn_labels_on()
    {
        var ctx = RegistryTests.Headless();
        Assert.True(ctx.Commands.Find("view.dimensionsBoth")!.IsChecked!());
        ctx.Commands.Execute("view.dimensions"); // off
        ctx.Commands.Execute("view.dimensionsOverall");
        Assert.True(ctx.Commands.Find("view.dimensions")!.IsChecked!());
        Assert.True(ctx.Commands.Find("view.dimensionsOverall")!.IsChecked!());
        Assert.False(ctx.Commands.Find("view.dimensionsBoth")!.IsChecked!());
    }

    [Theory]
    [InlineData(1.125f, "1.125 m")]
    [InlineData(0.30000001f, "30 cm")]
    [InlineData(0.125f, "12.5 cm")]
    [InlineData(2f, "2 m")]
    public void Measurements_keep_the_millimetres(float metres, string text) => Assert.Equal(text, Labels.Measure(metres));

    [Theory]
    [InlineData("1.5", EntryUnit.Length, new[] { 1.5f })]
    [InlineData("150cm", EntryUnit.Length, new[] { 1.5f })]
    [InlineData("150 cm", EntryUnit.Length, new[] { 1.5f })]
    [InlineData("25mm", EntryUnit.Length, new[] { 0.025f })]
    [InlineData("1 0 -2", EntryUnit.Length, new[] { 1f, 0f, -2f })]
    [InlineData("1, −2", EntryUnit.Length, new[] { 1f, -2f })]
    [InlineData(".5", EntryUnit.Length, new[] { 0.5f })]
    [InlineData("90°", EntryUnit.Angle, new[] { 90f })]
    [InlineData("-45", EntryUnit.Angle, new[] { -45f })]
    [InlineData("150%", EntryUnit.Factor, new[] { 1.5f })]
    [InlineData("x2", EntryUnit.Factor, new[] { 2f })]
    public void Typed_values_parse(string text, EntryUnit unit, float[] expected)
    {
        var values = NumericEntry.Parse(text, unit);
        Assert.NotNull(values);
        Assert.Equal(expected.Length, values.Length);
        for (int i = 0; i < expected.Length; i++) Assert.Equal(expected[i], values[i], 5);
    }

    [Theory]
    [InlineData("", EntryUnit.Length)]
    [InlineData("abc", EntryUnit.Length)]
    [InlineData("1 2 3 4", EntryUnit.Length)]
    [InlineData("5cm", EntryUnit.Angle)]
    [InlineData("50%", EntryUnit.Length)]
    [InlineData("1.2.3", EntryUnit.Length)]
    public void Nonsense_is_refused(string text, EntryUnit unit) => Assert.Null(NumericEntry.Parse(text, unit));

    /// <summary>Front view centred on the origin, 1 px = 1 cm.</summary>
    static ViewInfo Front() => new Camera { Projection = Projection.Front, Position = Vector3.Zero, OrthoHeight = 10 }.View(new Vector2(1000, 1000));

    static readonly Vector2 Center = new(500, 500);

    [Fact]
    public void A_typed_move_goes_exactly_that_far_along_the_handle()
    {
        var g = new Gizmo();
        g.BeginDrag(Handle.X, Front(), Vector3.Zero, Center);
        Assert.Equal(new Vector3(1.5f, 0, 0), g.Exact([1.5f])!.Value.M.Translation);
        Assert.Equal(new Vector3(1.5f, 0, 0), g.Amount.Delta);
        g.EndDrag();

        g.BeginDrag(Handle.PlaneXY, Front(), Vector3.Zero, Center);
        Assert.Equal(new Vector3(1, 2, 0), g.Exact([1, 2])!.Value.M.Translation);
        Assert.Equal(new Vector3(1, 0, 0), g.Exact([1, 0, 5])!.Value.M.Translation); // kept to the plane
        g.EndDrag();

        g.BeginDrag(Handle.Center, Front(), Vector3.Zero, Center);
        Assert.Null(g.Exact([1])); // no direction yet
        g.DragTransform(Front(), new Vector2(600, 500), 0.25f, snap: false);
        Assert.Equal(new Vector3(2, 0, 0), g.Exact([2])!.Value.M.Translation); // along the way it was heading
    }

    [Fact]
    public void Typed_rotation_and_scale_work_about_the_pivot()
    {
        var g = new Gizmo { Mode = GizmoMode.Rotate };
        g.BeginDrag(Handle.Z, Front(), new Vector3(1, 0, 0), Center);
        var r = g.Exact([90])!.Value.M;
        Assert.True(Vector3.Distance(new Vector3(1, 1, 0), Vector3.Transform(new Vector3(2, 0, 0), r)) < 1e-4f);
        g.EndDrag();

        g = new Gizmo { Mode = GizmoMode.Scale };
        g.BeginDrag(Handle.Y, Front(), Vector3.Zero, Center);
        var (m, rigid) = g.Exact([2])!.Value;
        Assert.False(rigid);
        Assert.Equal(new Vector3(1, 4, 1), Vector3.Transform(new Vector3(1, 2, 1), m));
        Assert.Null(g.Exact([0]));
    }

    [Fact]
    public void The_readout_names_each_moved_axis_and_the_total()
    {
        var basis = new[] { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ };
        var amount = new Gizmo.DragAmount(Vector3.Zero, new Vector3(1.25f, 0, 0), 0, Vector3.One);
        Assert.Equal([("Move", -1), ("X +1.25 m", 0)],
            DragReadout.Describe(GizmoMode.Translate, Handle.X, basis, amount, GestureKind.Transform, Vector3.One));

        amount = amount with { Delta = new Vector3(3, 0, -4) };
        Assert.Equal([("Extrude", -1), ("X +3 m", 0), ("Z −4 m", 2), ("5 m in all", -1)],
            DragReadout.Describe(GizmoMode.Translate, Handle.PlaneXZ, basis, amount, GestureKind.Extrude, Vector3.One));

        Assert.Equal([("Rotate", -1), ("+90°", 1)],
            DragReadout.Describe(GizmoMode.Rotate, Handle.Y, basis, amount with { Degrees = 90 }, GestureKind.Transform, Vector3.One));

        Assert.Equal([("Scale", -1), ("×2", 1), ("2 m → 4 m", 1)],
            DragReadout.Describe(GizmoMode.Scale, Handle.Y, basis, amount with { Scale = new Vector3(1, 2, 1) }, GestureKind.Transform, new Vector3(1, 2, 3)));
    }

    [Fact]
    public void The_dimensions_toggle_is_on_by_default()
    {
        var ctx = RegistryTests.Headless();
        var cmd = ctx.Commands.Find("view.dimensions")!;
        Assert.True(cmd.IsChecked!());
        ctx.Commands.Execute("view.dimensions");
        Assert.False(cmd.IsChecked!());
    }
}
