using System.Numerics;
using Chip.Core.Geometry;
using Chip.Core.Picking;
using Chip.Core.Scene;
using Chip.Core.Texturing;

namespace Chip.Core.Tests;

/// <summary>Fixtures for selection tests: a unit box, an n×n subdivided plane and an open (or capped) tube.</summary>
public static class SelectionFixtures
{
    public const string Mat = "dev_a";

    public static PolyMesh Box() => MeshOps.CreateBox(Vector3.Zero, Vector3.One, Mat, TextureMapping.Default);

    /// <summary>n×n quads on the XZ plane, facing +Y. Vertex (i, j) is index j*(n+1)+i; face (i, j) is index j*n+i.</summary>
    public static PolyMesh Plane(int n = 3)
    {
        var m = new PolyMesh { Name = "plane" };
        for (int j = 0; j <= n; j++)
            for (int i = 0; i <= n; i++) m.Positions.Add(new Vector3(i, 0, j));
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
                m.Faces.Add(new Face([V(i, j, n), V(i, j + 1, n), V(i + 1, j + 1, n), V(i + 1, j, n)], Mat, default));
        return m;
    }

    public static int V(int i, int j, int n = 3) => j * (n + 1) + i;
    public static int F(int i, int j, int n = 3) => j * n + i;

    /// <summary>A cylinder wall: <paramref name="sides"/> around, <paramref name="rows"/> quads tall. Vertex (side s, ring k) is k*sides+s.</summary>
    public static PolyMesh Tube(int sides = 8, int rows = 2, bool caps = false)
    {
        var m = new PolyMesh { Name = "tube" };
        for (int k = 0; k <= rows; k++)
            for (int s = 0; s < sides; s++)
            {
                float a = s * MathF.Tau / sides;
                m.Positions.Add(new Vector3(MathF.Cos(a), k, MathF.Sin(a)));
            }
        for (int k = 0; k < rows; k++)
            for (int s = 0; s < sides; s++)
            {
                int s1 = (s + 1) % sides;
                m.Faces.Add(new Face([k * sides + s, (k + 1) * sides + s, (k + 1) * sides + s1, k * sides + s1], Mat, default));
            }
        if (caps)
        {
            m.Faces.Add(new Face(Enumerable.Range(0, sides).ToArray(), Mat, default));
            m.Faces.Add(new Face(Enumerable.Range(0, sides).Select(s => rows * sides + s).Reverse().ToArray(), Mat, default));
        }
        return m;
    }

    public static Document Doc(params PolyMesh[] meshes)
    {
        var doc = new Document();
        foreach (var m in meshes) doc.Add(m);
        return doc;
    }

    public static Selection Select(SelectMode mode, params ElementRef[] items)
    {
        var sel = new Selection();
        SelectionOps.Replace(sel, mode, items, items.Length > 0 ? items[^1] : null);
        return sel;
    }
}

public class SelectionOpsLoopRingTests
{
    static Edge E(int a, int b) => new(a, b);
    static int V(int i, int j) => SelectionFixtures.V(i, j);

    [Fact]
    public void Box_loops_stop_at_the_three_edge_corners_and_rings_go_all_the_way_round()
    {
        var topo = new SelectionTopology(SelectionFixtures.Box());
        var vertical = E(0, 2); // x=0, z=0, along Y
        Assert.Equal([vertical], topo.EdgeLoop(vertical));
        var ring = topo.EdgeRing(vertical);
        Assert.Equal(4, ring.Count);
        Assert.Equal([E(0, 2), E(1, 3), E(4, 6), E(5, 7)], ring.OrderBy(e => e.A).ToList());
    }

    [Fact]
    public void Plane_interior_loop_runs_border_to_border()
    {
        var topo = new SelectionTopology(SelectionFixtures.Plane());
        var loop = topo.EdgeLoop(E(V(1, 1), V(1, 2)));
        Assert.Equal([E(V(1, 0), V(1, 1)), E(V(1, 1), V(1, 2)), E(V(1, 2), V(1, 3))], loop);
    }

    [Fact]
    public void Plane_border_edge_loop_follows_the_open_border()
    {
        var topo = new SelectionTopology(SelectionFixtures.Plane());
        var loop = topo.EdgeLoop(E(V(1, 0), V(2, 0)));
        Assert.Equal(12, loop.Count);
        Assert.All(loop, e => Assert.True(topo.IsBoundary(e)));
    }

