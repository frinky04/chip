using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using Chip.Core.Geometry;
using Chip.Core.Geometry.Primitives;
using Chip.Core.IO;
using Chip.Core.Scene;
using Chip.Core.Texturing;

namespace Chip.Core.Tests;

/// <summary>Stream L: soft / hard / dominant normals, auto-smooth, corner-normal continuity, persistence and export.</summary>
public class NormalsTests
{
    const string Mat = "dev_measurewall01a";
    const float Eps = 1e-4f;

    static PolyMesh Box() => MeshOps.CreateBox(Vector3.Zero, new Vector3(2, 2, 2), Mat, TextureMapping.Default);

    static PolyMesh Cylinder(int sides = 16, bool smooth = false) => Primitive.Build(PrimitiveShape.Cylinder,
        new PrimitiveOptions { Sides = sides, Smooth = smooth },
        PrimitiveFrame.FromBlock(new(0, 0, 0), new(4, 0, 4), Vector3.UnitY, 3), Mat, TextureMapping.Default);

    static PolyMesh Sphere() => Primitive.Build(PrimitiveShape.Sphere, new PrimitiveOptions { Smooth = false },
        PrimitiveFrame.FromBlock(new(0, 0, 0), new(4, 0, 4), Vector3.UnitY, 4), Mat, TextureMapping.Default);

    static int FaceFacing(PolyMesh m, Vector3 dir) => Enumerable.Range(0, m.Faces.Count).First(f => Vector3.Dot(m.FaceNormal(f), dir) > 0.99f);

    static bool Near(Vector3 a, Vector3 b) => Vector3.Distance(a, b) < Eps;

    /// <summary>Every distinct corner normal at each vertex (1 = smooth there, more = split).</summary>
    static Dictionary<int, List<Vector3>> NormalsPerVertex(PolyMesh m)
    {
        var normals = Topology.CornerNormals(m);
        var map = new Dictionary<int, List<Vector3>>();
        for (int f = 0; f < m.Faces.Count; f++)
            for (int i = 0; i < m.Faces[f].Verts.Length; i++)
            {
                int v = m.Faces[f].Verts[i];
                if (!map.TryGetValue(v, out var list)) map[v] = list = new List<Vector3>();
                if (!list.Any(n => Near(n, normals[f][i]))) list.Add(normals[f][i]);
            }
        return map;
    }

    // ---- Soft / hard ----

    [Fact]
    public void Soft_box_shares_one_normal_per_corner_and_hard_restores_flat()
    {
        var m = Box();
        Assert.Equal(12, MeshOps.SetEdgeSmoothing(m, m.Edges(), soft: true));
        foreach (var (v, list) in NormalsPerVertex(m))
        {
            Assert.Single(list);
            var outward = Vector3.Normalize(m.Positions[v] - new Vector3(1));
            Assert.True(Near(list[0], outward)); // a cube corner averages to the diagonal
        }
        Assert.Equal(12, MeshOps.SetEdgeSmoothing(m, m.Edges(), soft: false));
        Assert.Empty(m.SoftEdges);
        var normals = Topology.CornerNormals(m);
        for (int f = 0; f < m.Faces.Count; f++) Assert.All(normals[f], n => Assert.True(Near(n, m.FaceNormal(f))));
    }

    [Fact]
    public void Open_edges_cannot_be_soft_and_repeating_changes_nothing()
    {
        var quad = Primitive.Rectangle(Vector3.Zero, new Vector3(1, 0, 1), Vector3.UnitY, Mat, TextureMapping.Default)!;
        Assert.Equal(0, MeshOps.SetEdgeSmoothing(quad, quad.Edges(), soft: true));
        Assert.Empty(quad.SoftEdges);

        var m = Box();
        var top = MeshOps.EdgesOfFaces(m, [FaceFacing(m, Vector3.UnitY)]);
        Assert.Equal(4, top.Count);
        Assert.Equal(4, MeshOps.SetEdgeSmoothing(m, top, soft: true));
        Assert.Equal(0, MeshOps.SetEdgeSmoothing(m, top, soft: true));
        Assert.Equal(0, MeshOps.SetEdgeSmoothing(m, [new Edge(0, 99)], soft: true)); // not in the mesh
    }

