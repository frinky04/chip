using System.Numerics;
using Chip.Core.Geometry;
using Chip.Core.Geometry.Primitives;
using Chip.Core.Texturing;

namespace Chip.Core.Tests;

/// <summary>Export tessellation: same surface, no cracks, world-aligned lattice, attributes carried over, cap, determinism.</summary>
public class TessellatorTests
{
    const string Mat = "dev_measurewall01a";

    static PolyMesh Box(Vector3 a, Vector3 b) => MeshOps.CreateBox(a, b, Mat, TextureMapping.Default);

    static TessellatedMesh Run(PolyMesh m, float spacing, int cap = 1024, bool smooth = true) =>
        Tessellator.Tessellate(m, new TessellateOptions(spacing, cap), smooth);

    static PolyMesh Cylinder() => Primitive.Build(PrimitiveShape.Cylinder, new PrimitiveOptions { Sides = 16, Smooth = true },
        PrimitiveFrame.FromBlock(new(0, 0, 0), new(4, 0, 4), Vector3.UnitY, 3), Mat, TextureMapping.Default);

    static PolyMesh LPrism() => Primitive.Prism(
        [new(0, 0, 0), new(3, 0, 0), new(3, 0, 1), new(1, 0, 1), new(1, 0, 3), new(0, 0, 3)],
        new Vector3(0, 1.5f, 0), Mat, TextureMapping.Default)!;

    // ---- checks ----

    /// <summary>Every directed edge is used once and its reverse once: closed, consistently wound, no T-junctions.</summary>
    static void AssertClosed(PolyMesh m)
    {
        var directed = new Dictionary<(int, int), int>();
        foreach (var f in m.Faces)
            for (int i = 0; i < f.Verts.Length; i++)
            {
                var e = (f.Verts[i], f.Verts[(i + 1) % f.Verts.Length]);
                directed[e] = directed.GetValueOrDefault(e) + 1;
            }
        foreach (var ((a, b), count) in directed)
        {
            Assert.Equal(1, count);
            Assert.True(directed.ContainsKey((b, a)), $"edge {a}-{b} has no twin");
        }
    }

    /// <summary>No vertex sits inside an edge it isn't an end of.</summary>
    static void AssertNoTJunctions(PolyMesh m)
    {
        var edges = m.Edges().ToList();
        for (int v = 0; v < m.Positions.Count; v++)
        {
            var p = m.Positions[v];
            foreach (var e in edges)
            {
                if (e.A == v || e.B == v) continue;
                Vector3 a = m.Positions[e.A], b = m.Positions[e.B];
                var ab = b - a;
                float t = Vector3.Dot(p - a, ab) / ab.LengthSquared();
                if (t <= 1e-4f || t >= 1 - 1e-4f) continue;
                Assert.True(Vector3.Distance(a + ab * t, p) > 1e-5f, $"vertex {v} lies on edge {e.A}-{e.B}");
            }
        }
    }

    /// <summary>Largest distance of a corner from the face's plane.</summary>
    static float Warp(PolyMesh m, int f)
    {
        var n = m.FaceNormal(f);
        var p0 = m.Positions[m.Faces[f].Verts[0]];
        return m.Faces[f].Verts.Max(v => MathF.Abs(Vector3.Dot(m.Positions[v] - p0, n)));
    }

    /// <summary>Polygon area relative to its first corner (Topology.FaceArea loses digits far from the origin).</summary>
    static double PreciseArea(PolyMesh m, int f)
    {
        var v = m.Faces[f].Verts;
        var p0 = m.Positions[v[0]];
        double x = 0, y = 0, z = 0;
        for (int i = 1; i + 1 < v.Length; i++)
        {
            Vector3 a = m.Positions[v[i]] - p0, b = m.Positions[v[i + 1]] - p0;
            x += (double)a.Y * b.Z - (double)a.Z * b.Y;
            y += (double)a.Z * b.X - (double)a.X * b.Z;
            z += (double)a.X * b.Y - (double)a.Y * b.X;
        }
        return Math.Sqrt(x * x + y * y + z * z) * 0.5;
    }

    static float Area(PolyMesh m) => Enumerable.Range(0, m.Faces.Count).Sum(f => Topology.FaceArea(m, f));

