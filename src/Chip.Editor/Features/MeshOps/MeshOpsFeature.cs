using System.Numerics;
using Chip.Core.Geometry;
using Chip.Core.Scene;
using Chip.Core.Texturing;
using Chip.Editor.Framework;
using Ops = Chip.Core.Geometry.MeshOps;

namespace Chip.Editor.Features.MeshEditing;

/// <summary>
/// Stream A, mesh operations (SPEC §6): connect, merge faces, bridge, inset/outset, collapse, weld, merge meshes,
/// detach/extract and mirror. Push/pull and edge extrude are gizmo gestures (<see cref="TransformGesture"/>), listed on
/// the shortcuts sheet here. Every command is one undo step; a command that can't do anything leaves no step behind.
/// </summary>
public sealed class MeshOpsFeature : IEditorFeature
{
    EditorContext _ctx = null!;
    readonly InsetTool _inset = new();
    readonly OffsetTool _offset = new();
    readonly ArcExtrudeTool _arc = new();

    Selection Sel => _ctx.Sel;
    Document Doc => _ctx.Doc;

    public void Register(EditorContext ctx)
    {
        _ctx = ctx;
        var c = ctx.Commands;
        bool Mode(SelectMode m, int min = 1) => Sel.Mode == m && Sel.Count >= min;

        c.Add("mesh.connect", "Connect", Connect, "Mesh", "V",
            () => Mode(SelectMode.Vertex, 2) || Mode(SelectMode.Edge), "1-connect", 0);
        c.Add("mesh.mergeFaces", "Merge faces", MergeFaces, "Mesh", "Backspace", () => Mode(SelectMode.Face, 2), "1-connect", 1);
        c.Add("mesh.bridge", "Bridge", Bridge, "Mesh", "Shift+C",
            () => (Sel.Mode == SelectMode.Face && Sel.Count == 2) || Mode(SelectMode.Edge, 2), "1-connect", 2);
        c.Add("mesh.fill", "Fill holes", Fill, "Mesh", "Alt+F",
            () => Mode(SelectMode.Edge) || Mode(SelectMode.Vertex) || (Sel.Mode == SelectMode.Object && Sel.MeshIds().Any(id => Doc.Find(id) != null)),
            "1-connect", 3);

        c.Add("mesh.inset", "Inset", () => ctx.SetTool(_inset.For(outset: false)), "Mesh", "I", () => Mode(SelectMode.Face), "2-inset", 0);
        c.Add("mesh.outset", "Outset", () => ctx.SetTool(_inset.For(outset: true)), "Mesh", "O", () => Mode(SelectMode.Face), "2-inset", 1);
        c.Add("mesh.offset", "Offset faces", () => ctx.SetTool(_offset), "Mesh", "Shift+O", () => Mode(SelectMode.Face), "2-inset", 2);
        c.Add("mesh.arcExtrude", "Arc extrude", () => ctx.SetTool(_arc), "Mesh", "Shift+A",
            () => Mode(SelectMode.Face) || Mode(SelectMode.Edge), "2-inset", 3);

        c.Add("mesh.collapse", "Collapse", Collapse, "Mesh", null,
            () => !Sel.IsEmpty && Sel.Mode != SelectMode.Object, "3-weld", 0);
        c.Add("mesh.weld", "Weld overlapping vertices", Weld, "Mesh", "Ctrl+Shift+Alt+M", () => !Sel.IsEmpty, "3-weld", 1);

        c.Add("mesh.mergeMeshes", "Merge meshes", MergeMeshes, "Mesh", "M",
            () => Sel.Mode == SelectMode.Object && Sel.MeshIds().Count(id => Doc.Find(id) != null) >= 2, "4-meshes", 0);
        c.Add("mesh.detach", "Detach faces", () => Detach(extract: false), "Mesh", "N", () => Mode(SelectMode.Face), "4-meshes", 1);
        c.Add("mesh.extract", "Extract faces", () => Detach(extract: true), "Mesh", "Alt+N", () => Mode(SelectMode.Face), "4-meshes", 2);
        c.Add("mesh.mirror", "Mirror", Mirror, "Mesh", "Shift+Z", () => !Sel.IsEmpty, "4-meshes", 3);

        c.AddReference("Mesh", "Ctrl+Shift + drag", "push/pull faces");
        c.AddReference("Mesh", "Shift + drag edges", "extrude open edges");
        c.AddReference("Mesh", "Shift + drag rotate gizmo", "extrude round the pivot (MMB-drag the gizmo to move it)");
    }

    /// <summary>
    /// Runs one edit as an undo step. <paramref name="edit"/> returns null on success (and reports its own status), or a
    /// message saying why nothing happened, in which case the step is rolled back and the message shown as an error.
    /// </summary>
    void Edit(string label, Func<string?> edit)
    {
        _ctx.Session.Begin(label, Sel.MeshIds());
        string? error;
        try { error = edit(); }
        catch { _ctx.Session.Cancel(); throw; }
        if (error != null)
        {
            _ctx.Session.Cancel();
            _ctx.Status(error, error: true);
            return;
        }
        _ctx.Session.End();
    }

