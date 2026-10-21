using System.Numerics;
using Chip.Core.Geometry;
using Chip.Core.IO;
using Chip.Core.Scene;
using static Chip.Core.Tests.ClipTests;

namespace Chip.Core.Tests;

public class TagTests
{
    static readonly TagSet Surface = TagSet.Of("wall", "trim-a");

    static PolyMesh TaggedBox(Vector3 min = default, float size = 2)
    {
        var m = Box(min, min + new Vector3(size));
        foreach (var f in m.Faces) f.Tags = Surface;
        m.Tags = TagSet.Of("railing");
        return m;
    }

    static void AllTagged(PolyMesh m, string op)
    {
        for (int f = 0; f < m.Faces.Count; f++)
            Assert.True(m.Faces[f].Tags == Surface, $"{op}: face {f} has [{m.Faces[f].Tags}]");
    }

    // ---- TagSet ----

    [Theory]
    [InlineData("wall")]
    [InlineData("trim-a")]
    [InlineData("floor_2")]
    [InlineData("9th")]
    [InlineData("skip:baseboard")]
    public void Valid_names(string tag) => Assert.True(TagSet.IsValid(tag, out _));

    [Theory]
    [InlineData("")]
    [InlineData("Wall")]
    [InlineData("wall trim")]
    [InlineData("-wall")]
    [InlineData("skip:")]
    [InlineData("a:b")]
    [InlineData("wäll")]
    public void Invalid_names_say_why(string tag)
    {
        Assert.False(TagSet.IsValid(tag, out var why));
        Assert.False(string.IsNullOrWhiteSpace(why));
    }

    [Fact]
    public void Typed_input_normalizes_to_a_tag()
    {
        Assert.Equal("wall-trim", TagSet.Normalize("  Wall  Trim "));
        Assert.Equal("skip:baseboard", TagSet.Normalize("skip:Baseboard"));
        Assert.Null(TagSet.Normalize("wall!"));
    }

    [Fact]
    public void Sets_are_sorted_distinct_values()
    {
        var a = TagSet.Of("wall", "floor", "wall");
        Assert.Equal(["floor", "wall"], a.ToArray());
        TagSet literal = ["wall", "floor"];
        Assert.Equal(a, literal);
        Assert.Equal(a, TagSet.Of("floor").Add("wall"));
        Assert.Equal(a.GetHashCode(), TagSet.Of("floor").Add("wall").GetHashCode());
        Assert.Equal(TagSet.Empty, a.Remove("wall").Remove("floor"));
        Assert.True(a.Remove("ceiling") == a);
        Assert.Throws<ArgumentException>(() => a.Add("Not Valid"));
    }

    // ---- Propagation: every op that builds faces from a source face carries its tags ----

    [Fact]
    public void Every_op_keeps_tags_on_every_face()
    {
        void Run(string op, Action<PolyMesh> edit, bool grows = true)
        {
            var m = TaggedBox();
            edit(m);
            if (grows) Assert.True(m.Faces.Count > 6, $"{op} didn't add faces");
            AllTagged(m, op);
            Assert.True(m.Tags == TagSet.Of("railing"), $"{op} lost the mesh tags");
        }

        Run("extrude", m => MeshOps.ExtrudeFaces(m, [3]));
        Run("arc extrude", m => MeshOps.ArcExtrudeFaces(m, [3], ArcSweep.Bend(new(0.5f, 1, 0.5f), Vector3.UnitY, Vector3.UnitX, 1, 1.5f, 3), true));
        Run("push/pull", m => MeshOps.PushPullFaces(m, [3, 1], Vector3.UnitY));
        Run("inset", m => MeshOps.Inset(m, [3]).Apply(m, 0.25f, outset: false));
        Run("outset", m => MeshOps.Inset(m, [3]).Apply(m, 0.25f, outset: true));
        Run("offset", m => MeshOps.Offset(m, [3]).Apply(m, 0.25f));
        Run("bevel edges", m => MeshOps.BevelEdges(m, m.Edges().Take(4), 0.2f, 2));
        Run("bevel vertices", m => MeshOps.BevelVertices(m, [0, 7], 0.2f));
        Run("connect", m => MeshOps.ConnectVertices(m, [2, 7]));
        Run("connect edges", m => MeshOps.ConnectEdges(m, MeshOps.EdgesOfFaces(m, [3]).Take(2)));
        Run("duplicate", m => MeshOps.DuplicateFaces(m, [3, 4]));
        Run("knife", m => MeshOps.Knife(m, [new(-0.5f, 2, 1), new(2.5f, 2, 1)], new Vector3(1, 10, 1)));
        Run("mirror", m => MeshOps.Mirror(m, Enumerable.Range(0, m.Positions.Count).ToArray(), Vector3.Zero, Vector3.UnitX), grows: false);
        Run("flip", m => MeshOps.FlipFaces(m, [0, 1]), grows: false);
        Run("merge meshes", m => MeshOps.MergeMeshes(m, [TaggedBox(new Vector3(5, 0, 0))]));
        Run("bridge", m =>
        {
            MeshOps.MergeMeshes(m, [TaggedBox(new Vector3(5, 0, 0))]);
            Assert.NotNull(MeshOps.BridgeFaces(m, 1, 6, out _));
        });
        Run("merge faces", m =>
        {
            MeshOps.ConnectVertices(m, [2, 7]); // split the top, then merge it back
            Assert.NotEmpty(MeshOps.MergeFaces(m, [3, m.Faces.Count - 1]).Merged);
        }, grows: false);

        var src = TaggedBox();
        AllTagged(MeshOps.CopyFaces(src, [1, 3]), "copy faces");
        var (front, back) = MeshOps.Split(TaggedBox(), new Vector3(1, 1, 1), Vector3.UnitX);
        AllTagged(front!, "clip front");
        AllTagged(back!, "clip back");

        var plane = new PolyMesh();
        plane.Positions.AddRange([new(0, 0, 0), new(0, 0, 1), new(1, 0, 1), new(1, 0, 0)]);
        plane.Faces.Add(new Face([0, 1, 2, 3], "dev_a", default) { Tags = Surface });
        MeshOps.ExtrudeEdges(plane, [new Edge(0, 1)]);
        AllTagged(plane, "extrude edges");
        MeshOps.ArcExtrudeEdges(plane, [new Edge(2, 3)], ArcSweep.Bend(new(1, 0, 0.5f), Vector3.UnitX, Vector3.UnitY, 1, 1.5f, 3), true);
        AllTagged(plane, "arc extrude edges");
    }

