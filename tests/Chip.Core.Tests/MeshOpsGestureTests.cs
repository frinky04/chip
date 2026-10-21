using System.Numerics;
using Chip.Core.Geometry;
using Chip.Core.Scene;
using static Chip.Core.Tests.MeshOpsTests;

namespace Chip.Core.Tests;

/// <summary>Push/pull and edge extrude through the same <see cref="TransformGesture"/> surface the gizmo uses.</summary>
public class MeshOpsGestureTests
{
    static Session SessionWith(PolyMesh m)
    {
        var doc = new Document();
        doc.Add(m);
        var s = new Session();
        s.Reset(doc, null);
        return s;
    }

    [Fact]
    public void Push_pull_on_a_box_top_resizes_it_without_adding_faces()
    {
        var s = SessionWith(UnitBox());
        var id = s.Doc.Meshes[0].Id;
        s.Sel.ConvertTo(SelectMode.Face, s.Doc);
        s.Sel.Add(ElementRef.Face(id, 3));

        var g = TransformGesture.Start(s, "Push/Pull", GestureKind.PushPull)!;
        g.Apply(Matrix4x4.CreateTranslation(0.5f, 1, 0.25f), rigid: true); // off-normal motion is dropped
        g.Commit();

        var m = s.Doc.Meshes[0];
        Assert.Equal(6, m.Faces.Count);
        Assert.Equal(8, m.Positions.Count);
        Assert.True(IsClosedManifold(m));
        Assert.Equal(new Vector3(0, 0, 0), m.Bounds().Min);
        Assert.Equal(new Vector3(1, 2, 1), m.Bounds().Max);
        Assert.Equal("Push/Pull", s.UndoLabel);

        Assert.True(s.Undo());
        Assert.Equal(1f, s.Doc.Meshes[0].Bounds().Max.Y);
    }

    [Fact]
    public void Push_pull_can_push_a_face_inward()
    {
        var s = SessionWith(Box(Vector3.Zero, new Vector3(2, 2, 2)));
        s.Sel.ConvertTo(SelectMode.Face, s.Doc);
        s.Sel.Add(ElementRef.Face(s.Doc.Meshes[0].Id, 1)); // +X
        var g = TransformGesture.Start(s, "Push/Pull", GestureKind.PushPull)!;
        g.Apply(Matrix4x4.CreateTranslation(-0.5f, 0, 0), rigid: true);
        g.Commit();
        var m = s.Doc.Meshes[0];
        Assert.Equal(6, m.Faces.Count);
        Assert.Equal(1.5f, m.Bounds().Max.X, 5);
        Assert.True(IsClosedManifold(m));
    }

    [Fact]
    public void Repeated_rotate_extrudes_round_an_outside_pivot_sweep_an_arc()
    {
        var s = SessionWith(Box(Vector3.Zero, new Vector3(2, 2, 2)));
        var id = s.Doc.Meshes[0].Id;
        s.Sel.ConvertTo(SelectMode.Face, s.Doc);
        s.Sel.Add(ElementRef.Face(id, 3)); // top
        var pivot = new Vector3(3, 2, 1);  // a temporary pivot a metre off the box's +X side
        for (int i = 0; i < 3; i++)
        {
            var g = TransformGesture.Start(s, "Extrude", GestureKind.Extrude)!;
            g.Apply(TransformGesture.About(pivot, Matrix4x4.CreateRotationZ(-MathF.PI / 6)), rigid: true);
            g.Commit();
        }
        var m = s.Doc.Meshes[0];
        Assert.Equal(6 + 3 * 4, m.Faces.Count);
        Assert.True(IsClosedManifold(m));
        Assert.True(Vector3.Distance(Vector3.UnitX, m.FaceNormal(3)) < 1e-4f);
        Assert.True(Vector3.Distance(new Vector3(3, 4, 1), m.FaceCenter(3)) < 1e-4f);
    }

    [Fact]
    public void Unmoved_or_cancelled_push_pull_leaves_no_trace()
    {
        var s = SessionWith(UnitBox());
        s.Sel.ConvertTo(SelectMode.Face, s.Doc);
        s.Sel.Add(ElementRef.Face(s.Doc.Meshes[0].Id, 3));
        TransformGesture.Start(s, "Push/Pull", GestureKind.PushPull)!.Commit();
        Assert.Null(s.UndoLabel);

        var g = TransformGesture.Start(s, "Push/Pull", GestureKind.PushPull)!;
        g.Apply(Matrix4x4.CreateTranslation(0, 1, 0), rigid: true);
        g.Cancel();
        Assert.Null(s.UndoLabel);
        Assert.Equal(1f, s.Doc.Meshes[0].Bounds().Max.Y);
    }

    [Fact]
    public void Shift_dragging_an_open_edge_extrudes_a_quad_and_selects_the_new_edge()
    {
        var plane = new PolyMesh();
        plane.Positions.AddRange([new(0, 0, 0), new(0, 0, 1), new(1, 0, 1), new(1, 0, 0)]);
        plane.Faces.Add(new Face([0, 1, 2, 3], "dev_a", default));
        var s = SessionWith(plane);
        var id = s.Doc.Meshes[0].Id;
        s.Sel.ConvertTo(SelectMode.Edge, s.Doc);
        s.Sel.Add(ElementRef.EdgeOf(id, new Edge(2, 3))); // the x = 1 edge

        var g = TransformGesture.Start(s, "Extrude", GestureKind.Extrude)!;
        g.Apply(Matrix4x4.CreateTranslation(1, 0, 0), rigid: true);
        g.Commit();

        var m = s.Doc.Meshes[0];
        Assert.Equal(2, m.Faces.Count);
        Assert.True(IsConsistentlyOriented(m));
        Assert.True(m.FaceNormal(1).Y > 0.99f);
        var sel = Assert.Single(s.Sel.Items);
        Assert.Equal(2f, m.Positions[sel.A].X);
        Assert.Equal(2f, m.Positions[sel.B].X);

        Assert.True(s.Undo());
        Assert.Single(s.Doc.Meshes[0].Faces);
        Assert.Contains(ElementRef.EdgeOf(id, new Edge(2, 3)), s.Sel.Items);
    }

    [Fact]
    public void Shift_dragging_a_closed_edge_just_moves_it()
    {
        var s = SessionWith(UnitBox());
        s.Sel.ConvertTo(SelectMode.Edge, s.Doc);
        s.Sel.Add(ElementRef.EdgeOf(s.Doc.Meshes[0].Id, new Edge(2, 3)));
        var g = TransformGesture.Start(s, "Extrude", GestureKind.Extrude)!;
        g.Apply(Matrix4x4.CreateTranslation(0, 1, 0), rigid: true);
        g.Commit();
        Assert.Equal(6, s.Doc.Meshes[0].Faces.Count);
        Assert.Equal(2f, s.Doc.Meshes[0].Bounds().Max.Y);
    }
}
