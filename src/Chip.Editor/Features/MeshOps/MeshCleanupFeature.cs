using Chip.Core.Geometry;
using Chip.Core.Scene;
using Chip.Editor.Framework;
using Ops = Chip.Core.Geometry.MeshOps;

namespace Chip.Editor.Features.MeshEditing;

/// <summary>
/// Mesh hygiene: dissolve vertices and edges without losing the faces round them, check meshes for broken topology
/// (selecting what's wrong), and clean them up in one step. Every edit is one undo step; nothing to do leaves none.
/// </summary>
public sealed class MeshCleanupFeature : IEditorFeature
{
    EditorContext _ctx = null!;
    Selection Sel => _ctx.Sel;
    Document Doc => _ctx.Doc;

    public void Register(EditorContext ctx)
    {
        _ctx = ctx;
        var c = ctx.Commands;
        c.Add(new Command
        {
            Id = "mesh.dissolve", Label = "Dissolve", Execute = Dissolve, Menu = "Mesh", Group = "3-weld", Order = 2,
            Hotkeys = [Hotkey.Parse("Ctrl+Backspace")],
            CanExecute = () => Sel.Mode is SelectMode.Vertex or SelectMode.Edge && !Sel.IsEmpty,
            Description = "Remove the selected vertices or edges and keep the faces round them",
        });
        c.Add(new Command
        {
            Id = "mesh.cleanUp", Label = "Clean up mesh", Execute = CleanUp, Menu = "Mesh", Group = "3-weld", Order = 3,
            CanExecute = () => Sel.MeshIds().Any(id => Doc.Find(id) != null),
            Description = "Weld overlapping vertices, untangle self-crossing faces, sew T-junctions and dissolve stray straight-edge vertices",
        });
        c.Add(new Command
        {
            Id = "mesh.check", Label = "Check mesh", Execute = Check, Menu = "Mesh", Group = "3-weld", Order = 4,
            CanExecute = () => Doc.Meshes.Count > 0,
            Description = "Find broken faces, T-junctions and non-manifold edges in the selected meshes (all meshes if none), and select them",
        });
    }

    static string Plural(int n, string noun) => n == 1 ? $"1 {noun}" : noun.EndsWith("vertex") ? $"{n} {noun[..^2]}ices" : $"{n} {noun}s";

    /// <summary>Selected meshes (any mode), else every visible, unlocked mesh.</summary>
    List<PolyMesh> Targets()
    {
        var ids = Sel.MeshIds();
        var picked = Doc.Meshes.Where(m => ids.Contains(m.Id)).ToList();
        return picked.Count > 0 ? picked : Doc.Meshes.Where(m => !m.Hidden && !m.Locked).ToList();
    }

    void Dissolve()
    {
        int done = 0, skipped = 0;
        bool edges = Sel.Mode == SelectMode.Edge;
        _ctx.Session.Begin(edges ? "Dissolve Edges" : "Dissolve Vertices", Sel.MeshIds());
        foreach (var g in Sel.Items.GroupBy(i => i.Mesh).ToList())
        {
            if (Doc.Find(g.Key) is not { } mesh) continue;
            var r = edges ? Ops.DissolveEdges(mesh, g.Select(i => i.AsEdge)) : Ops.DissolveVertices(mesh, g.Select(i => i.A));
            done += r.Dissolved;
            skipped += r.Skipped;
            if (edges) Sel.Items.RemoveWhere(i => i.Mesh == mesh.Id); // the merged edges no longer exist
            else Sel.Remap(mesh.Id, r.VertexMap, r.FaceMap);
        }
        if (done == 0)
        {
            _ctx.Session.Cancel();
            _ctx.Status(edges
                ? "Nothing to dissolve: pick edges between two faces (an open edge has nothing to merge with)"
                : "Nothing to dissolve: where several edges meet, the faces round the vertex must lie in one plane", error: true);
            return;
        }
        _ctx.Session.End();
        _ctx.Status($"Dissolved {Plural(done, edges ? "edge" : "vertex")}{(skipped > 0 ? $"; skipped {skipped}" : "")}");
    }

