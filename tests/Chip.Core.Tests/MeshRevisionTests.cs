using System.Numerics;
using Chip.Core.Diagnostics;
using Chip.Core.Geometry;
using Chip.Core.Scene;
using Chip.Core.Texturing;

namespace Chip.Core.Tests;

public class MeshRevisionTests
{
    static (Session S, PolyMesh A, PolyMesh B) Two()
    {
        var doc = new Document();
        var a = doc.Add(MeshOps.CreateBox(Vector3.Zero, Vector3.One, "m", TextureMapping.Default));
        var b = doc.Add(MeshOps.CreateBox(new Vector3(3, 0, 0), new Vector3(4, 1, 1), "m", TextureMapping.Default));
        var s = new Session();
        s.Reset(doc, null);
        return (s, a, b);
    }

    [Fact]
    public void OnlyTheEditedMeshChangesRevision()
    {
        var (s, a, b) = Two();
        int ra = s.MeshRevision(a), rb = s.MeshRevision(b);
        Assert.NotEqual(ra, rb);

        s.Do("Move", () => s.Doc.Find(a.Id)!.Positions[0] += Vector3.UnitY);

        Assert.NotEqual(ra, s.MeshRevision(s.Doc.Find(a.Id)!));
        Assert.Equal(rb, s.MeshRevision(s.Doc.Find(b.Id)!));
    }

    [Fact]
    public void StableWithoutEditsAndAcrossCloneSwaps()
    {
        var (s, a, b) = Two();
        int rb = s.MeshRevision(b);
        Assert.Equal(rb, s.MeshRevision(b));

        // Undo swaps in a cloned document: same content keeps its revision, the reverted mesh gets a new one.
        int before = s.MeshRevision(a);
        s.Do("Move", () => s.Doc.Find(a.Id)!.Positions[0] += Vector3.UnitY);
        int moved = s.MeshRevision(s.Doc.Find(a.Id)!);
        s.Undo();
        Assert.Equal(rb, s.MeshRevision(s.Doc.Find(b.Id)!));
        int undone = s.MeshRevision(s.Doc.Find(a.Id)!);
        Assert.NotEqual(moved, undone);
        Assert.NotEqual(before, undone);
    }

    [Fact]
    public void MeshesOutsideTheDocumentHaveNoRevision()
    {
        var (s, a, _) = Two();
        Assert.Equal(-1, s.MeshRevision(a.Clone()));
        s.Do("Delete", () => s.Doc.Meshes.RemoveAll(m => m.Id == a.Id));
        Assert.Equal(-1, s.MeshRevision(a));
    }

    [Fact]
    public void StressLevelHitsThePhase2Target()
    {
        var doc = StressLevel.Generate();
        Assert.Equal(StressLevel.TargetMeshes, doc.Meshes.Count);
        Assert.InRange(StressLevel.FaceCount(doc), 90_000, 110_000);
        Assert.Equal(StressLevel.FaceCount(doc), StressLevel.FaceCount(StressLevel.Generate()));
    }
}