    [Fact]
    public void Plane_ring_crosses_the_quads_and_stops_at_the_border()
    {
        var topo = new SelectionTopology(SelectionFixtures.Plane());
        var ring = topo.EdgeRing(E(V(1, 1), V(2, 1)));
        Assert.Equal([E(V(1, 0), V(2, 0)), E(V(1, 1), V(2, 1)), E(V(1, 2), V(2, 2)), E(V(1, 3), V(2, 3))], ring.OrderBy(e => e.A));
    }

    [Fact]
    public void Tube_middle_loop_closes_and_vertical_loop_stops_at_both_borders()
    {
        var topo = new SelectionTopology(SelectionFixtures.Tube(8, 2));
        var middle = topo.EdgeLoop(E(8, 9));
        Assert.Equal(8, middle.Count);
        Assert.All(middle, e => Assert.True(e.A is >= 8 and < 16 && e.B is >= 8 and < 16));
        Assert.Equal([E(0, 8), E(8, 16)], topo.EdgeLoop(E(0, 8)));
        Assert.Equal(8, topo.EdgeLoop(E(16, 17)).Count); // top border
    }

    [Fact]
    public void Tube_rings_close_around_and_run_top_to_bottom()
    {
        var topo = new SelectionTopology(SelectionFixtures.Tube(8, 2));
        var around = topo.EdgeRing(E(0, 8));
        Assert.Equal(8, around.Count);
        Assert.Equal(8, around.Distinct().Count());
        Assert.Equal([E(0, 1), E(8, 9), E(16, 17)], topo.EdgeRing(E(8, 9)).OrderBy(e => e.A).ToList());
    }

    [Fact]
    public void Capped_tube_loops_stop_at_the_ngon_cap_poles()
    {
        var topo = new SelectionTopology(SelectionFixtures.Tube(8, 2, caps: true));
        Assert.Equal(8, topo.EdgeLoop(E(8, 9)).Count);
        Assert.Equal([E(0, 8), E(8, 16)], topo.EdgeLoop(E(0, 8)));      // ring verts are valence 3
        Assert.Equal([E(16, 17)], topo.EdgeLoop(E(16, 17)));              // cap edge: not a border any more
        Assert.Equal(8, topo.EdgeRing(E(0, 8)).Count);
        Assert.Equal(3, topo.EdgeRing(E(8, 9)).Count);                    // stops at the n-gon caps
    }

    [Fact]
    public void Ring_then_convert_to_faces_gives_the_face_loop()
    {
        var tube = SelectionFixtures.Tube(8, 2);
        var doc = SelectionFixtures.Doc(tube);
        var sel = SelectionFixtures.Select(SelectMode.Edge, ElementRef.EdgeOf(tube.Id, E(0, 8)));
        Assert.Equal(7, SelectionOps.SelectRings(doc, sel));
        sel.ConvertTo(SelectMode.Face, doc);
        Assert.Equal(Enumerable.Range(0, 8), sel.Items.Select(i => i.A).Order());
    }

    [Fact]
    public void Loop_command_keeps_the_active_edge_and_unions_every_selected_edge()
    {
        var plane = SelectionFixtures.Plane();
        var doc = SelectionFixtures.Doc(plane);
        var a = ElementRef.EdgeOf(plane.Id, E(V(1, 1), V(1, 2)));
        var b = ElementRef.EdgeOf(plane.Id, E(V(1, 1), V(2, 1)));
        var sel = SelectionFixtures.Select(SelectMode.Edge, a, b);
        SelectionOps.SelectLoops(doc, sel);
        Assert.Equal(6, sel.Count);
        Assert.Equal(b, sel.Active);
    }
}

public class SelectionOpsGrowShrinkTests
{
    static int V(int i, int j) => SelectionFixtures.V(i, j);
    static int F(int i, int j) => SelectionFixtures.F(i, j);

    [Fact]
    public void Faces_grow_across_shared_edges_and_shrink_back()
    {
        var plane = SelectionFixtures.Plane();
        var doc = SelectionFixtures.Doc(plane);
        var center = ElementRef.Face(plane.Id, F(1, 1));
        var sel = SelectionFixtures.Select(SelectMode.Face, center);
        SelectionOps.Grow(doc, sel);
        Assert.Equal(5, sel.Count);
        SelectionOps.Grow(doc, sel);
        Assert.Equal(9, sel.Count);
        SelectionOps.Shrink(doc, sel);
        Assert.Equal([center], sel.Items);
        Assert.Equal(center, sel.Active);
        SelectionOps.Shrink(doc, sel);
        Assert.True(sel.IsEmpty);
        Assert.Null(sel.Active);
    }