    static float MaxEdge(PolyMesh m) => m.Edges().Max(e => Vector3.Distance(m.Positions[e.A], m.Positions[e.B]));

    /// <summary>Smallest triangle angle in degrees (needles show up as near zero).</summary>
    static float MinAngle(PolyMesh m) => m.Faces.Min(f =>
    {
        var p = f.Verts.Select(v => m.Positions[v]).ToArray();
        float min = 180;
        for (int i = 0; i < 3; i++)
        {
            var a = Vector3.Normalize(p[(i + 1) % 3] - p[i]);
            var b = Vector3.Normalize(p[(i + 2) % 3] - p[i]);
            min = MathF.Min(min, MathF.Acos(Math.Clamp(Vector3.Dot(a, b), -1, 1)) * 180 / MathF.PI);
        }
        return min;
    });

    static (Vector2 P, Vector2[] Loop) Project(PolyMesh src, int f, Vector3 p)
    {
        var (u, v) = Primitive.BaseAxes(src.FaceNormal(f));
        var loop = src.FacePoints(f).Select(q => new Vector2(Vector3.Dot(q, u), Vector3.Dot(q, v))).ToArray();
        return (new Vector2(Vector3.Dot(p, u), Vector3.Dot(p, v)), loop);
    }

    static bool Inside(Vector2 p, Vector2[] loop)
    {
        bool inside = false;
        for (int i = 0, j = loop.Length - 1; i < loop.Length; j = i++)
            if ((loop[i].Y > p.Y) != (loop[j].Y > p.Y)
                && p.X < (loop[j].X - loop[i].X) * (p.Y - loop[i].Y) / (loop[j].Y - loop[i].Y) + loop[i].X)
                inside = !inside;
        return inside;
    }

    static bool OnBoundary(PolyMesh src, int f, Vector3 p, float eps = 1e-4f)
    {
        var v = src.Faces[f].Verts;
        for (int i = 0; i < v.Length; i++)
        {
            Vector3 a = src.Positions[v[i]], b = src.Positions[v[(i + 1) % v.Length]];
            var ab = b - a;
            float t = Math.Clamp(Vector3.Dot(p - a, ab) / ab.LengthSquared(), 0, 1);
            if (Vector3.Distance(a + ab * t, p) < eps) return true;
        }
        return false;
    }

    /// <summary>Each triangle lies in its source face's plane, inside its polygon, wound the same way; per-face area is kept.</summary>
    static void AssertSameSurface(PolyMesh src, TessellatedMesh t)
    {
        var m = t.Mesh;
        Assert.Equal(m.Faces.Count, t.SourceFaces.Length);
        Assert.Equal(m.Faces.Count, t.CornerNormals.Length);
        var area = new double[src.Faces.Count];
        for (int i = 0; i < m.Faces.Count; i++)
        {
            int f = t.SourceFaces[i];
            var face = m.Faces[i];
            Assert.Equal(3, face.Verts.Length);
            Assert.Equal(src.Faces[f].Material, face.Material);
            Assert.Equal(src.Faces[f].Map, face.Map);
            var n = src.FaceNormal(f);
            var p0 = src.Positions[src.Faces[f].Verts[0]];
            Vector3 a = m.Positions[face.Verts[0]], b = m.Positions[face.Verts[1]], c = m.Positions[face.Verts[2]];
            foreach (var p in (Vector3[])[a, b, c])
            {
                Assert.True(MathF.Abs(Vector3.Dot(p - p0, n)) < 1e-4f, "vertex off the face plane");
                var (pp, loop) = Project(src, f, p);
                Assert.True(Inside(pp, loop) || OnBoundary(src, f, p), "vertex outside its source polygon");
            }
            var cross = Vector3.Cross(b - a, c - a);
            area[f] += cross.Length() * 0.5;
            if (cross.Length() < 1e-7f) continue;
            Assert.True(Vector3.Dot(Vector3.Normalize(cross), n) > 0.999f, "triangle winding differs from its source face");
            var (centroid, poly) = Project(src, f, (a + b + c) / 3);
            Assert.True(Inside(centroid, poly), "triangle outside its source polygon");
        }
        for (int f = 0; f < src.Faces.Count; f++)
        {
            if (!t.SourceFaces.Contains(f)) continue;
            double expected = PreciseArea(src, f);
            Assert.True(Math.Abs(area[f] - expected) < 1e-4 * Math.Max(1, expected),
                $"{src.Name} face {f} ({src.Faces[f].Verts.Length} corners, off-plane {Warp(src, f)}): area {area[f]} != {expected}");
        }
    }