    void CleanUp()
    {
        var meshes = Sel.MeshIds().Select(Doc.Find).OfType<PolyMesh>().ToList();
        int welded = 0, untangled = 0, sewn = 0, dissolved = 0, removed = 0;
        _ctx.Session.Begin("Clean Up Mesh", meshes.Select(m => m.Id));
        foreach (var mesh in meshes)
        {
            var r = Ops.CleanUp(mesh);
            if (!r.Changed) continue;
            welded += r.Welded; untangled += r.Untangled; sewn += r.TJunctions; dissolved += r.Dissolved; removed += r.RemovedFaces;
            if (Sel.Mode == SelectMode.Edge) Sel.Items.RemoveWhere(i => i.Mesh == mesh.Id);
            else Sel.Remap(mesh.Id, r.VertexMap, r.FaceMap);
        }
        if (welded + untangled + sewn + dissolved + removed == 0)
        {
            _ctx.Session.Cancel();
            _ctx.Status(meshes.Count == 1 ? "Nothing to clean up: the mesh is already tidy" : "Nothing to clean up: the meshes are already tidy");
            return;
        }
        _ctx.Session.End();
        var parts = new List<string>();
        if (welded > 0) parts.Add($"welded {Plural(welded, "vertex")}");
        if (untangled > 0) parts.Add($"untangled {Plural(untangled, "face")}");
        if (sewn > 0) parts.Add($"sewed {Plural(sewn, "T-junction")}");
        if (dissolved > 0) parts.Add($"dissolved {Plural(dissolved, "stray vertex")}");
        if (removed > 0) parts.Add($"removed {Plural(removed, "empty face")}");
        var text = string.Join(", ", parts);
        _ctx.Status(char.ToUpper(text[0]) + text[1..]);
    }

    void Check()
    {
        var found = Targets().Select(m => (Mesh: m, Issues: Ops.Check(m))).Where(x => x.Issues.Count > 0).ToList();
        var all = found.SelectMany(x => x.Issues).ToList();
        if (all.Count == 0)
        {
            _ctx.Status("No problems found");
            return;
        }

        // Select what's wrong, in the mode that shows it best: broken faces, else T-junction vertices, else edges.
        var faceKinds = new[] { MeshIssueKind.DegenerateFace, MeshIssueKind.RepeatedVertex, MeshIssueKind.SelfIntersectingFace };
        var mode = all.Any(i => faceKinds.Contains(i.Kind)) ? SelectMode.Face
            : all.Any(i => i.Kind == MeshIssueKind.TJunction) ? SelectMode.Vertex : SelectMode.Edge;
        _ctx.SetMode(mode);
        Sel.Clear();
        foreach (var (mesh, issues) in found)
            foreach (var i in issues)
            {
                if (mode == SelectMode.Face && faceKinds.Contains(i.Kind)) Sel.Add(ElementRef.Face(mesh.Id, i.Face));
                else if (mode == SelectMode.Vertex && i.Kind == MeshIssueKind.TJunction) Sel.Add(ElementRef.Vertex(mesh.Id, i.Vertex));
                else if (mode == SelectMode.Edge) Sel.Add(ElementRef.EdgeOf(mesh.Id, i.Edge));
            }

        var counts = all.GroupBy(i => i.Kind).OrderBy(g => g.Key).Select(g => Describe(g.Key, g.Count()));
        _ctx.Status($"Found {string.Join(", ", counts)}. Selected the {(mode == SelectMode.Face ? "faces" : mode == SelectMode.Vertex ? "vertices" : "edges")}; Mesh > Clean up mesh fixes most of it", error: true);
    }

    static string Describe(MeshIssueKind kind, int n) => kind switch
    {
        MeshIssueKind.DegenerateFace => Plural(n, "face") + " with no area",
        MeshIssueKind.RepeatedVertex => Plural(n, "pinched face"),
        MeshIssueKind.SelfIntersectingFace => Plural(n, "self-crossing face"),
        MeshIssueKind.TJunction => Plural(n, "T-junction"),
        MeshIssueKind.NonManifoldEdge => Plural(n, "edge") + " with more than two faces",
        _ => Plural(n, "edge") + " between faces wound opposite ways",
    };
}