    [Fact]
    public void Box_shrink_peels_only_against_unselected_faces()
    {
        var box = SelectionFixtures.Box();
        var doc = SelectionFixtures.Doc(box);
        int top = Enumerable.Range(0, 6).Single(f => box.FaceNormal(f).Y > 0.9f);
        var sel = SelectionFixtures.Select(SelectMode.Face, ElementRef.Face(box.Id, top));
        SelectionOps.Grow(doc, sel);
        Assert.Equal(5, sel.Count);
        SelectionOps.Shrink(doc, sel);
        Assert.Equal([ElementRef.Face(box.Id, top)], sel.Items);
        sel.SelectAll(doc);
        SelectionOps.Shrink(doc, sel);
        Assert.Equal(6, sel.Count); // closed and fully selected: no rim
    }

    [Fact]
    public void Vertices_grow_along_edges_and_shrink_off_the_border()
    {
        var plane = SelectionFixtures.Plane();
        var doc = SelectionFixtures.Doc(plane);
        var sel = SelectionFixtures.Select(SelectMode.Vertex, ElementRef.Vertex(plane.Id, V(1, 1)));
        SelectionOps.Grow(doc, sel);
        Assert.Equal(5, sel.Count);
        sel.SelectAll(doc);
        SelectionOps.Shrink(doc, sel);
        Assert.Equal(new[] { V(1, 1), V(2, 1), V(1, 2), V(2, 2) }.Order(), sel.Items.Select(i => i.A).Order());
    }

    [Fact]
    public void Edges_grow_by_shared_vertices_and_shrink_to_the_interior()
    {
        var plane = SelectionFixtures.Plane();
        var doc = SelectionFixtures.Doc(plane);
        var sel = SelectionFixtures.Select(SelectMode.Edge, ElementRef.EdgeOf(plane.Id, new Edge(V(1, 1), V(2, 1))));
        SelectionOps.Grow(doc, sel);
        Assert.Equal(7, sel.Count); // itself + 3 more at each interior end
        sel.SelectAll(doc);
        Assert.Equal(24, sel.Count);
        SelectionOps.Shrink(doc, sel);
        Assert.Equal(4, sel.Count); // the inner square
    }
}

public class SelectionOpsFloodPathTests
{
    static int V(int i, int j) => SelectionFixtures.V(i, j);
    static int F(int i, int j) => SelectionFixtures.F(i, j);

    /// <summary>The 3×3 plane with its right column hinged up by <paramref name="degrees"/> about x = 2.</summary>
    static PolyMesh Folded(float degrees)
    {
        var m = SelectionFixtures.Plane();
        float rise = MathF.Tan(degrees * MathF.PI / 180f);
        for (int j = 0; j <= 3; j++) m.Positions[V(3, j)] += new Vector3(0, rise, 0);
        return m;
    }

    [Fact]
    public void Coplanar_flood_stops_at_the_fold()
    {
        var m = Folded(30);
        var doc = SelectionFixtures.Doc(m);
        var faces = SelectionOps.Flood(doc, ElementRef.Face(m.Id, F(0, 0)), SelectMode.Face, coplanar: true);
        Assert.Equal(6, faces.Count);
        Assert.DoesNotContain(faces, f => f.A % 3 == 2);
        Assert.Equal(9, SelectionOps.Flood(doc, ElementRef.Face(m.Id, F(0, 0)), SelectMode.Face).Count);
    }

    [Fact]
    public void Coplanar_tolerance_is_about_one_degree()
    {
        var gentle = Folded(0.5f);
        Assert.Equal(9, new SelectionTopology(gentle).CoplanarFaces(F(0, 0)).Count);
        var steep = Folded(2f);
        Assert.Equal(6, new SelectionTopology(steep).CoplanarFaces(F(0, 0)).Count);
    }

    [Fact]
    public void Box_coplanar_flood_is_one_face_and_connected_flood_is_all_six()
    {
        var box = SelectionFixtures.Box();
        var doc = SelectionFixtures.Doc(box, SelectionFixtures.Plane());
        Assert.Single(SelectionOps.Flood(doc, ElementRef.Face(box.Id, 0), SelectMode.Face, coplanar: true));
        var all = SelectionOps.Flood(doc, ElementRef.Face(box.Id, 0), SelectMode.Face);
        Assert.Equal(6, all.Count);
        Assert.All(all, f => Assert.Equal(box.Id, f.Mesh));
    }

