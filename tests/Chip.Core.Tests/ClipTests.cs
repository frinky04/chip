using System.Numerics;
using Chip.Core.Geometry;
using Chip.Core.Texturing;

namespace Chip.Core.Tests;

public class ClipTests
{
    internal static PolyMesh Box(Vector3 min, Vector3 max, string material = "dev_a") => MeshOps.CreateBox(min, max, material, TextureMapping.Default);

    internal static bool IsClosedManifold(PolyMesh m) =>
        m.Faces.SelectMany(f => f.Edges()).GroupBy(e => e).All(g => g.Count() == 2);

    /// <summary>Every directed edge appears once and its reverse once: closed and consistently wound.</summary>
    internal static bool IsConsistentlyWound(PolyMesh m)
    {
        var directed = new HashSet<(int, int)>();
        foreach (var f in m.Faces)
            for (int i = 0; i < f.Verts.Length; i++)
                if (!directed.Add((f.Verts[i], f.Verts[(i + 1) % f.Verts.Length]))) return false;
        return directed.All(d => directed.Contains((d.Item2, d.Item1)));
    }

    [Fact]
    public void Diagonal_clip_gives_two_closed_meshes_whose_volumes_sum_to_the_original()
    {
        var box = Box(Vector3.Zero, new Vector3(2, 2, 2));
        var (front, back) = MeshOps.Split(box, new Vector3(1, 1, 1), new Vector3(1, 0.6f, 0.3f));
        Assert.NotNull(front);
        Assert.NotNull(back);
        foreach (var part in new[] { front!, back! })
        {
            Assert.True(IsClosedManifold(part));
            Assert.True(IsConsistentlyWound(part));
            Assert.True(MeshOps.Volume(part) > 0);
        }
        Assert.Equal(8f, MeshOps.Volume(front!) + MeshOps.Volume(back!), 3);
        Assert.Equal(4f, MeshOps.Volume(front!), 3); // plane through the centre halves a box
    }

    [Fact]
    public void Keeping_the_front_leaves_one_closed_mesh_on_the_front_side()
    {
        var box = Box(Vector3.Zero, new Vector3(4, 2, 2));
        var n = Vector3.Normalize(new Vector3(1, 1, 0));
        var (front, _) = MeshOps.Split(box, new Vector3(3, 1, 1), n);
        Assert.NotNull(front);
        Assert.True(IsClosedManifold(front!));
        Assert.All(front!.Positions, p => Assert.True(Vector3.Dot(p - new Vector3(3, 1, 1), n) > -1e-4f));
        Assert.Equal(box.Id, front.Id);
    }

    [Fact]
    public void A_plane_through_existing_vertices_splits_cleanly()
    {
        var box = Box(Vector3.Zero, Vector3.One);
        var (front, back) = MeshOps.Split(box, Vector3.Zero, new Vector3(1, 0, -1));
        Assert.True(IsClosedManifold(front!) && IsClosedManifold(back!));
        Assert.Equal(0.5f, MeshOps.Volume(front!), 4);
        Assert.Equal(0.5f, MeshOps.Volume(back!), 4);
        Assert.Equal(5, front!.Faces.Count); // triangular prism: 2 triangles + 3 quads (one is the cap)
    }

    [Fact]
    public void A_plane_that_misses_returns_the_whole_mesh_on_one_side()
    {
        var box = Box(Vector3.Zero, Vector3.One);
        var (front, back) = MeshOps.Split(box, new Vector3(0, 5, 0), Vector3.UnitY);
        Assert.Null(front);
        Assert.Equal(6, back!.Faces.Count);
    }

    [Fact]
    public void Caps_are_world_aligned_and_use_the_cap_material_or_the_cut_face()
    {
        var box = Box(Vector3.Zero, new Vector3(2, 2, 2));
        var (front, back) = MeshOps.Split(box, new Vector3(0, 1, 0), Vector3.UnitY, capMaterial: "cap");
        int cap = Enumerable.Range(0, front!.Faces.Count).Single(f => front.Faces[f].Material == "cap");
        Assert.True(front.FaceNormal(cap).Y < -0.99f);
        var aligned = TextureMapping.AlignToGrid(front.FaceNormal(cap), TextureMapping.Default);
        Assert.Equal(aligned.UAxis, front.Faces[cap].Map.UAxis);
        Assert.Equal(aligned.VAxis, front.Faces[cap].Map.VAxis);
        Assert.Equal(6, front.Faces.Count);

        var (_, plain) = MeshOps.Split(box, new Vector3(0, 1, 0), Vector3.UnitY);
        Assert.All(plain!.Faces, f => Assert.Equal("dev_a", f.Material));
        Assert.Equal(6, back!.Faces.Count);
    }

