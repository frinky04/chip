using System.Numerics;
using Chip.Core.Geometry;
using Chip.Core.Picking;
using Chip.Core.Scene;
using Chip.Core.Texturing;

namespace Chip.Core.Tests;

/// <summary>Vertex/edge hover: forgiving radius, perspective-correct depth, clipped edges, but never through a wall.</summary>
public class EdgePickingTests
{
    static ViewInfo View(Vector3 eye, Vector3 target)
    {
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(75 * MathF.PI / 180, 16 / 9f, 0.02f, 2000);
        return new ViewInfo(Matrix4x4.CreateLookAt(eye, target, Vector3.UnitY) * projection, new(1600, 900), eye);
    }

    static (Document Doc, PolyMesh Mesh) Level(params PolyMesh[] meshes)
    {
        var doc = new Document();
        foreach (var m in meshes) doc.Add(m);
        return (doc, meshes[0]);
    }

    static PolyMesh Box(Vector3 min, Vector3 max) => MeshOps.CreateBox(min, max, "m", TextureMapping.Default);

    static Edge EdgeThrough(PolyMesh m, Vector3 a, Vector3 b) =>
        m.Edges().Single(e => (Near(m.Positions[e.A], a) && Near(m.Positions[e.B], b)) || (Near(m.Positions[e.A], b) && Near(m.Positions[e.B], a)));

    static bool Near(Vector3 x, Vector3 y) => Vector3.Distance(x, y) < 1e-4f;

    /// <summary>Picks with both the brute-force reference and the cache, asserting they agree.</summary>
    static ElementRef? Pick(Document doc, ViewInfo view, Vector2 mouse, SelectMode mode)
    {
        var session = new Session();
        session.Reset(doc, null);
        var brute = Picker.Pick(doc, view, mouse, mode);
        Assert.Equal(brute, new PickCache(session).Pick(view, mouse, mode));
        return brute;
    }

    static Vector2 Screen(ViewInfo view, Vector3 p)
    {
        Assert.True(view.Project(p, out var s));
        return s;
    }

    [Fact]
    public void A_long_edge_is_pickable_near_its_far_end_in_perspective()
    {
        // A 40 m long beam seen at a grazing angle from near one end: the old screen-space depth check put the
        // far part of the edge behind the beam's own top face.
        var (doc, beam) = Level(Box(Vector3.Zero, new Vector3(40, 1, 1)));
        var view = View(new Vector3(-2, 3, 5), new Vector3(20, 0.5f, 0.5f));
        var edge = EdgeThrough(beam, new Vector3(0, 1, 1), new Vector3(40, 1, 1));
        foreach (float x in new[] { 3f, 12f, 20f, 25f }) // well clear of the far end, where perspective crowds the end edge in
        {
            var mouse = Screen(view, new Vector3(x, 1, 1)) + new Vector2(0, -4); // just onto the top face
            Assert.Equal(ElementRef.EdgeOf(beam.Id, edge), Pick(doc, view, mouse, SelectMode.Edge));
        }
    }

    [Fact]
    public void An_edge_running_behind_the_camera_is_still_pickable()
    {
        // Standing over the middle of a long floor slab looking along it: its side edges start behind the camera.
        var (doc, slab) = Level(Box(new Vector3(0, -1, 0), new Vector3(40, 0, 4)));
        var view = View(new Vector3(20, 2, 2), new Vector3(0, 0, 2));
        var edge = EdgeThrough(slab, new Vector3(0, 0, 4), new Vector3(40, 0, 4));
        var mouse = Screen(view, new Vector3(12, 0, 4));
        Assert.Equal(ElementRef.EdgeOf(slab.Id, edge), Pick(doc, view, mouse, SelectMode.Edge));
    }

    [Fact]
    public void Edges_are_forgiving_up_to_the_radius()
    {
        var (doc, box) = Level(Box(Vector3.Zero, new Vector3(4, 2, 4)));
        var view = View(new Vector3(2, 6, 10), new Vector3(2, 1, 2));
        var edge = EdgeThrough(box, new Vector3(0, 2, 4), new Vector3(4, 2, 4)); // top front edge
        var on = Screen(view, new Vector3(2, 2, 4));
        Assert.Equal(ElementRef.EdgeOf(box.Id, edge), Pick(doc, view, on + new Vector2(0, -12), SelectMode.Edge)); // old radius was 7
        Assert.Equal(ElementRef.EdgeOf(box.Id, edge), Pick(doc, view, on + new Vector2(0, 12), SelectMode.Edge));
    }

    [Fact]
    public void A_wide_radius_never_reaches_an_edge_hidden_behind_a_wall()
    {
        // A wall in front of a box: the box's top edge sits just above the wall's top on screen but behind it in depth.
        var box = Box(new Vector3(0, 0, 0), new Vector3(4, 2, 2));
        var wall = Box(new Vector3(-1, 0, 4), new Vector3(5, 1.9f, 4.2f));
        var (doc, _) = Level(box, wall);
        var view = View(new Vector3(2, 2.2f, 12), new Vector3(2, 1, 0));
        var hiddenEdge = EdgeThrough(box, new Vector3(0, 0, 2), new Vector3(4, 0, 2)); // box's bottom front edge, behind the wall
        var mouse = Screen(view, new Vector3(2, 0, 2));
        var picked = Pick(doc, view, mouse, SelectMode.Edge);
        Assert.NotEqual(ElementRef.EdgeOf(box.Id, hiddenEdge), picked);
    }

    [Fact]
    public void Vertices_use_the_same_visibility_rule()
    {
        var (doc, box) = Level(Box(Vector3.Zero, new Vector3(4, 2, 4)));
        var view = View(new Vector3(2, 6, 10), new Vector3(2, 1, 2));
        int corner = box.Positions.FindIndex(p => Near(p, new Vector3(4, 2, 4)));
        int hidden = box.Positions.FindIndex(p => Near(p, new Vector3(4, 0, 0))); // far bottom corner, behind the box
        Assert.Equal(ElementRef.Vertex(box.Id, corner), Pick(doc, view, Screen(view, box.Positions[corner]) + new Vector2(10, 5), SelectMode.Vertex));
        Assert.NotEqual(ElementRef.Vertex(box.Id, hidden), Pick(doc, view, Screen(view, box.Positions[hidden]), SelectMode.Vertex));
    }
}