    // ---- tests ----

    [Fact]
    public void Box_IsClosedKeepsAreaAndHasSmallTriangles()
    {
        var box = Box(Vector3.Zero, new Vector3(2, 2, 2));
        var t = Run(box, 0.25f);
        AssertClosed(t.Mesh);
        AssertNoTJunctions(t.Mesh);
        AssertSameSurface(box, t);
        Assert.Equal(24f, Area(t.Mesh), 3);
        Assert.True(MaxEdge(t.Mesh) <= 1.5f * 0.25f, $"max edge {MaxEdge(t.Mesh)}");
        // An 8x8 grid per face: 2 * 49 interior + 32 boundary - 2 triangles.
        Assert.Equal(6 * 128, t.TriangleCount);
        Assert.Equal(6 * 49 + 12 * 7 + 8, t.Mesh.Positions.Count);
    }

    [Fact]
    public void OffGridBox_IsClosedWithoutSlivers()
    {
        var box = Box(new Vector3(0.13f, -0.41f, 0.2f), new Vector3(2.07f, 1.33f, 2.94f));
        var t = Run(box, 0.25f);
        AssertClosed(t.Mesh);
        AssertNoTJunctions(t.Mesh);
        AssertSameSurface(box, t);
        // Edge splits fall between lattice lines here, so allow a little more than the aligned case.
        Assert.True(MaxEdge(t.Mesh) <= 1.6f * 0.25f, $"max edge {MaxEdge(t.Mesh)}");
        Assert.True(MinAngle(t.Mesh) > 12, $"min angle {MinAngle(t.Mesh)}");
    }

    [Fact]
    public void LShapedPrism_ConcaveCapsStayInside()
    {
        var prism = LPrism();
        var t = Run(prism, 0.25f);
        AssertClosed(t.Mesh);
        AssertNoTJunctions(t.Mesh);
        AssertSameSurface(prism, t);
        Assert.Equal(Area(prism), Area(t.Mesh), 3);
        Assert.True(MaxEdge(t.Mesh) <= 1.5f * 0.25f, $"max edge {MaxEdge(t.Mesh)}");
        Assert.True(MinAngle(t.Mesh) > 20, $"min angle {MinAngle(t.Mesh)}");
    }

    [Fact]
    public void SmoothedCylinder_InterpolatesNormalsAcrossSides()
    {
        var cyl = Cylinder();
        var source = Topology.CornerNormals(cyl);
        var t = Tessellator.Tessellate(cyl, Enumerable.Range(0, cyl.Faces.Count).ToArray(), source, new TessellateOptions(0.25f));
        AssertClosed(t.Mesh);
        AssertSameSurface(cyl, t);

        int curved = 0;
        for (int i = 0; i < t.Mesh.Faces.Count; i++)
        {
            int f = t.SourceFaces[i];
            var flat = cyl.FaceNormal(f);
            var verts = cyl.Faces[f].Verts;
            for (int c = 0; c < 3; c++)
            {
                var n = t.CornerNormals[i][c];
                Assert.Equal(1f, n.Length(), 4);
                if (MathF.Abs(flat.Y) > 0.99f) { Assert.True(Vector3.Distance(n, flat) < 1e-5f, "cap normal not flat"); continue; }

                // Side faces: corners above each other share a normal, so the normal varies only across the face.
                var p = t.Mesh.Positions[t.Mesh.Faces[i].Verts[c]];
                var pa = cyl.Positions[verts[0]];
                int k = Enumerable.Range(1, verts.Length - 1).First(j => MathF.Abs(cyl.Positions[verts[j]].X - pa.X) + MathF.Abs(cyl.Positions[verts[j]].Z - pa.Z) > 1e-3f);
                var dir = cyl.Positions[verts[k]] - pa; dir.Y = 0;
                var rel = p - pa; rel.Y = 0;
                float s = Vector3.Dot(rel, dir) / dir.LengthSquared();
                var expected = Vector3.Normalize(Vector3.Lerp(source[f][0], source[f][k], s));
                Assert.True(Vector3.Distance(n, expected) < 1e-4f, $"normal {n} != {expected}");
                if (Vector3.Dot(n, flat) < 0.9999f) curved++;
            }
        }
        Assert.True(curved > 100, "side normals are flat");
    }