    [Fact]
    public void A_hard_seam_creases_an_otherwise_soft_cylinder()
    {
        var m = Cylinder(smooth: true);
        Assert.Equal(16, m.SoftEdges.Count);
        // Smooth sides: one normal per side vertex among side faces, and it is horizontal (radial).
        var normals = Topology.CornerNormals(m);
        var sides = Enumerable.Range(0, m.Faces.Count).Where(f => MathF.Abs(m.FaceNormal(f).Y) < 0.5f).ToList();
        foreach (var f in sides)
            Assert.All(normals[f], n => Assert.True(MathF.Abs(n.Y) < Eps));

        var seam = m.SoftEdges.First();
        MeshOps.SetEdgeSmoothing(m, [seam], soft: false);
        normals = Topology.CornerNormals(m);
        var touching = sides.Where(f => m.Faces[f].Edges().Contains(seam)).ToList();
        Assert.Equal(2, touching.Count);
        int a = touching[0], b = touching[1];
        var na = normals[a][Array.IndexOf(m.Faces[a].Verts, seam.A)];
        var nb = normals[b][Array.IndexOf(m.Faces[b].Verts, seam.A)];
        Assert.False(Near(na, nb)); // creased, even though the faces are still joined by soft edges the long way round
        Assert.True(Near(na, m.FaceNormal(a)));
        // The far side of each face stays smooth with its other neighbour.
        int far = Array.FindIndex(m.Faces[a].Verts, v => v != seam.A && v != seam.B);
        Assert.False(Near(normals[a][far], m.FaceNormal(a)));
        Assert.True(MathF.Abs(normals[a][far].Y) < Eps);
    }

    // ---- Dominant ----

    [Fact]
    public void Dominant_top_face_bends_the_sides_at_shared_corners_and_stays_flat()
    {
        var m = Box();
        int top = FaceFacing(m, Vector3.UnitY);
        Assert.Equal(1, MeshOps.SetDominantFaces(m, [top]));
        Assert.True(m.Faces[top].Dominant);
        Assert.Empty(m.SoftEdges); // right-angle walls stay separate surfaces
        MeshOps.SetEdgeSmoothing(m, MeshOps.EdgesOfFaces(m, [top]), soft: true); // ...unless softened by hand
        var normals = Topology.CornerNormals(m);
        Assert.All(normals[top], n => Assert.True(Near(n, Vector3.UnitY)));
        int front = FaceFacing(m, Vector3.UnitZ);
        for (int i = 0; i < 4; i++)
        {
            var p = m.Positions[m.Faces[front].Verts[i]];
            Assert.True(Near(normals[front][i], p.Y > 1 ? Vector3.UnitY : Vector3.UnitZ)); // top corners take the top's normal
        }
        Assert.Equal(0, MeshOps.SetDominantFaces(m, [top]));
        Assert.Equal(1, MeshOps.ClearDominantFaces(m, [top]));
        Assert.False(m.Faces[top].Dominant);
    }

    [Fact]
    public void Dominant_survives_ops_that_split_or_copy_a_face()
    {
        var m = Box();
        int top = FaceFacing(m, Vector3.UnitY);
        MeshOps.SetDominantFaces(m, [top]);
        var center = m.FaceCenter(top);

        // Clip through the top: both halves keep the flag, the new cap faces don't get it.
        var (front, back) = MeshOps.Split(m, center, Vector3.UnitX);
        foreach (var half in new[] { front!, back! })
        {
            Assert.Single(half.Faces, f => f.Dominant);
            Assert.True(half.FaceNormal(half.Faces.FindIndex(f => f.Dominant)).Y > 0.99f);
        }

        Assert.True(MeshOps.CopyFaces(m, [top]).Faces.Single().Dominant);
        var dup = MeshOps.DuplicateFaces(m, [top]);
        Assert.True(m.Faces[dup.Single()].Dominant);
    }