    [Fact]
    public void Clipping_a_tube_bridges_the_hole_in_the_cap()
    {
        var tube = Tube(4, 2, 2);
        Assert.True(IsClosedManifold(tube));
        Assert.Equal(24f, MeshOps.Volume(tube), 3);
        var (front, back) = MeshOps.Split(tube, new Vector3(0, 1, 0), Vector3.UnitY);
        foreach (var part in new[] { front!, back! })
        {
            Assert.True(IsConsistentlyWound(part));
            Assert.Equal(12f, MeshOps.Volume(part), 3);
        }
    }

    [Fact]
    public void Explicit_uvs_are_interpolated_on_cut_corners()
    {
        var box = Box(Vector3.Zero, new Vector3(2, 2, 2));
        foreach (var f in box.Faces) f.UVs = f.Verts.Select(v => f.Map.Project(box.Positions[v], 512, 512) + new Vector2(0.25f, 0)).ToArray();
        var (front, _) = MeshOps.Split(box, new Vector3(1, 1, 1), new Vector3(1, 0.5f, 0.25f));
        foreach (var f in front!.Faces.Where(f => f.UVs != null))
            for (int i = 0; i < f.Verts.Length; i++)
            {
                var expected = f.Map.Project(front.Positions[f.Verts[i]], 512, 512) + new Vector2(0.25f, 0);
                Assert.True(Vector2.Distance(expected, f.UVs![i]) < 1e-4f);
            }
        Assert.Contains(front.Faces, f => f.UVs == null); // the cap is projected
    }

    [Fact]
    public void Clip_is_one_undoable_step()
    {
        var doc = new Chip.Core.Scene.Document();
        var box = doc.Add(Box(Vector3.Zero, Vector3.One));
        var s = new Chip.Core.Scene.Session();
        s.Reset(doc, null);
        s.Do("Clip", () =>
        {
            var m = s.Doc.Find(box.Id)!;
            var (front, back) = MeshOps.Split(m, new Vector3(0.5f), Vector3.UnitX);
            m.Positions = front!.Positions; m.Faces = front.Faces; m.SoftEdges = front.SoftEdges;
            s.Doc.Add(back!);
        });
        Assert.Equal(2, s.Doc.Meshes.Count);
        s.Undo();
        Assert.Single(s.Doc.Meshes);
        Assert.Equal(6, s.Doc.Meshes[0].Faces.Count);
    }

    /// <summary>A square ring: outer side <paramref name="outer"/>, square hole <paramref name="inner"/>, height <paramref name="h"/>.</summary>
    internal static PolyMesh Tube(float outer, float inner, float h)
    {
        var m = new PolyMesh();
        float o = outer / 2, i = inner / 2;
        Vector2[] ring = [new(-o, -o), new(o, -o), new(o, o), new(-o, o)];
        Vector2[] hole = [new(-i, -i), new(i, -i), new(i, i), new(-i, i)];
        // 0-3 outer bottom, 4-7 outer top, 8-11 inner bottom, 12-15 inner top (x, z in the plane)
        foreach (var y in new[] { 0f, h }) foreach (var p in ring) m.Positions.Add(new Vector3(p.X, y, p.Y));
        foreach (var y in new[] { 0f, h }) foreach (var p in hole) m.Positions.Add(new Vector3(p.X, y, p.Y));
        for (int k = 0; k < 4; k++)
        {
            int k1 = (k + 1) % 4;
            m.Faces.Add(new Face([k, k + 4, k1 + 4, k1], "dev_a", default));             // outer wall
            m.Faces.Add(new Face([8 + k, 8 + k1, 12 + k1, 12 + k], "dev_a", default));   // inner wall
            m.Faces.Add(new Face([4 + k, 12 + k, 12 + k1, 4 + k1], "dev_a", default));   // top
            m.Faces.Add(new Face([k, k1, 8 + k1, 8 + k], "dev_a", default));             // bottom
        }
        // Orient outward: flip everything if the volume came out negative.
        if (MeshOps.Volume(m) < 0) MeshOps.FlipFaces(m, Enumerable.Range(0, m.Faces.Count));
        MeshOps.AlignToGrid(m, Enumerable.Range(0, m.Faces.Count), TextureMapping.Default);
        return m;
    }
}