    [Fact]
    public void ExplicitUVs_AreInterpolatedAndExactAtCorners()
    {
        var box = Box(Vector3.Zero, new Vector3(2, 2, 2));
        int top = Enumerable.Range(0, box.Faces.Count).First(f => box.FaceNormal(f).Y > 0.99f);
        int side = Enumerable.Range(0, box.Faces.Count).First(f => box.FaceNormal(f).X > 0.99f);
        static Vector2 Affine(Vector3 p) => new(0.5f * p.X + 0.1f, 0.25f * p.Z - 0.3f);
        box.Faces[top].UVs = box.Faces[top].Verts.Select(v => Affine(box.Positions[v])).ToArray();
        box.Faces[side].UVs = [new(0.3f, 0.7f), new(1.9f, 0.1f), new(1.2f, 2.5f), new(-0.4f, 1.1f)]; // not affine

        var t = Run(box, 0.25f);
        AssertClosed(t.Mesh);
        for (int i = 0; i < t.Mesh.Faces.Count; i++)
        {
            int f = t.SourceFaces[i];
            var face = t.Mesh.Faces[i];
            if (f != top && f != side) { Assert.Null(face.UVs); continue; }
            Assert.NotNull(face.UVs);
            var src = box.Faces[f];
            for (int c = 0; c < 3; c++)
            {
                var p = t.Mesh.Positions[face.Verts[c]];
                var uv = face.UVs![c];
                if (f == top) Assert.True(Vector2.Distance(uv, Affine(p)) < 1e-5f, $"uv {uv} != {Affine(p)}");
                for (int k = 0; k < src.Verts.Length; k++)
                {
                    Vector3 a = box.Positions[src.Verts[k]], b = box.Positions[src.Verts[(k + 1) % src.Verts.Length]];
                    if (p == a) Assert.Equal(src.UVs![k], uv); // corners reproduce exactly
                    var ab = b - a;
                    float s = Vector3.Dot(p - a, ab) / ab.LengthSquared();
                    if (s > 0 && s < 1 && Vector3.Distance(a + ab * s, p) < 1e-5f)
                        Assert.True(Vector2.Distance(uv, Vector2.Lerp(src.UVs![k], src.UVs[(k + 1) % src.Verts.Length], s)) < 1e-5f, "boundary uv off its edge");
                }
            }
        }
    }

    [Fact]
    public void NeighbouringBoxes_ShareGridAndEdgeSplits()
    {
        const float s = 0.25f;
        var a = Box(new Vector3(0.1f, 0, 0.1f), new Vector3(2.1f, 1, 2.1f));
        var b = Box(new Vector3(2.1f, 0, 0.1f), new Vector3(4.35f, 1, 2.1f));
        var ta = Run(a, s);
        var tb = Run(b, s);
        AssertClosed(ta.Mesh);
        AssertClosed(tb.Mesh);

        // Interior lattice points of the two coplanar tops sit on one world grid.
        foreach (var (src, t) in new[] { (a, ta), (b, tb) })
        {
            int top = Enumerable.Range(0, src.Faces.Count).First(f => src.FaceNormal(f).Y > 0.99f);
            var interior = Enumerable.Range(0, t.Mesh.Faces.Count).Where(i => t.SourceFaces[i] == top)
                .SelectMany(i => t.Mesh.Faces[i].Verts).Distinct().Select(v => t.Mesh.Positions[v])
                .Where(p => !OnBoundary(src, top, p)).ToList();
            Assert.NotEmpty(interior);
            foreach (var p in interior)
            {
                Assert.True(MathF.Abs(p.X / s - MathF.Round(p.X / s)) < 1e-4f, $"x {p.X} off grid");
                Assert.True(MathF.Abs(p.Z / s - MathF.Round(p.Z / s)) < 1e-4f, $"z {p.Z} off grid");
            }
        }

        // The edge both tops share (x = 2.1, y = 1) is split at bit-identical points in both meshes.
        static HashSet<Vector3> OnSharedEdge(PolyMesh m) =>
            m.Positions.Where(p => p.X == 2.1f && p.Y == 1f && p.Z >= 0.1f && p.Z <= 2.1f).ToHashSet();
        var ea = OnSharedEdge(ta.Mesh);
        Assert.Equal(9, ea.Count);
        Assert.True(ea.SetEquals(OnSharedEdge(tb.Mesh)));
    }

