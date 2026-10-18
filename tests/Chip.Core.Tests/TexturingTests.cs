using System.Numerics;
using Chip.Core.Geometry;
using Chip.Core.Scene;
using Chip.Core.Texturing;

namespace Chip.Core.Tests;

/// <summary>Shared helpers for the texturing tests, plus the projection/explicit equivalence and texture lock.</summary>
public class TexturingTests
{
    internal const float Eps = 1e-3f;

    /// <summary>"wide" is 1024x512; everything else is 512x512.</summary>
    internal static readonly TextureSize Size = name => name == "wide" ? (1024, 512) : (512, 512);

    internal static PolyMesh Box(Vector3 min, Vector3 max, string material = "dev") =>
        MeshOps.CreateBox(min, max, material, TextureMapping.Default);

    internal static int FaceWithNormal(PolyMesh m, Vector3 n) =>
        Enumerable.Range(0, m.Faces.Count).First(f => Vector3.Dot(m.FaceNormal(f), n) > 0.99f);

    internal static readonly TextureMapping Floor = TextureMapping.AlignToGrid(Vector3.UnitY, TextureMapping.Default);

    /// <summary>A strip of quads on the floor (normal +Y) along X, with the given segment lengths, 1 m wide in Z.</summary>
    internal static PolyMesh FloorStrip(params float[] lengths)
    {
        var m = new PolyMesh();
        float x = 0;
        m.Positions.Add(new Vector3(0, 0, 0));
        m.Positions.Add(new Vector3(0, 0, 1));
        foreach (var len in lengths)
        {
            x += len;
            m.Positions.Add(new Vector3(x, 0, 0));
            m.Positions.Add(new Vector3(x, 0, 1));
        }
        for (int i = 0; i < lengths.Length; i++)
        {
            int a = i * 2;
            m.Faces.Add(new Face([a, a + 1, a + 3, a + 2], "dev", Floor));
        }
        Assert.All(Enumerable.Range(0, m.Faces.Count), f => Assert.True(m.FaceNormal(f).Y > 0.99f));
        return m;
    }

    /// <summary>An arc of quads (a curved road): inner radius 4, outer 5, <paramref name="segments"/> over 90 degrees.</summary>
    internal static PolyMesh Arc(int segments)
    {
        var m = new PolyMesh();
        for (int i = 0; i <= segments; i++)
        {
            float t = i * MathF.PI * 0.5f / segments;
            m.Positions.Add(new Vector3(MathF.Cos(t) * 4, 0, MathF.Sin(t) * 4));
            m.Positions.Add(new Vector3(MathF.Cos(t) * 5, 0, MathF.Sin(t) * 5));
        }
        for (int i = 0; i < segments; i++)
        {
            int a = i * 2;
            m.Faces.Add(new Face([a, a + 2, a + 3, a + 1], "dev", Floor));
            if (m.FaceNormal(i).Y < 0) Array.Reverse(m.Faces[i].Verts);
        }
        return m;
    }

    /// <summary>UV of vertex <paramref name="v"/> as seen by face <paramref name="f"/>.</summary>
    internal static Vector2 UvAt(PolyMesh m, int f, int v) => UvSpace.Get(new FaceRef(m, f), Size)[UvSpace.Corner(m.Faces[f], v)];

    /// <summary>Asserts the two faces show identical UVs at both corners of their shared edge.</summary>
    internal static void AssertSeamless(PolyMesh m, int a, int b)
    {
        var edge = UvSpace.SharedEdge(m, a, b) ?? throw new Exception($"faces {a} and {b} don't touch");
        AssertNear(UvAt(m, a, edge.A), UvAt(m, b, edge.A));
        AssertNear(UvAt(m, a, edge.B), UvAt(m, b, edge.B));
    }

    internal static void AssertNear(Vector2 expected, Vector2 actual, float eps = Eps) =>
        Assert.True(Vector2.Distance(expected, actual) < eps, $"expected {expected}, got {actual}");

    internal static void AssertNear(float expected, float actual, float eps = Eps) =>
        Assert.True(MathF.Abs(expected - actual) < eps, $"expected {expected}, got {actual}");

