using Chip.Core.Texturing;
using System.Numerics;
using Chip.Core.Diagnostics;
using Chip.Core.Geometry;
using Chip.Core.IO;
using Chip.Core.Scene;

namespace Chip.Core.Tests;

public class UndoDeltaTests
{
    static Session Create(Document doc, bool reference = false)
    {
        var session = new Session { FullSnapshotUndo = reference, ValidateUndoScopes = true };
        session.Reset(doc, null);
        return session;
    }

    static void Equal(Session expected, Session actual)
    {
        Assert.Equal(DocumentSerializer.Serialize(expected.Doc), DocumentSerializer.Serialize(actual.Doc));
        Assert.Equal(expected.Sel.Mode, actual.Sel.Mode);
        Assert.True(expected.Sel.Items.SetEquals(actual.Sel.Items));
        Assert.Equal(expected.Sel.Active, actual.Sel.Active);
        Assert.Equal(expected.Dirty, actual.Dirty);
        Assert.Equal(expected.Version, actual.Version);
        Assert.Equal(expected.InGesture, actual.InGesture);
        Assert.Equal(expected.UndoLabel, actual.UndoLabel);
        Assert.Equal(expected.RedoLabel, actual.RedoLabel);
        Assert.Equal(expected.Path, actual.Path);
    }

    [Theory]
    [InlineData(12, 300)]
    [InlineData(2000, 6)] // stress spot-check: every step serializes the whole level twice in Debug
    public void RandomEditsMatchFullSnapshots(int meshes, int count)
    {
        var doc = StressLevel.Generate(meshes, entities: meshes == 2000 ? 200 : 8);
        var actual = Create(doc);
        var reference = Create(doc.Clone(), reference: true);
        var rng = new Random(1807);
        for (int step = 0; step < count; step++)
        {
            int action = rng.Next(10), index = rng.Next(actual.Doc.Meshes.Count), choice = rng.Next(12);
            if (choice == 0) { Assert.Equal(reference.Undo(), actual.Undo()); }
            else if (choice == 1) { Assert.Equal(reference.Redo(), actual.Redo()); }
            else if (choice == 2) { actual.MarkSaved("test.chip"); reference.MarkSaved("test.chip"); }
            else
            {
                foreach (var session in new[] { actual, reference })
                {
                    session.Sel.Clear();
                    session.Sel.ConvertTo(step % 2 == 0 ? SelectMode.Object : SelectMode.Face, session.Doc);
                    int id = session.Doc.Meshes[index].Id;
                    session.Sel.Add(session.Sel.Mode == SelectMode.Object ? ElementRef.Object(id) : ElementRef.Face(id, 0));
                    string label = $"edit {step}";
                    if (step % 3 == 0) session.Begin(label); // Safe fallback mixed into the same history.
                    else session.Begin(label, action == 8 ? session.Doc.Meshes.Select(m => m.Id) : new[] { id });
                    Edit(session, action, index, step);
                    session.Touch();
                    Assert.False(session.Undo());
                    Assert.False(session.Redo());
                    if (choice == 3) session.Cancel();
                    else session.End();
                }
            }
            Equal(reference, actual);
        }
        while (reference.Undo()) { Assert.True(actual.Undo()); Equal(reference, actual); }
        Assert.False(actual.Undo());
        while (reference.Redo()) { Assert.True(actual.Redo()); Equal(reference, actual); }
        Assert.False(actual.Redo());
    }