    [Fact]
    public void Dominant_faces_either_side_of_a_chamfer_shade_it_like_a_round()
    {
        var m = Box();
        var edge = new Edge(m.Positions.FindIndex(p => p == new Vector3(0, 2, 2)), m.Positions.FindIndex(p => p == new Vector3(2, 2, 2)));
        var strip = Assert.Single(MeshOps.BevelEdges(m, [edge], 0.25f, 1));
        int top = FaceFacing(m, Vector3.UnitY), front = FaceFacing(m, Vector3.UnitZ);
        MeshOps.SetDominantFaces(m, [top, front]);
        Assert.Equal(2, m.SoftEdges.Count); // the 45° edges to the chamfer, not the 90° edges to the end faces
        var normals = Topology.CornerNormals(m);
        var verts = m.Faces[strip].Verts;
        for (int i = 0; i < verts.Length; i++)
        {
            var p = m.Positions[verts[i]];
            Assert.True(Near(normals[strip][i], p.Y > 1.99f ? Vector3.UnitY : Vector3.UnitZ));
        }
        Assert.All(normals[top], n => Assert.True(Near(n, Vector3.UnitY)));
        Assert.All(normals[front], n => Assert.True(Near(n, Vector3.UnitZ)));
    }

    // ---- Auto-smooth ----

    [Theory]
    [InlineData(16, 30f, 16)] // 22.5° between sides: smooth; 90° caps: hard
    [InlineData(16, 20f, 0)]
    [InlineData(8, 44f, 0)]   // 45° between sides
    [InlineData(8, 46f, 8)]
    [InlineData(16, 91f, 48)] // everything, caps included
    public void Auto_smooth_softens_below_the_angle_only(int sides, float angle, int expected)
    {
        var m = Cylinder(sides);
        Assert.Equal(expected, MeshOps.AutoSmooth(m, angle));
        Assert.Equal(expected, m.SoftEdges.Count);
    }

    [Fact]
    public void Auto_smooth_hardens_sharp_edges_and_respects_its_scope()
    {
        var m = Box();
        foreach (var e in m.Edges()) m.SoftEdges.Add(e);
        var top = MeshOps.EdgesOfFaces(m, [FaceFacing(m, Vector3.UnitY)]);
        Assert.Equal(0, MeshOps.AutoSmooth(m, 45, top));
        Assert.Equal(8, m.SoftEdges.Count); // only the top's four edges changed
        Assert.Equal(0, MeshOps.AutoSmooth(m, 45));
        Assert.Empty(m.SoftEdges);
        Assert.Equal(12, MeshOps.AutoSmooth(m, 90.5f));
    }

    [Fact]
    public void Auto_smoothed_sphere_has_continuous_radial_normals()
    {
        var m = Sphere();
        MeshOps.AutoSmooth(m, MeshOps.DefaultSmoothAngle);
        Assert.Equal(m.Edges().Count(), m.SoftEdges.Count);
        var (min, max) = m.Bounds();
        var centre = (min + max) * 0.5f;
        foreach (var (v, list) in NormalsPerVertex(m))
        {
            Assert.Single(list);
            Assert.True(Vector3.Dot(list[0], Vector3.Normalize(m.Positions[v] - centre)) > 0.95f);
        }
    }

    [Fact]
    public void Edge_angle_is_null_for_open_edges()
    {
        var quad = Primitive.Rectangle(Vector3.Zero, new Vector3(1, 0, 1), Vector3.UnitY, Mat, TextureMapping.Default)!;
        var ef = Topology.EdgeFaces(quad);
        Assert.Null(Topology.EdgeAngle(quad, quad.Edges().First(), ef));
        var box = Box();
        Assert.Equal(90f, Topology.EdgeAngle(box, box.Edges().First(), Topology.EdgeFaces(box))!.Value, 3);
    }

    [Fact]
    public void Clear_smoothing_flattens_everything()
    {
        var m = Box();
        MeshOps.SetDominantFaces(m, [0, 1]);
        MeshOps.SetEdgeSmoothing(m, m.Edges(), soft: true);
        MeshOps.ClearSmoothing(m);
        Assert.Empty(m.SoftEdges);
        Assert.DoesNotContain(m.Faces, f => f.Dominant);
    }

