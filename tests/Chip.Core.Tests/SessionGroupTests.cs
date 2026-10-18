using System.Numerics;
using Chip.Core.Geometry;
using Chip.Core.Scene;
using Chip.Core.Texturing;

namespace Chip.Core.Tests;

public class SessionGroupTests
{
    [Fact]
    public void RemovedObjectsLeaveTheirGroupAndUndoPutsThemBack()
    {
        var doc = new Document();
        var a = doc.Add(MeshOps.CreateBox(Vector3.Zero, Vector3.One, "m", TextureMapping.Default));
        var b = doc.Add(MeshOps.CreateBox(new Vector3(2, 0, 0), new Vector3(3, 1, 1), "m", TextureMapping.Default));
        var g = doc.Add(new Group { Members = [a.Id, b.Id] });
        var s = new Session();
        s.Reset(doc, null);

        // Merge-style edit: b's geometry joins a and b disappears, without the op knowing about groups.
        s.Do("Merge Meshes", [a.Id, b.Id], () =>
        {
            MeshOps.MergeMeshes(s.Doc.Find(a.Id)!, [s.Doc.Find(b.Id)!]);
            s.Doc.Meshes.RemoveAll(m => m.Id == b.Id);
        });
        Assert.Equal([a.Id], s.Doc.FindGroup(g.Id)!.Members);

        s.Undo();
        Assert.Equal([a.Id, b.Id], s.Doc.FindGroup(g.Id)!.Members);
        s.Redo();
        Assert.Equal([a.Id], s.Doc.FindGroup(g.Id)!.Members);
    }
}