    [Fact]
    public void Flood_on_edges_is_the_loop_and_on_vertices_the_connected_part()
    {
        var plane = SelectionFixtures.Plane();
        var box = SelectionFixtures.Box();
        var doc = SelectionFixtures.Doc(plane, box);
        Assert.Equal(3, SelectionOps.Flood(doc, ElementRef.EdgeOf(plane.Id, new Edge(V(1, 1), V(1, 2))), SelectMode.Edge).Count);
        Assert.Equal(16, SelectionOps.Flood(doc, ElementRef.Vertex(plane.Id, 5), SelectMode.Vertex).Count);
        Assert.Equal(8, SelectionOps.Flood(doc, ElementRef.Vertex(box.Id, 0), SelectMode.Vertex).Count);
    }

    [Fact]
    public void Vertex_path_corner_to_corner_is_six_edges_long()
    {
        var topo = new SelectionTopology(SelectionFixtures.Plane());
        var path = topo.VertexPath([V(0, 0)], [V(3, 3)])!;
        Assert.Equal(7, path.Count);
        Assert.Equal(V(0, 0), path[0]);
        Assert.Equal(V(3, 3), path[^1]);
        for (int i = 1; i < path.Count; i++) Assert.Contains(path[i], topo.Neighbors(path[i - 1]));
    }

    [Fact]
    public void Box_vertex_path_takes_three_edges_between_opposite_corners()
    {
        var path = new SelectionTopology(SelectionFixtures.Box()).VertexPath([0], [7])!;
        Assert.Equal(4, path.Count);
    }

    [Fact]
    public void Face_path_goes_by_adjacency()
    {
        var topo = new SelectionTopology(SelectionFixtures.Plane());
        var path = topo.FacePath(F(0, 0), F(2, 2))!;
        Assert.Equal(5, path.Count);
        for (int i = 1; i < path.Count; i++) Assert.Contains(path[i], topo.FaceNeighbors(path[i - 1]));
    }

    [Fact]
    public void Edge_path_includes_both_ends()
    {
        var topo = new SelectionTopology(SelectionFixtures.Plane());
        var from = new Edge(V(0, 0), V(1, 0));
        var to = new Edge(V(3, 2), V(3, 3));
        var path = topo.EdgePath(from, to)!;
        Assert.Equal(from, path[0]);
        Assert.Equal(to, path[^1]);
        Assert.Equal(6, path.Count); // (1,0) to (3,2) is four edges, plus both ends
    }

    [Fact]
    public void Path_select_chains_from_the_active_element()
    {
        var plane = SelectionFixtures.Plane();
        var other = SelectionFixtures.Box();
        var doc = SelectionFixtures.Doc(plane, other);
        var sel = SelectionFixtures.Select(SelectMode.Face, ElementRef.Face(plane.Id, F(0, 0)));
        Assert.True(SelectionOps.SelectPath(doc, sel, ElementRef.Face(plane.Id, F(2, 0))));
        Assert.Equal(3, sel.Count);
        Assert.Equal(ElementRef.Face(plane.Id, F(2, 0)), sel.Active);
        Assert.True(SelectionOps.SelectPath(doc, sel, ElementRef.Face(plane.Id, F(2, 2))));
        Assert.Equal(5, sel.Count);
        // Another mesh: no path, the target is just added and becomes active.
        Assert.False(SelectionOps.SelectPath(doc, sel, ElementRef.Face(other.Id, 0)));
        Assert.Equal(6, sel.Count);
        Assert.Equal(ElementRef.Face(other.Id, 0), sel.Active);
    }

    [Fact]
    public void Disconnected_parts_have_no_path()
    {
        var m = SelectionFixtures.Plane(1);
        var far = SelectionFixtures.Plane(1);
        int offset = m.Positions.Count;
        m.Positions.AddRange(far.Positions.Select(p => p + new Vector3(5, 0, 0)));
        m.Faces.Add(new Face(far.Faces[0].Verts.Select(v => v + offset).ToArray(), "x", default));
        var topo = new SelectionTopology(m);
        Assert.Null(topo.FacePath(0, 1));
        Assert.Null(topo.VertexPath([0], [offset]));
        Assert.Single(topo.ConnectedFaces(0));
    }
}