    // ---- Ops keep smoothing valid ----

    [Fact]
    public void Rounded_bevel_shades_smoothly_across_its_bands()
    {
        var m = Box();
        var edge = new Edge(m.Positions.FindIndex(p => p == new Vector3(0, 2, 2)), m.Positions.FindIndex(p => p == new Vector3(2, 2, 2)));
        var bands = MeshOps.BevelEdges(m, [edge], 0.5f, 3);
        var normals = Topology.CornerNormals(m);
        // Between consecutive bands the shared corners carry one normal.
        foreach (var e in m.SoftEdges)
        {
            var faces = bands.Where(f => m.Faces[f].Edges().Contains(e)).ToList();
            Assert.Equal(2, faces.Count);
            var n0 = normals[faces[0]][Array.IndexOf(m.Faces[faces[0]].Verts, e.A)];
            var n1 = normals[faces[1]][Array.IndexOf(m.Faces[faces[1]].Verts, e.A)];
            Assert.True(Near(n0, n1));
        }
    }

    [Fact]
    public void Clip_through_a_smooth_cylinder_keeps_the_sides_soft_and_the_cap_hard()
    {
        var m = Cylinder(smooth: true);
        var (front, back) = MeshOps.Split(m, new Vector3(2, 1.5f, 2), Vector3.UnitY);
        foreach (var half in new[] { front!, back! })
        {
            Assert.Equal(16, half.SoftEdges.Count); // each vertical seam keeps its half
            var live = half.Edges().ToHashSet();
            Assert.All(half.SoftEdges, e => Assert.Contains(e, live));
            var normals = Topology.CornerNormals(half);
            for (int f = 0; f < half.Faces.Count; f++)
                if (MathF.Abs(half.FaceNormal(f).Y) > 0.99f) Assert.All(normals[f], n => Assert.True(MathF.Abs(n.Y) > 0.99f));
        }
    }

    [Fact]
    public void Inset_on_a_smooth_cylinder_keeps_the_soft_edge_between_inset_faces()
    {
        var m = Cylinder(smooth: true);
        var sides = Enumerable.Range(0, m.Faces.Count).Where(f => MathF.Abs(m.FaceNormal(f).Y) < 0.5f).ToList();
        int a = sides[0];
        int b = sides.First(f => f != a && m.Faces[f].Edges().Intersect(m.Faces[a].Edges()).Any());
        var shared = m.Faces[a].Edges().Intersect(m.Faces[b].Edges()).Single();
        Assert.Contains(shared, m.SoftEdges);
        MeshOps.Inset(m, [a, b]).Apply(m, 0.1f, outset: false);
        var inner = m.Faces[a].Edges().Intersect(m.Faces[b].Edges()).Single();
        Assert.Contains(inner, m.SoftEdges);
        Assert.All(m.SoftEdges, e => Assert.Contains(e, m.Edges()));
    }

    // ---- Persistence and undo ----

    [Fact]
    public void Dominant_flag_round_trips_and_is_omitted_when_unset()
    {
        var doc = new Document();
        var m = doc.Add(Box());
        Assert.DoesNotContain("dominant", DocumentSerializer.Serialize(doc));
        MeshOps.SetDominantFaces(m, [2]);
        var json = DocumentSerializer.Serialize(doc);
        Assert.Contains("\"dominant\": true", json);
        var back = DocumentSerializer.Deserialize(json).Meshes[0];
        Assert.True(back.Faces[2].Dominant);
        Assert.Equal(1, back.Faces.Count(f => f.Dominant));
        Assert.Equal(m.SoftEdges.OrderBy(e => e.A).ThenBy(e => e.B), back.SoftEdges.OrderBy(e => e.A).ThenBy(e => e.B));
        Assert.True(m.Clone().Faces[2].Dominant);
    }

