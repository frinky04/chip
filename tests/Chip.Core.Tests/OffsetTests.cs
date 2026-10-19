using System.Numerics;
using Chip.Core.Geometry;
using static Chip.Core.Tests.ClipTests;

namespace Chip.Core.Tests;

public class OffsetTests
{
    // Box faces: 0 -X, 1 +X, 2 -Y, 3 +Y, 4 -Z, 5 +Z.

    [Fact]
    public void One_face_moves_out_along_its_normal_with_bridging_sides()
    {
        var m = Box(Vector3.Zero, new Vector3(2));
        var r = MeshOps.Offset(m, [3]);
        r.Apply(m, 0.5f);
        Assert.Equal(10, m.Faces.Count);
        Assert.Equal(4, r.SideFaces.Count);
        Assert.All(m.Faces[3].Verts, v => Assert.Equal(2.5f, m.Positions[v].Y, 5));
        Assert.True(IsConsistentlyWound(m));
        Assert.All(r.SideFaces, f => Assert.Equal("dev_a", m.Faces[f].Material));
    }

    [Fact]
    public void Corner_between_two_faces_is_mitred_so_each_plane_moves_the_full_distance()
    {
        var m = Box(Vector3.Zero, new Vector3(2));
        MeshOps.Offset(m, [1, 5]).Apply(m, 0.25f);
        Assert.All(m.Faces[1].Verts, v => Assert.Equal(2.25f, m.Positions[v].X, 5));
        Assert.All(m.Faces[5].Verts, v => Assert.Equal(2.25f, m.Positions[v].Z, 5));
        Assert.True(IsConsistentlyWound(m));
    }

    [Fact]
    public void A_ring_of_walls_becomes_an_even_band_like_a_baseboard()
    {
        var m = Box(Vector3.Zero, new Vector3(4, 1, 4));
        MeshOps.Offset(m, [0, 1, 4, 5]).Apply(m, 0.1f);
        Assert.Equal(6 + 8, m.Faces.Count); // top and bottom outlines bridged, the ring itself stays joined
        Assert.All(m.Faces[0].Verts, v => Assert.Equal(-0.1f, m.Positions[v].X, 5));
        Assert.All(m.Faces[1].Verts, v => Assert.Equal(4.1f, m.Positions[v].X, 5));
        Assert.All(m.Faces[4].Verts, v => Assert.Equal(-0.1f, m.Positions[v].Z, 5));
        Assert.All(m.Faces[5].Verts, v => Assert.Equal(4.1f, m.Positions[v].Z, 5));
        Assert.All(m.Faces[0].Verts, v => Assert.InRange(m.Positions[v].Y, -1e-5f, 1 + 1e-5f)); // height unchanged
        Assert.True(IsConsistentlyWound(m));
    }

    [Fact]
    public void Negative_distance_pulls_faces_in_and_the_sides_face_into_the_recess()
    {
        var m = Box(Vector3.Zero, new Vector3(2));
        var r = MeshOps.Offset(m, [3]);
        r.Apply(m, -0.5f);
        Assert.All(m.Faces[3].Verts, v => Assert.Equal(1.5f, m.Positions[v].Y, 5));
        var west = r.SideFaces.Single(f => m.Faces[f].Verts.All(v => m.Positions[v].X < 1e-5f));
        Assert.True(m.FaceNormal(west).X > 0.99f);
        Assert.True(IsConsistentlyWound(m));
    }

    [Fact]
    public void A_closed_region_inflates_in_place_without_new_vertices()
    {
        var m = Box(Vector3.Zero, new Vector3(2));
        var r = MeshOps.Offset(m, Enumerable.Range(0, 6).ToArray());
        r.Apply(m, 0.5f);
        Assert.Empty(r.SideFaces);
        Assert.Equal(8, m.Positions.Count);
        var (min, max) = m.Bounds();
        Assert.Equal(new Vector3(-0.5f), min);
        Assert.Equal(new Vector3(2.5f), max);
    }

    static PolyMesh Plane()
    {
        var m = new PolyMesh();
        m.Positions.AddRange([new(0, 0, 0), new(0, 0, 1), new(1, 0, 1), new(1, 0, 0)]);
        m.Faces.Add(new Face([0, 1, 2, 3], "dev_a", default));
        return m;
    }

    [Fact]
    public void A_lone_plane_offsets_into_a_closed_slab()
    {
        var m = Plane();
        Assert.True(m.FaceNormal(0).Y > 0.99f);
        var r = MeshOps.Offset(m, [0]);
        r.Apply(m, 0.25f);
        Assert.Single(r.CapFaces);
        Assert.Equal(6, m.Faces.Count);
        Assert.True(IsConsistentlyWound(m));
        Assert.True(m.FaceNormal(r.CapFaces[0]).Y < -0.99f);
        Assert.Equal(0.25f, m.Bounds().Max.Y, 5);
    }

    [Fact]
    public void A_slab_offset_backwards_turns_round_so_it_stays_outward_facing()
    {
        var m = Plane();
        var r = MeshOps.Offset(m, [0]);
        r.Apply(m, -0.25f);
        Assert.True(IsConsistentlyWound(m));
        Assert.True(m.FaceNormal(0).Y < -0.99f); // the moved face is now the bottom
        Assert.True(m.FaceNormal(r.CapFaces[0]).Y > 0.99f);
        Assert.All(r.SideFaces, s => Assert.True(Vector3.Dot(m.FaceNormal(s), m.FaceCenter(s) - new Vector3(0.5f, -0.125f, 0.5f)) > 0));
        r.Apply(m, 0.25f); // and back again
        Assert.True(m.FaceNormal(0).Y > 0.99f);
        Assert.True(IsConsistentlyWound(m));
    }

    [Fact]
    public void Faces_joined_to_the_rest_of_the_mesh_get_no_cap()
    {
        var m = Box(Vector3.Zero, new Vector3(2));
        Assert.Empty(MeshOps.Offset(m, [3]).CapFaces);
    }

    [Fact]
    public void Apply_can_be_called_repeatedly_from_the_prepared_state()
    {
        var m = Box(Vector3.Zero, new Vector3(2));
        var r = MeshOps.Offset(m, [3]);
        r.Apply(m, 3);
        r.Apply(m, 0.25f);
        Assert.All(m.Faces[3].Verts, v => Assert.Equal(2.25f, m.Positions[v].Y, 5));
    }
}