    [Fact]
    public void Clone_and_hash_include_tags()
    {
        var m = TaggedBox();
        var copy = m.Clone();
        Assert.True(copy.Tags == m.Tags && copy.Faces[2].Tags == m.Faces[2].Tags);
        ulong before = MeshHash.Of(m);
        m.Faces[2].Tags = m.Faces[2].Tags.Add("floor");
        Assert.NotEqual(before, MeshHash.Of(m));
        ulong mid = MeshHash.Of(m);
        m.Tags = TagSet.Empty;
        Assert.NotEqual(mid, MeshHash.Of(m));
    }

    // ---- Save, undo, select ----

    [Fact]
    public void Tags_round_trip_through_the_file_and_are_omitted_when_empty()
    {
        var doc = new Document();
        var m = doc.Add(TaggedBox());
        m.Faces[0].Tags = TagSet.Empty;
        var json = DocumentSerializer.Serialize(doc);
        var back = DocumentSerializer.Deserialize(json).Meshes[0];
        Assert.True(back.Tags == TagSet.Of("railing"));
        Assert.True(back.Faces[0].Tags.IsEmpty);
        Assert.True(back.Faces[1].Tags == Surface);

        Assert.DoesNotContain("\"tags\"", DocumentSerializer.Serialize(new Document { Meshes = { Box(Vector3.Zero, Vector3.One) } }));
    }

    [Fact]
    public void A_file_with_a_bad_tag_says_which_mesh_and_why()
    {
        var doc = new Document();
        doc.Add(TaggedBox());
        var json = DocumentSerializer.Serialize(doc).Replace("\"railing\"", "\"Rail Ing\"");
        var e = Assert.Throws<InvalidDataException>(() => DocumentSerializer.Deserialize(json));
        Assert.Contains("Mesh 1", e.Message);
        Assert.Contains("Rail Ing", e.Message);
    }

    [Fact]
    public void Tagging_is_an_undoable_step_on_faces_or_meshes()
    {
        var session = new Session();
        var doc = new Document();
        var m = doc.Add(Box(Vector3.Zero, new Vector3(2)));
        session.Reset(doc, null);
        session.Sel.ConvertTo(SelectMode.Face, session.Doc);
        session.Sel.Add(ElementRef.Face(m.Id, 1));
        session.Sel.Add(ElementRef.Face(m.Id, 3));

        session.Do("Tag", () => Assert.Equal(2, TagOps.Set(session.Doc, session.Sel, "wall", on: true)));
        Assert.Equal((2, 2), TagOps.Coverage(session.Doc, session.Sel, "wall"));
        Assert.Equal(0, TagOps.Set(session.Doc, session.Sel, "wall", on: true)); // already there: nothing changes
        Assert.True(session.Undo());
        Assert.True(session.Doc.Meshes[0].Faces[1].Tags.IsEmpty);
        Assert.True(session.Redo());
        Assert.True(session.Doc.Meshes[0].Faces[3].Tags.Contains("wall"));

        session.Sel.ConvertTo(SelectMode.Object, session.Doc);
        session.Do("Tag mesh", () => TagOps.Set(session.Doc, session.Sel, "railing", on: true));
        Assert.True(session.Doc.Meshes[0].Tags.Contains("railing"));
        Assert.True(session.Doc.Meshes[0].Faces[0].Tags.IsEmpty); // object mode tags the mesh, not its faces
        Assert.True(session.Undo());
        Assert.True(session.Doc.Meshes[0].Tags.IsEmpty);
    }

    [Fact]
    public void Usage_counts_and_select_by_tag_skip_hidden_and_locked()
    {
        var doc = new Document();
        var a = doc.Add(TaggedBox());
        var b = doc.Add(TaggedBox(new Vector3(5, 0, 0)));
        var hidden = doc.Add(TaggedBox(new Vector3(10, 0, 0)));
        hidden.Hidden = true;
        b.Faces[0].Tags = TagSet.Of("floor");

        var usage = TagOps.Usage(doc);
        Assert.Equal(new TagUse("floor", 1, 0), usage[0]);
        Assert.Equal(new TagUse("railing", 0, 3), usage[1]);
        Assert.Equal(new TagUse("wall", 17, 0), usage[3]);

        var sel = new Selection();
        Assert.Equal(11, TagOps.Select(doc, sel, "wall", SelectMode.Face));
        Assert.Equal(SelectMode.Face, sel.Mode);
        Assert.Equal(2, TagOps.Select(doc, sel, "railing", SelectMode.Object));
        Assert.Contains(ElementRef.Object(a.Id), sel.Items);
        Assert.DoesNotContain(ElementRef.Object(hidden.Id), sel.Items);
    }
}
