using Chip.Core.Geometry;
using Chip.Core.Picking;

namespace Chip.Core.Scene;

/// <summary>A named snapshot of a selection (mode, elements, active element). Kept for the session; not saved in the file.</summary>
public sealed record SelectionSet(string Name, SelectMode Mode, ElementRef[] Items, ElementRef? Active)
{
    public static SelectionSet Capture(Selection sel, string name) => new(name, sel.Mode, [.. sel.Items], sel.Active);

    /// <summary>Replaces the selection with this set, dropping elements that no longer resolve. Returns how many came back.</summary>
    public int RestoreInto(Selection sel, Document doc)
    {
        SelectionOps.Replace(sel, Mode, Items, Active);
        sel.Prune(doc);
        return sel.Count;
    }
}

/// <summary>
/// Selection power tools over a <see cref="Selection"/>: flood fill, faces on plane, edge loop/ring, grow/shrink, shortest
/// path, select by material and select connected. Mesh walks live in <see cref="SelectionTopology"/>.
/// <para>Active element rules (other tools rely on these): a click, flood or path makes the clicked element active; loop,
/// ring, grow, shrink and connected keep the active element while it stays selected; select-by-material makes the reference
/// face active. Anything else leaves it null.</para>
/// </summary>
public static class SelectionOps
{
    /// <summary>Angle tolerance for "faces on plane".</summary>
    public const float CoplanarDegrees = 1f;

    /// <summary>Sets mode and elements in one go, without mode conversion. <paramref name="active"/> must be in <paramref name="items"/> to stick.</summary>
    public static void Replace(Selection sel, SelectMode mode, IEnumerable<ElementRef> items, ElementRef? active)
    {
        sel.Clear();
        sel.ConvertTo(mode, new Document()); // empty selection: only the mode changes
        sel.Items.UnionWith(items);
        sel.Active = active is { } a && sel.Items.Contains(a) ? a : null;
    }

    /// <summary>
    /// What a double-click on <paramref name="clicked"/> selects: faces → connected faces (or connected faces on the same plane
    /// when <paramref name="coplanar"/>), edges → the edge loop, vertices → connected vertices. Objects → just the object.
    /// </summary>
    public static List<ElementRef> Flood(Document doc, ElementRef clicked, SelectMode mode, bool coplanar = false)
    {
        if (doc.Find(clicked.Mesh) is not { } m || mode == SelectMode.Object) return [clicked];
        var topo = new SelectionTopology(m);
        return mode switch
        {
            SelectMode.Face => (coplanar ? topo.CoplanarFaces(clicked.A, CoplanarDegrees) : topo.ConnectedFaces(clicked.A))
                .Select(f => ElementRef.Face(m.Id, f)).ToList(),
            SelectMode.Edge => topo.EdgeLoop(clicked.AsEdge).Select(e => ElementRef.EdgeOf(m.Id, e)).ToList(),
            _ => topo.ConnectedVertices([clicked.A]).Order().Select(v => ElementRef.Vertex(m.Id, v)).ToList(),
        };
    }

    /// <summary>Adds the loop through every selected edge. Returns how many edges were added.</summary>
    public static int SelectLoops(Document doc, Selection sel) => ExpandEdges(doc, sel, (t, e) => t.EdgeLoop(e));

    /// <summary>Adds the ring through every selected edge. Returns how many edges were added.</summary>
    public static int SelectRings(Document doc, Selection sel) => ExpandEdges(doc, sel, (t, e) => t.EdgeRing(e));

    static int ExpandEdges(Document doc, Selection sel, Func<SelectionTopology, Edge, List<Edge>> walk)
    {
        if (sel.Mode != SelectMode.Edge) return 0;
        int before = sel.Count;
        foreach (var g in sel.Items.GroupBy(i => i.Mesh).ToList())
        {
            if (doc.Find(g.Key) is not { } m) continue;
            var topo = new SelectionTopology(m);
            foreach (var item in g)
                sel.Items.UnionWith(walk(topo, item.AsEdge).Select(e => ElementRef.EdgeOf(m.Id, e)));
        }
        return sel.Count - before;
    }

    /// <summary>Grows the selection by one step: faces across shared edges, vertices and edges along edges.</summary>
    public static void Grow(Document doc, Selection sel) => Step(doc, sel, grow: true);

    /// <summary>Shrinks the selection by one step, peeling its rim (open borders count as rim).</summary>
    public static void Shrink(Document doc, Selection sel) => Step(doc, sel, grow: false);