    static void Edit(Session session, int action, int index, int step)
    {
        var doc = session.Doc;
        var mesh = doc.Meshes[index];
        switch (action)
        {
            case 0:
                for (int i = 0; i < mesh.Positions.Count; i++) mesh.Positions[i] += new Vector3(0.25f, step, -0.5f);
                break;
            case 1:
                var face = mesh.Faces[0];
                face.Material = step % 2 == 0 ? "BRICK" : "brick";
                face.Map.Rotation += 15;
                face.UVs = face.Verts.Select((_, i) => new Vector2(i, step)).ToArray();
                face.Dominant = !face.Dominant;
                mesh.SoftEdges.SymmetricExceptWith(face.Edges());
                break;
            case 2:
                MeshOps.DuplicateFaces(mesh, [0]);
                session.Sel.Clear();
                break;
            case 3:
                var entity = doc.Entities[0];
                entity.Position += Vector3.One;
                entity.Rotation += new Vector3(0, 45, 0);
                entity.Properties["custom"] = step.ToString();
                entity.ClassName = "light_point";
                entity.Hidden = !entity.Hidden;
                entity.Locked = !entity.Locked;
                doc.Add(new Entity { Name = $"entity {step}" });
                doc.Entities.Reverse();
                if (doc.Entities.Count > 12) doc.Entities.RemoveAt(1);
                break;
            case 4:
                var group = doc.Add(new Group { Name = $"group {step}", Members = [mesh.Id], Hidden = true, Locked = true });
                if (doc.Groups.Count > 1) { doc.Groups[0].Parent = group.Id; doc.Groups[0].Members.Add(doc.Entities[0].Id); }
                doc.Groups.Reverse();
                if (doc.Groups.Count > 6) doc.Groups.RemoveAt(1);
                break;
            case 5:
                doc.ExportScale += 0.25f;
                doc.ExportCollision = !doc.ExportCollision;
                doc.ExportPath = $"test{step}.glb";
                doc.Export.Smoothing = !doc.Export.Smoothing;
                doc.Export.IncludeHidden = !doc.Export.IncludeHidden;
                doc.Export.EmbedTextures = !doc.Export.EmbedTextures;
                doc.Export.ExportOnSave = !doc.Export.ExportOnSave;
                doc.Export.MaterialRemap["brick"] = $"res://{step}.tres";
                break;
            case 6:
                doc.Meshes.RemoveAt(index);
                doc.Add(mesh.Clone());
                doc.Meshes.Reverse();
                break;
            case 7:
                mesh = mesh.Clone();
                mesh.Name = $"renamed {step}";
                mesh.Hidden = !mesh.Hidden;
                mesh.Locked = !mesh.Locked;
                mesh.Collision = CollisionMode.Only;
                doc.Meshes[index] = mesh;
                break;
            case 8:
                foreach (var m in doc.Meshes)
                    for (int i = 0; i < m.Positions.Count; i++) m.Positions[i] += Vector3.UnitX;
                break;
            case 9: break; // Empty steps retain the original Dirty/Version semantics.
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HistoryIsCappedAndNewEditsClearRedo(bool reference)
    {
        var s = Create(StressLevel.Generate(1, entities: 0), reference);
        for (int i = 0; i < 270; i++) s.Do("Move", [1], () => s.Doc.Meshes[0].Positions[0] += Vector3.UnitY);
        for (int i = 0; i < 256; i++) Assert.True(s.Undo());
        Assert.False(s.Undo());
        for (int i = 0; i < 256; i++) Assert.True(s.Redo());
        Assert.False(s.Redo());
        Assert.True(s.Undo());
        s.Do("Other", [1], () => s.Doc.Meshes[0].Name = "other");
        Assert.False(s.Redo());
        Assert.Equal("Other", s.UndoLabel);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void ScopeViolationsAreLoudIncludingRemovedAndReplacedMeshes(int change)
    {
        var s = Create(StressLevel.Generate(2, entities: 0));
        s.Begin("Bad scope", [s.Doc.Meshes[0].Id]);
        var other = s.Doc.Meshes[1];
        if (change == 0) other.Faces[0].Material = "CASE-CHANGE";
        if (change == 1) { other.Hidden = true; s.Doc.Meshes.Remove(other); }
        if (change == 2) { var copy = other.Clone(); copy.Name = "replacement"; s.Doc.Meshes[1] = copy; }
        if (change == 3) other.Positions[0] += Vector3.One;
        if (change == 4) other.Faces[0].Material = other.Faces[0].Material.ToUpperInvariant();
        Assert.Contains("scope violation", Assert.Throws<InvalidOperationException>(() => s.End()).Message);
        Assert.Throws<InvalidOperationException>(() => s.Cancel());
    }

    [Fact]
    public void CaseOnlyAndSignedZeroEditsAreRetainedExactly()
    {
        var s = Create(StressLevel.Generate(1, entities: 0));
        s.Doc.Meshes[0].Faces[0].Material = "brick";
        s.Doc.Meshes[0].Positions[0] = Vector3.Zero;
        string before = DocumentSerializer.Serialize(s.Doc);
        s.Do("Exact", [1], () =>
        {
            s.Doc.Meshes[0].Faces[0].Material = "BRICK";
            s.Doc.Meshes[0].Positions[0] = new Vector3(-0f, 0, 0);
        });
        string after = DocumentSerializer.Serialize(s.Doc);
        Assert.NotEqual(before, after);
        s.Undo(); Assert.Equal(before, DocumentSerializer.Serialize(s.Doc));
        s.Redo(); Assert.Equal(after, DocumentSerializer.Serialize(s.Doc));
    }

    [Fact]
    public void ExceptionsCancelAndNestedBeginCommitsPreviousStep()
    {
        var s = Create(StressLevel.Generate(2, entities: 2));
        string before = DocumentSerializer.Serialize(s.Doc);
        Assert.Throws<ArgumentException>(() => s.Do("Fail", [1], () =>
        {
            s.Doc.Meshes[0].Faces[0].Verts[0] = 123;
            s.Doc.Entities[0].Properties.Clear();
            throw new ArgumentException("test");
        }));
        Assert.Equal(before, DocumentSerializer.Serialize(s.Doc));
        Assert.False(s.Dirty);
        Assert.False(s.InGesture);
        s.Begin("First", [1]); s.Doc.Meshes[0].Name = "first";
        s.Begin("Second", [2]); s.Doc.Meshes[1].Name = "second"; s.End();
        Assert.Equal("Second", s.UndoLabel);
        s.Undo(); Assert.Equal("First", s.UndoLabel);
        s.Undo(); Assert.Equal(before, DocumentSerializer.Serialize(s.Doc));
    }

    [Fact]
    public void RevisionsFollowScopedUndoRedoCancelAndStructuralChanges()
    {
        var s = Create(StressLevel.Generate(2, entities: 0));
        int a = s.Doc.Meshes[0].Id, b = s.Doc.Meshes[1].Id;
        int revision = s.MeshRevision(s.Doc.Find(a)!), stable = s.MeshRevision(s.Doc.Find(b)!);
        s.Do("Move", [a], () => s.Doc.Find(a)!.Positions[0] += Vector3.One);
        int edited = s.MeshRevision(s.Doc.Find(a)!);
        Assert.NotEqual(revision, edited);
        s.Undo(); int undone = s.MeshRevision(s.Doc.Find(a)!); Assert.NotEqual(edited, undone);
        s.Redo(); Assert.NotEqual(undone, s.MeshRevision(s.Doc.Find(a)!));
        s.Begin("Cancel", [a]); s.Doc.Find(a)!.Positions[0] += Vector3.One; s.Touch();
        int live = s.MeshRevision(s.Doc.Find(a)!);
        s.Cancel(); Assert.NotEqual(live, s.MeshRevision(s.Doc.Find(a)!));
        Assert.Equal(stable, s.MeshRevision(s.Doc.Find(b)!));
        var old = s.Doc.Find(a)!;
        s.Do("Delete", [a], () => s.Doc.Meshes.Remove(old));
        Assert.Equal(-1, s.MeshRevision(old));
        s.Undo(); Assert.True(s.MeshRevision(s.Doc.Find(a)!) >= 0);
        Assert.Equal(stable, s.MeshRevision(s.Doc.Find(b)!));
    }
}

public class UndoScopeReportingTests
{
    [Fact]
    public void ViolationGoesToTheHandlerWhenOneIsSet()
    {
        var doc = new Document();
        var a = doc.Add(MeshOps.CreateBox(Vector3.Zero, Vector3.One, "m", TextureMapping.Default));
        var b = doc.Add(MeshOps.CreateBox(new Vector3(2, 0, 0), new Vector3(3, 1, 1), "m", TextureMapping.Default));
        string? reported = null;
        var s = new Session { ValidateUndoScopes = true, ScopeViolation = m => reported = m };
        s.Reset(doc, null);

        s.Do("Sneaky", [a.Id], () => s.Doc.Find(b.Id)!.Positions[0] += Vector3.UnitY);

        Assert.Contains("Sneaky", reported);
        Assert.Equal("Sneaky", s.UndoLabel); // the step still commits; the editor keeps running
    }
}