    [Fact]
    public void Smoothing_edits_undo_in_one_step()
    {
        var doc = new Document();
        var box = doc.Add(Box());
        var s = new Session();
        s.Reset(doc, null);
        s.Do("Dominant Normals", () => MeshOps.SetDominantFaces(s.Doc.Find(box.Id)!, [0]));
        Assert.True(s.Doc.Find(box.Id)!.Faces[0].Dominant);
        s.Undo();
        Assert.False(s.Doc.Find(box.Id)!.Faces[0].Dominant);
        Assert.Empty(s.Doc.Find(box.Id)!.SoftEdges);
        s.Redo();
        Assert.True(s.Doc.Find(box.Id)!.Faces[0].Dominant);
    }

    [Fact]
    public void Capture_fixture_loads_with_its_smoothing()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "tests", "fixtures", "normals.chip"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var doc = DocumentSerializer.Load(Path.Combine(dir!.FullName, "tests", "fixtures", "normals.chip"));
        Assert.Contains(doc.Meshes, m => m.SoftEdges.Count > 0);
        Assert.Contains(doc.Meshes, m => m.SoftEdges.Count == 0); // the flat references
        Assert.Equal(2, doc.Meshes.Sum(m => m.Faces.Count(f => f.Dominant)));
    }

    // ---- Export ----

    [Fact]
    public void Auto_smoothed_cylinder_exports_shared_side_normals_and_split_cap_normals()
    {
        var doc = new Document();
        var m = doc.Add(Cylinder());
        MeshOps.AutoSmooth(m, 30);
        var (positions, normals) = ExportedVertices(GlbExporter.Build(doc, new MaterialLibrary()));
        Assert.Equal(m.Faces.Sum(f => f.Verts.Length), positions.Count);
        var byPosition = positions.Select((p, i) => (p, n: normals[i]))
            .GroupBy(x => (MathF.Round(x.p.X, 3), MathF.Round(x.p.Y, 3), MathF.Round(x.p.Z, 3)));
        foreach (var g in byPosition)
        {
            var distinct = new List<Vector3>();
            foreach (var (_, n) in g) if (!distinct.Any(d => Near(d, n))) distinct.Add(n);
            // Every rim vertex: one radial normal shared by its two side corners, one vertical cap normal.
            Assert.Equal(2, distinct.Count);
            Assert.Single(distinct, n => MathF.Abs(n.Y) > 0.999f);
            Assert.Single(distinct, n => MathF.Abs(n.Y) < Eps);
            Assert.Equal(2, g.Count(x => MathF.Abs(x.n.Y) < Eps));
        }
    }

    /// <summary>POSITION and NORMAL of every vertex in the GLB (all primitives).</summary>
    static (List<Vector3> Positions, List<Vector3> Normals) ExportedVertices(byte[] glb)
    {
        int jsonLen = (int)BitConverter.ToUInt32(glb, 12);
        var json = JsonNode.Parse(Encoding.UTF8.GetString(glb, 20, jsonLen))!;
        int binStart = 20 + jsonLen + 8;
        List<Vector3> Read(int accessor)
        {
            var acc = json["accessors"]![accessor]!;
            var view = json["bufferViews"]![acc["bufferView"]!.GetValue<int>()]!;
            int offset = binStart + (view["byteOffset"]?.GetValue<int>() ?? 0) + (acc["byteOffset"]?.GetValue<int>() ?? 0);
            int count = acc["count"]!.GetValue<int>();
            var list = new List<Vector3>(count);
            for (int i = 0; i < count; i++)
                list.Add(new Vector3(BitConverter.ToSingle(glb, offset + i * 12), BitConverter.ToSingle(glb, offset + i * 12 + 4), BitConverter.ToSingle(glb, offset + i * 12 + 8)));
            return list;
        }
        var pos = new List<Vector3>();
        var nrm = new List<Vector3>();
        foreach (var mesh in json["meshes"]!.AsArray())
            foreach (var prim in mesh!["primitives"]!.AsArray())
            {
                pos.AddRange(Read(prim!["attributes"]!["POSITION"]!.GetValue<int>()));
                nrm.AddRange(Read(prim["attributes"]!["NORMAL"]!.GetValue<int>()));
            }
        return (pos, nrm);
    }
}