    static void Step(Document doc, Selection sel, bool grow)
    {
        if (sel.Mode == SelectMode.Object) return;
        var result = new List<ElementRef>();
        foreach (var g in sel.Items.GroupBy(i => i.Mesh))
        {
            if (doc.Find(g.Key) is not { } m) continue;
            var topo = new SelectionTopology(m);
            switch (sel.Mode)
            {
                case SelectMode.Face:
                    var faces = g.Select(i => i.A).ToHashSet();
                    result.AddRange((grow ? topo.GrowFaces(faces) : topo.ShrinkFaces(faces)).Select(f => ElementRef.Face(m.Id, f)));
                    break;
                case SelectMode.Vertex:
                    var verts = g.Select(i => i.A).ToHashSet();
                    result.AddRange((grow ? topo.GrowVertices(verts) : topo.ShrinkVertices(verts)).Select(v => ElementRef.Vertex(m.Id, v)));
                    break;
                case SelectMode.Edge:
                    var edges = g.Select(i => i.AsEdge).ToHashSet();
                    result.AddRange((grow ? topo.GrowEdges(edges) : topo.ShrinkEdges(edges)).Select(e => ElementRef.EdgeOf(m.Id, e)));
                    break;
            }
        }
        Replace(sel, sel.Mode, result, sel.Active);
    }

    /// <summary>
    /// The shortest path from <paramref name="from"/> to <paramref name="to"/> in the same mesh: by edge length for vertices and
    /// edges, by face adjacency for faces. Null if they're in different meshes, unconnected, or in object mode.
    /// </summary>
    public static List<ElementRef>? Path(Document doc, ElementRef from, ElementRef to, SelectMode mode)
    {
        if (from.Mesh != to.Mesh || mode == SelectMode.Object || doc.Find(from.Mesh) is not { } m) return null;
        var topo = new SelectionTopology(m);
        return mode switch
        {
            SelectMode.Vertex => topo.VertexPath([from.A], [to.A])?.Select(v => ElementRef.Vertex(m.Id, v)).ToList(),
            SelectMode.Edge => topo.EdgePath(from.AsEdge, to.AsEdge)?.Select(e => ElementRef.EdgeOf(m.Id, e)).ToList(),
            _ => topo.FacePath(from.A, to.A)?.Select(f => ElementRef.Face(m.Id, f)).ToList(),
        };
    }

    /// <summary>
    /// Adds the path from the active element to <paramref name="target"/> and makes the target active, so repeated clicks chain.
    /// With no usable active element it just adds the target. Returns whether a path was found.
    /// </summary>
    public static bool SelectPath(Document doc, Selection sel, ElementRef target)
    {
        var path = sel.Active is { } a ? Path(doc, a, target, sel.Mode) : null;
        sel.Items.UnionWith(path ?? [target]);
        sel.Active = target;
        return path != null;
    }

    /// <summary>The material of the active face, if the active element is a face.</summary>
    public static string? ActiveFaceMaterial(Document doc, Selection sel) =>
        sel.Mode == SelectMode.Face && sel.Active is { } a && doc.Find(a.Mesh) is { } m && a.A < m.Faces.Count ? m.Faces[a.A].Material : null;

    /// <summary>Switches to face mode and selects every visible, unlocked face using <paramref name="material"/>. Returns the count.</summary>
    public static int SelectByMaterial(Document doc, Selection sel, string material)
    {
        var faces = new List<ElementRef>();
        foreach (var m in doc.Meshes.Where(m => !m.Hidden && !m.Locked))
            for (int f = 0; f < m.Faces.Count; f++)
                if (m.Faces[f].Material == material) faces.Add(ElementRef.Face(m.Id, f));
        Replace(sel, SelectMode.Face, faces, sel.Mode == SelectMode.Face ? sel.Active : null);
        return faces.Count;
    }

    /// <summary>Extends the selection to everything connected to it (faces across shared edges, vertices and edges along edges).</summary>
    public static void SelectConnected(Document doc, Selection sel)
    {
        if (sel.Mode == SelectMode.Object) return;
        var result = new List<ElementRef>();
        foreach (var g in sel.Items.GroupBy(i => i.Mesh))
        {
            if (doc.Find(g.Key) is not { } m) continue;
            var topo = new SelectionTopology(m);
            switch (sel.Mode)
            {
                case SelectMode.Face:
                    var faces = new HashSet<int>();
                    foreach (var i in g)
                        if (!faces.Contains(i.A)) faces.UnionWith(topo.ConnectedFaces(i.A));
                    result.AddRange(faces.Select(f => ElementRef.Face(m.Id, f)));
                    break;
                case SelectMode.Vertex:
                    result.AddRange(topo.ConnectedVertices(g.Select(i => i.A)).Select(v => ElementRef.Vertex(m.Id, v)));
                    break;
                case SelectMode.Edge:
                    var verts = topo.ConnectedVertices(g.SelectMany(i => new[] { i.A, i.B }));
                    result.AddRange(m.Edges().Where(e => verts.Contains(e.A)).Select(e => ElementRef.EdgeOf(m.Id, e)));
                    break;
            }
        }
        Replace(sel, sel.Mode, result, sel.Active);
    }
}