public class SelectionOpsMiscTests
{
    [Fact]
    public void Select_by_material_switches_to_faces_and_skips_hidden_meshes()
    {
        var plane = SelectionFixtures.Plane();
        var box = SelectionFixtures.Box();
        var hidden = SelectionFixtures.Box();
        hidden.Hidden = true;
        plane.Faces[4].Material = "red";
        box.Faces[2].Material = "red";
        hidden.Faces[0].Material = "red";
        var doc = SelectionFixtures.Doc(plane, box, hidden);
        var sel = SelectionFixtures.Select(SelectMode.Face, ElementRef.Face(plane.Id, 4));
        Assert.Equal("red", SelectionOps.ActiveFaceMaterial(doc, sel));
        Assert.Equal(2, SelectionOps.SelectByMaterial(doc, sel, "red"));
        Assert.Equal(ElementRef.Face(plane.Id, 4), sel.Active);

        var objects = SelectionFixtures.Select(SelectMode.Object, ElementRef.Object(box.Id));
        Assert.Null(SelectionOps.ActiveFaceMaterial(doc, objects));
        Assert.Equal(13, SelectionOps.SelectByMaterial(doc, objects, SelectionFixtures.Mat));
        Assert.Equal(SelectMode.Face, objects.Mode);
        Assert.Null(objects.Active);
    }

    [Fact]
    public void Select_connected_extends_within_each_mesh_only()
    {
        var plane = SelectionFixtures.Plane();
        var box = SelectionFixtures.Box();
        var doc = SelectionFixtures.Doc(plane, box);
        var sel = SelectionFixtures.Select(SelectMode.Edge, ElementRef.EdgeOf(box.Id, new Edge(0, 2)));
        SelectionOps.SelectConnected(doc, sel);
        Assert.Equal(12, sel.Count);
        Assert.Equal(ElementRef.EdgeOf(box.Id, new Edge(0, 2)), sel.Active);
        var faces = SelectionFixtures.Select(SelectMode.Face, ElementRef.Face(plane.Id, 0));
        SelectionOps.SelectConnected(doc, faces);
        Assert.Equal(9, faces.Count);
    }

    [Fact]
    public void Selection_sets_restore_mode_items_and_active_and_drop_what_was_deleted()
    {
        var plane = SelectionFixtures.Plane();
        var doc = SelectionFixtures.Doc(plane);
        var sel = SelectionFixtures.Select(SelectMode.Face, ElementRef.Face(plane.Id, 7), ElementRef.Face(plane.Id, 8));
        var set = SelectionSet.Capture(sel, "Corner");
        sel.ConvertTo(SelectMode.Vertex, doc);
        sel.Clear();
        Assert.Equal(2, set.RestoreInto(sel, doc));
        Assert.Equal(SelectMode.Face, sel.Mode);
        Assert.Equal(ElementRef.Face(plane.Id, 8), sel.Active);

        MeshOps.DeleteFaces(plane, [8]);
        Assert.Equal(1, set.RestoreInto(sel, doc));
        Assert.Null(sel.Active);
    }

    [Fact]
    public void Visible_only_filter_drops_elements_behind_other_geometry()
    {
        var front = MeshOps.CreateBox(new Vector3(-1, -1, -1), new Vector3(1, 1, 1), "a", TextureMapping.Default);
        var back = MeshOps.CreateBox(new Vector3(-0.5f, -0.5f, -6), new Vector3(0.5f, 0.5f, -5), "a", TextureMapping.Default);
        var doc = SelectionFixtures.Doc(front, back);
        var eye = new Vector3(0, 0, 10);
        Assert.False(Visibility.IsVisible(doc, eye, back.Positions[7]));
        Assert.True(Visibility.IsVisible(doc, eye, front.Positions[7]));  // (1,1,1): front corner
        Assert.False(Visibility.IsVisible(doc, eye, front.Positions[0])); // (-1,-1,-1): behind its own front face

        var view = new ViewInfo(Matrix4x4.CreateLookAt(eye, Vector3.Zero, Vector3.UnitY) *
            Matrix4x4.CreatePerspectiveFieldOfView(1, 1, 0.1f, 100), new Vector2(512, 512), eye);
        var all = Picker.Marquee(doc, view, Vector2.Zero, new Vector2(512, 512), SelectMode.Vertex);
        Assert.Equal(16, all.Count);
        var seen = Visibility.FilterVisible(doc, view, all, SelectMode.Vertex);
        Assert.Equal(4, seen.Count); // only the front box's near face corners
        Assert.All(seen, e => Assert.Equal(front.Id, e.Mesh));

        front.Hidden = true; // hidden meshes don't occlude
        Assert.True(Visibility.IsVisible(doc, eye, back.Positions[7]));
    }
}