    [Fact]
    public void CoplanarFacesInOneMesh_HaveNoTJunctions()
    {
        var m = new PolyMesh();
        m.Positions.AddRange([new(0, 0, 0), new(1.3f, 0, 0), new(3.1f, 0, 0), new(0, 0, 2), new(1.3f, 0, 2), new(3.1f, 0, 2)]);
        m.Faces.Add(new Face([0, 3, 4, 1], Mat, TextureMapping.Default));
        m.Faces.Add(new Face([1, 4, 5, 2], Mat, TextureMapping.Default));
        foreach (var f in m.Faces) if (m.FaceNormal(m.Faces.IndexOf(f)).Y < 0) Array.Reverse(f.Verts);

        var t = Run(m, 0.3f);
        AssertSameSurface(m, t);
        AssertNoTJunctions(t.Mesh);
        // The shared edge's pieces are used by one triangle on each side; the open border by one.
        var uses = new Dictionary<Edge, int>();
        foreach (var f in t.Mesh.Faces) foreach (var e in f.Edges()) uses[e] = uses.GetValueOrDefault(e) + 1;
        int shared = 0;
        foreach (var (e, count) in uses)
        {
            Vector3 p = t.Mesh.Positions[e.A], q = t.Mesh.Positions[e.B];
            bool onShared = p.X == 1.3f && q.X == 1.3f;
            bool onBorder = (p.X == q.X && (p.X == 0 || p.X == 3.1f)) || (p.Z == q.Z && (p.Z == 0 || p.Z == 2));
            Assert.Equal(onBorder && !onShared ? 1 : 2, count);
            if (onShared) shared++;
        }
        Assert.Equal(7, shared); // ceil(2 / 0.3)
    }

    [Fact]
    public void SliverFaces_StayValid()
    {
        var thin = Box(Vector3.Zero, new Vector3(8, 0.02f, 0.02f));
        var t = Run(thin, 0.25f);
        AssertClosed(t.Mesh);
        AssertSameSurface(thin, t);
        Assert.Equal(Area(thin), Area(t.Mesh), 4);

        var tri = new PolyMesh();
        tri.Positions.AddRange([new(0, 0, 0), new(5, 0, 0.01f), new(10, 0, 0)]);
        tri.Faces.Add(new Face([0, 1, 2], Mat, TextureMapping.Default));
        var tt = Run(tri, 0.25f);
        AssertSameSurface(tri, tt);
        Assert.Equal(Area(tri), Area(tt.Mesh), 4);
    }

    [Fact]
    public void FaceSmallerThanSpacing_KeepsItsCorners()
    {
        var box = Box(Vector3.Zero, new Vector3(0.3f, 0.3f, 0.3f));
        var t = Run(box, 1f);
        AssertClosed(t.Mesh);
        AssertSameSurface(box, t);
        Assert.Equal(12, t.TriangleCount);
        Assert.Equal(8, t.Mesh.Positions.Count);
    }

    [Fact]
    public void Cap_CoarsensInteriorButNotEdges()
    {
        const float s = 0.25f;
        var box = Box(Vector3.Zero, new Vector3(40, 40, 40));
        var t = Run(box, s, cap: 1024);
        AssertClosed(t.Mesh);
        AssertSameSurface(box, t);

        int onEdges = t.Mesh.Positions.Count(p => Enumerable.Range(0, box.Faces.Count).Any(f => OnBoundary(box, f, p)));
        Assert.Equal(8 + 12 * 159, onEdges); // edges split at the global spacing regardless of the cap
        for (int f = 0; f < box.Faces.Count; f++)
        {
            var interior = Enumerable.Range(0, t.Mesh.Faces.Count).Where(i => t.SourceFaces[i] == f)
                .SelectMany(i => t.Mesh.Faces[i].Verts).Distinct().Select(v => t.Mesh.Positions[v])
                .Where(p => !OnBoundary(box, f, p)).ToList();
            Assert.InRange(interior.Count, 256, 1024);
            foreach (var p in interior)
                foreach (float c in (float[])[p.X, p.Y, p.Z])
                    Assert.True(MathF.Abs(c / s - MathF.Round(c / s)) < 1e-4f, "capped lattice left the world grid");
        }
    }