    IEnumerable<(PolyMesh Mesh, List<ElementRef> Items)> ByMesh() =>
        Sel.Items.GroupBy(i => i.Mesh).Select(g => (Doc.Find(g.Key), g.ToList())).Where(x => x.Item1 != null).Select(x => (x.Item1!, x.Item2)).ToList();

    void Select(IEnumerable<ElementRef> items)
    {
        Sel.Clear();
        foreach (var i in items) Sel.Add(i);
    }

    static string Plural(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";

    // ---- Connect / merge / bridge ----

    void Connect() => Edit("Connect", () =>
    {
        var chords = new List<ElementRef>();
        foreach (var (mesh, items) in ByMesh())
        {
            var made = Sel.Mode == SelectMode.Vertex
                ? Ops.ConnectVertices(mesh, items.Select(i => i.A))
                : Ops.ConnectEdges(mesh, items.Select(i => i.AsEdge));
            chords.AddRange(made.Select(e => ElementRef.EdgeOf(mesh.Id, e)));
        }
        if (chords.Count == 0)
            return Sel.Mode == SelectMode.Vertex
                ? "Nothing to connect: pick two or more vertices on the same face that aren't already joined by an edge"
                : "Nothing to connect: pick two or more edges around the same face";
        if (Sel.Mode == SelectMode.Edge) Select(chords);
        _ctx.Status($"Connected with {Plural(chords.Count, "new edge")}");
        return null;
    });

    void MergeFaces() => Edit("Merge Faces", () =>
    {
        var merged = new List<ElementRef>();
        int skipped = 0;
        foreach (var (mesh, faces) in Sel.FaceSets(Doc))
        {
            int? keep = Sel.Active is { } a && a.Mesh == mesh.Id ? a.A : null;
            var r = Ops.MergeFaces(mesh, faces, keep);
            skipped += r.Skipped;
            merged.AddRange(r.Merged.Select(f => ElementRef.Face(mesh.Id, f)));
        }
        if (merged.Count == 0)
            return skipped > 0
                ? "Can't merge: the faces would enclose a hole or meet only at a corner"
                : "Select two or more faces that share edges to merge them";
        Select(merged);
        _ctx.Status($"Merged into {Plural(merged.Count, "face")}{(skipped > 0 ? $"; skipped {skipped} region(s) with holes" : "")}");
        return null;
    });

    void Bridge() => Edit("Bridge", () =>
    {
        var groups = ByMesh().ToList();
        if (groups.Count != 1) return "Bridge works within one mesh: merge the meshes first (M)";
        var (mesh, items) = groups[0];
        string? error;
        if (Sel.Mode == SelectMode.Face)
        {
            if (Ops.BridgeFaces(mesh, items[0].A, items[1].A, out error) is not { } quads) return error;
            Select(quads.Select(f => ElementRef.Face(mesh.Id, f)));
            _ctx.Status($"Bridged with {Plural(quads.Count, "face")}");
        }
        else
        {
            if (Ops.BridgeEdges(mesh, items.Select(i => i.AsEdge), out error) is not { } r) return error;
            if (r.VertexMap is { } vmap) Sel.Remap(mesh.Id, vmap, r.FaceMap!);
            _ctx.Status(r.Bridges == 1 ? $"Bridged with {Plural(r.Faces.Count, "face")}" : $"Made {r.Bridges} bridges with {Plural(r.Faces.Count, "face")}");
        }
        return null;
    });

    /// <summary>Caps the holes the selection touches: edges or vertices on a hole's rim, or every hole of selected objects.</summary>
    void Fill() => Edit("Fill", () =>
    {
        var filled = new List<ElementRef>();
        int skipped = 0, nonPlanar = 0;
        foreach (var (mesh, items) in ByMesh())
        {
            IEnumerable<Edge>? rim = null;
            if (Sel.Mode == SelectMode.Edge) rim = items.Select(i => i.AsEdge);
            else if (Sel.Mode == SelectMode.Vertex)
            {
                var verts = items.Select(i => i.A).ToHashSet();
                rim = mesh.BoundaryEdges().Where(e => verts.Contains(e.A) || verts.Contains(e.B));
            }
            var r = Ops.FillHoles(mesh, rim);
            skipped += r.Skipped;
            nonPlanar += r.NonPlanar;
            filled.AddRange(r.Faces.Select(f => ElementRef.Face(mesh.Id, f)));
        }
        if (filled.Count == 0)
            return skipped > 0
                ? "Can't fill: the hole's outline crosses itself or its edges are wound inconsistently"
                : Sel.Mode == SelectMode.Object ? "No holes to fill" : "Nothing to fill: pick an open edge or vertex on the rim of a hole";
        if (Sel.Mode != SelectMode.Object)
        {
            _ctx.SetMode(SelectMode.Face);
            Select(filled);
        }
        var notes = new List<string>();
        if (nonPlanar > 0) notes.Add($"{Plural(nonPlanar, "fill")} not flat");
        if (skipped > 0) notes.Add($"skipped {Plural(skipped, "hole")} that can't be filled");
        _ctx.Status($"Filled {Plural(filled.Count, "hole")}{(notes.Count > 0 ? $" ({string.Join("; ", notes)})" : "")}");
        return null;
    });

    // ---- Collapse / weld ----

    void Collapse() => Edit("Collapse", () =>
    {
        var points = new List<ElementRef>();
        foreach (var (mesh, items) in ByMesh())
        {
            List<List<int>> groups = Sel.Mode switch
            {
                SelectMode.Vertex => [items.Select(i => i.A).ToList()],
                SelectMode.Edge => Components(items.Select(i => new[] { i.A, i.B })),
                _ => Components(items.Select(i => mesh.Faces[i.A].Verts)),
            };
            var (_, _, kept) = Ops.Collapse(mesh, groups);
            points.AddRange(kept.Select(v => ElementRef.Vertex(mesh.Id, v)));
        }
        Doc.Meshes.RemoveAll(m => m.Faces.Count == 0);
        if (Sel.Mode == SelectMode.Vertex) Select(points);
        else Sel.Clear();
        _ctx.Status($"Collapsed to {Plural(points.Count, "point")}");
        return null;
    });

    /// <summary>Groups vertex sets that share a vertex (connected edges or faces collapse together).</summary>
    static List<List<int>> Components(IEnumerable<int[]> sets)
    {
        var parent = new Dictionary<int, int>();
        int Find(int x)
        {
            if (!parent.TryGetValue(x, out int p)) return parent[x] = x;
            return p == x ? x : parent[x] = Find(p);
        }
        foreach (var s in sets)
            for (int i = 1; i < s.Length; i++) parent[Find(s[i])] = Find(s[0]);
        foreach (var s in sets) foreach (var v in s) Find(v);
        return parent.Keys.GroupBy(Find).Select(g => g.ToList()).ToList();
    }

    void Weld() => Edit("Weld Overlapping", () =>
    {
        int welded = 0;
        var verts = Sel.Mode == SelectMode.Vertex ? Sel.VertexSets(Doc) : null;
        foreach (var id in Sel.MeshIds())
        {
            if (Doc.Find(id) is not { } mesh) continue;
            if (Ops.WeldOverlapping(mesh, Ops.WeldDistance, verts?.GetValueOrDefault(mesh)) is not { } r) continue;
            welded += r.Welded;
            Sel.Remap(mesh.Id, r.VertexMap, r.FaceMap);
        }
        if (welded == 0) return "No vertices closer than 1 mm to weld";
        _ctx.Status(welded == 1 ? "Welded 1 vertex" : $"Welded {welded} vertices");
        return null;
    });

    // ---- Meshes ----

    void MergeMeshes() => Edit("Merge Meshes", () =>
    {
        var ids = Sel.MeshIds();
        var meshes = Doc.Meshes.Where(m => ids.Contains(m.Id)).ToList();
        var target = Sel.Active is { } a && Doc.Find(a.Mesh) is { } active ? active : meshes[0];
        var others = meshes.Where(m => m != target).ToList();
        Ops.MergeMeshes(target, others);
        Doc.Meshes.RemoveAll(others.Contains);
        Select([ElementRef.Object(target.Id)]);
        _ctx.Status($"Merged {meshes.Count} meshes into {target.Name}");
        return null;
    });

    void Detach(bool extract) => Edit(extract ? "Extract Faces" : "Detach Faces", () =>
    {
        var copies = new List<ElementRef>();
        foreach (var (mesh, faces) in Sel.FaceSets(Doc))
        {
            var copy = Doc.Add(Ops.CopyFaces(mesh, faces));
            copies.AddRange(Enumerable.Range(0, copy.Faces.Count).Select(f => ElementRef.Face(copy.Id, f)));
            Ops.DeleteFaces(mesh, faces);
        }
        Doc.Meshes.RemoveAll(m => m.Faces.Count == 0);
        Select(copies);
        _ctx.Status($"{(extract ? "Extracted" : "Detached")} {Plural(copies.Count, "face")} into a new mesh");
        return null;
    });

    /// <summary>Mirrors the selection across its pivot, along the horizontal axis closest to the view's left-right.</summary>
    void Mirror() => Edit("Mirror", () =>
    {
        if (_ctx.SelectionPivot() is not { } pivot) return "Select something to mirror";
        var right = _ctx.Camera.Right;
        var axis = Vector3.Abs(TextureMapping.DominantAxis(right with { Y = 0 }));
        if (axis.LengthSquared() < 0.5f) axis = Vector3.UnitX;
        foreach (var (mesh, verts) in Sel.VertexSets(Doc)) Ops.Mirror(mesh, verts, pivot, axis);
        // Whole objects take their origins across with them (a mirror isn't a rotation, so the axes stay as they were).
        if (Sel.Mode == SelectMode.Object)
            foreach (var t in OriginOps.Targets(Doc, Sel))
                if (t.Origin is { } o) t.Origin = o with { Position = o.Position - 2 * axis * Vector3.Dot(o.Position - pivot, axis) };
        _ctx.Status($"Mirrored along {(axis.X > 0 ? "X" : axis.Y > 0 ? "Y" : "Z")}");
        return null;
    });
}