    /// <summary>UV distance per world metre along an edge of a face (512 px/m on a 512 px texture = 1).</summary>
    internal static float Density(PolyMesh m, int f, int i)
    {
        var face = m.Faces[f];
        var uv = UvSpace.Get(new FaceRef(m, f), Size);
        int j = (i + 1) % face.Verts.Length;
        var (w, _) = Size(face.Material);
        float world = Vector3.Distance(m.Positions[face.Verts[i]], m.Positions[face.Verts[j]]);
        return Vector2.Distance(uv[i], uv[j]) * w / TextureMapping.TexelsPerMeter / world;
    }

    [Fact]
    public void Affine_edits_agree_on_projected_and_explicit_faces()
    {
        var a = Box(Vector3.Zero, new Vector3(3, 2, 1));
        var b = a.Clone();
        int f = FaceWithNormal(a, Vector3.UnitZ);
        a.Faces[f].Map = TextureMapping.AlignToFace(a.FaceNormal(f), a.Faces[f].Map) with { Rotation = 30, ShiftU = 17 };
        b.Faces[f].Map = a.Faces[f].Map;
        UvSpace.Bake(new FaceRef(b, f), Size);

        foreach (var m in new[] { a, b })
        {
            var r = new FaceRef(m, f);
            UvSpace.ApplyAffine(r, new Vector2(-2, 2), new Vector2(0.25f, -1), Size);
            UvSpace.Rotate(r, 25, new Vector2(0.3f, 0.7f), Size);
        }
        Assert.Null(a.Faces[f].UVs); // stayed a projection
        var ua = UvSpace.Get(new FaceRef(a, f), Size);
        var ub = UvSpace.Get(new FaceRef(b, f), Size);
        for (int i = 0; i < ua.Length; i++) AssertNear(ub[i], ua[i]);
    }

    [Fact]
    public void Positive_rotation_turns_the_image_clockwise_on_screen()
    {
        // Wall facing +Z: U = +X (right), V = -Y (down). A point right of the pivot should sample texels from above.
        var m = Box(Vector3.Zero, new Vector3(2, 2, 2));
        int f = FaceWithNormal(m, Vector3.UnitZ);
        var r = new FaceRef(m, f);
        UvSpace.Bake(r, Size);
        var pivot = new Vector2(0.5f, 0.5f);
        m.Faces[f].UVs = [pivot + new Vector2(1, 0), pivot, pivot, pivot];
        UvSpace.Rotate(r, 10, pivot, Size);
        Assert.True(m.Faces[f].UVs![0].Y < pivot.Y);
    }

    [Fact]
    public void Texture_lock_off_leaves_projection_world_aligned_on_object_moves()
    {
        foreach (bool locked in new[] { true, false })
        {
            var doc = new Document();
            var box = doc.Add(Box(Vector3.Zero, Vector3.One));
            var s = new Session { TextureLock = locked };
            s.Reset(doc, null);
            s.Sel.ConvertTo(SelectMode.Object, s.Doc);
            s.Sel.Add(ElementRef.Object(box.Id));
            var before = s.Doc.Meshes[0].Faces[0].Map;
            var g = TransformGesture.Start(s, "Move")!;
            g.Apply(Matrix4x4.CreateTranslation(0.5f, 0.25f, 0.75f), rigid: true);
            g.Commit();
            var after = s.Doc.Meshes[0].Faces[0].Map;
            Assert.Equal(locked, !before.Equals(after));
        }
    }

    [Fact]
    public void Unwrap_is_one_undo_step()
    {
        var doc = new Document();
        var box = doc.Add(Box(Vector3.Zero, Vector3.One));
        var s = new Session();
        s.Reset(doc, null);
        s.Do("Unwrap", () => Unwrap.Apply(s.Doc.Meshes[0], [0, 1, 2], UnwrapMode.Conforming, Size));
        Assert.NotNull(s.Doc.Meshes[0].Faces[0].UVs);
        Assert.True(s.Undo());
        Assert.All(s.Doc.Meshes[0].Faces, f => Assert.Null(f.UVs));
        _ = box;
    }
}