    [Fact]
    public void SameInput_SameOutput()
    {
        foreach (var make in new Func<PolyMesh>[] { Cylinder, LPrism, () => Box(new Vector3(0.13f, 0, 0.2f), new Vector3(3.3f, 2, 1.7f)) })
        {
            var x = Run(make(), 0.2f);
            var y = Run(make(), 0.2f);
            Assert.Equal(x.Mesh.Positions, y.Mesh.Positions);
            Assert.Equal(x.SourceFaces, y.SourceFaces);
            Assert.Equal(x.Mesh.Faces.Count, y.Mesh.Faces.Count);
            for (int i = 0; i < x.Mesh.Faces.Count; i++)
            {
                Assert.Equal(x.Mesh.Faces[i].Verts, y.Mesh.Faces[i].Verts);
                Assert.Equal(x.CornerNormals[i], y.CornerNormals[i]);
            }
        }
    }

    [Fact]
    public void FlatFaces_KeepTheFaceNormal()
    {
        var box = Box(Vector3.Zero, new Vector3(2, 1, 3));
        var t = Run(box, 0.25f, smooth: false);
        for (int i = 0; i < t.Mesh.Faces.Count; i++)
            foreach (var n in t.CornerNormals[i])
                Assert.True(Vector3.Distance(n, box.FaceNormal(t.SourceFaces[i])) < 1e-5f);
    }

    [Fact]
    public void StressShapes_StayClosedAndOnTheSurface()
    {
        // Every block-tool shape the stress level uses (spheres, tori, arches, stairs, smoothed and not).
        var doc = Chip.Core.Diagnostics.StressLevel.Generate(meshes: 60, entities: 0);
        foreach (var mesh in doc.Meshes)
        {
            var t = Run(mesh, 0.3f);
            AssertClosed(t.Mesh);
            AssertSameSurface(mesh, t);
            Assert.Equal(0, t.CoarseFaces);
            Assert.Equal(0, t.SkippedPoints);
            // Strips narrower than the lattice margin (arch legs) are bridged by boundary-to-boundary triangles.
            Assert.True(MaxEdge(t.Mesh) <= 2f * 0.3f, $"{mesh.Name}: max edge {MaxEdge(t.Mesh)}");
        }
    }

    [Fact]
    public void DegenerateLoops_DontThrow()
    {
        // A corner sitting on a straight edge is resampled normally.
        var m = new PolyMesh();
        m.Positions.AddRange([new(0, 0, 0), new(1, 0, 0), new(2, 0, 0), new(2, 0, 2), new(0, 0, 2)]);
        m.Faces.Add(new Face([0, 4, 3, 2, 1], Mat, TextureMapping.Default));
        var t = Run(m, 0.25f);
        AssertSameSurface(m, t);
        Assert.Equal(0, t.CoarseFaces);

        // A bow tie and a zero-area face come through without throwing.
        var bad = new PolyMesh();
        bad.Positions.AddRange([new(0, 0, 0), new(2, 0, 2), new(2, 0, 0), new(0, 0, 2), new(3, 0, 0), new(4, 0, 0), new(5, 0, 0)]);
        bad.Faces.Add(new Face([0, 1, 2, 3], Mat, TextureMapping.Default));
        bad.Faces.Add(new Face([4, 5, 6], Mat, TextureMapping.Default));
        var tb = Run(bad, 0.25f);
        Assert.All(tb.Mesh.Faces, f => Assert.Equal(3, f.Verts.Length));
    }

    [Fact]
    public void Subset_OnlyTessellatesTheGivenFaces()
    {
        var box = Box(Vector3.Zero, new Vector3(2, 2, 2));
        var t = Tessellator.Tessellate(box, [1, 3], Topology.CornerNormals(box), new TessellateOptions(0.5f));
        Assert.All(t.SourceFaces, f => Assert.Contains(f, (int[])[1, 3]));
        AssertSameSurface(box, t);
    }
}
