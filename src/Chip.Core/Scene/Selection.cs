using Chip.Core.Geometry;

namespace Chip.Core.Scene;

public enum SelectMode { Vertex, Edge, Face, Object }

/// <summary>
/// One selectable element. Vertex: A = vertex. Edge: A &lt; B vertex pair. Face: A = face. Object: a mesh or an entity
/// (ids are unique across both, so <see cref="Mesh"/> holds either; mesh-only code skips ids <c>Document.Find</c> can't resolve).
/// </summary>
public readonly record struct ElementRef(int Mesh, int A = -1, int B = -1)
{
    public static ElementRef Vertex(int mesh, int v) => new(mesh, v);
    public static ElementRef Face(int mesh, int f) => new(mesh, f);
    public static ElementRef Object(int mesh) => new(mesh);
    public static ElementRef EdgeOf(int mesh, Edge e) => new(mesh, e.A, e.B);
    public Edge AsEdge => new(A, B);
}

/// <summary>Mode plus the selected elements of that mode. Switching mode converts the selection.</summary>
public sealed class Selection
{
    public SelectMode Mode { get; private set; } = SelectMode.Object;
    public readonly HashSet<ElementRef> Items = new();
    /// <summary>Last element explicitly picked; the reference for inspector edits.</summary>
    public ElementRef? Active;

    public int Count => Items.Count;
    public bool IsEmpty => Items.Count == 0;

    public Selection Clone()
    {
        var s = new Selection { Mode = Mode, Active = Active };
        s.Items.UnionWith(Items);
        return s;
    }

    public void Clear() { Items.Clear(); Active = null; }

    public void Add(ElementRef e) { Items.Add(e); Active = e; }

    public void Toggle(ElementRef e)
    {
        if (!Items.Remove(e)) Add(e);
        else if (Active == e) Active = null;
    }

    public void SelectAll(Document doc)
    {
        foreach (var m in doc.Meshes.Where(m => !m.Hidden && !m.Locked))
            Items.UnionWith(Elements(m, Mode));
        if (Mode == SelectMode.Object) Items.UnionWith(SelectableEntities(doc));
    }

    public void Invert(Document doc)
    {
        var all = doc.Meshes.Where(m => !m.Hidden && !m.Locked).SelectMany(m => Elements(m, Mode)).ToHashSet();
        if (Mode == SelectMode.Object) all.UnionWith(SelectableEntities(doc));
        all.ExceptWith(Items);
        Clear();
        Items.UnionWith(all);
    }

    /// <summary>
    /// Converts to another mode. Vertices→edges/faces take fully enclosed elements, falling back to touching ones.
    /// Edges→faces take faces containing any selected edge. Anything→object takes owning meshes.
    /// Entities only exist in object mode: switching to a component mode drops them.
    /// </summary>
    public void ConvertTo(SelectMode mode, Document doc)
    {
        if (mode == Mode) return;
        var result = new HashSet<ElementRef>();
        foreach (var (mesh, items) in Items.GroupBy(i => i.Mesh).Select(g => (doc.Find(g.Key), g.ToList())))
        {
            if (mesh == null) continue;
            var verts = VerticesOf(mesh, items, Mode);
            var edges = Mode == SelectMode.Edge ? items.Select(i => i.AsEdge).ToHashSet() : null;
            switch (mode)
            {
                case SelectMode.Object:
                    result.Add(ElementRef.Object(mesh.Id));
                    break;
                case SelectMode.Vertex:
                    result.UnionWith(verts.Select(v => ElementRef.Vertex(mesh.Id, v)));
                    break;
                case SelectMode.Edge:
                    var meshEdges = mesh.Edges().ToList();
                    var enclosed = meshEdges.Where(e => verts.Contains(e.A) && verts.Contains(e.B)).ToList();
                    if (enclosed.Count == 0) enclosed = meshEdges.Where(e => verts.Contains(e.A) || verts.Contains(e.B)).ToList();
                    result.UnionWith(enclosed.Select(e => ElementRef.EdgeOf(mesh.Id, e)));
                    break;
                case SelectMode.Face:
                    var faces = new List<int>();
                    for (int f = 0; f < mesh.Faces.Count; f++)
                    {
                        var face = mesh.Faces[f];
                        bool hit = edges != null ? face.Edges().Any(edges.Contains) : face.Verts.All(verts.Contains);
                        if (hit) faces.Add(f);
                    }
                    if (faces.Count == 0 && edges == null)
                        for (int f = 0; f < mesh.Faces.Count; f++)
                            if (mesh.Faces[f].Verts.Any(verts.Contains)) faces.Add(f);
                    result.UnionWith(faces.Select(f => ElementRef.Face(mesh.Id, f)));
                    break;
            }
        }
        Mode = mode;
        Clear();
        Items.UnionWith(result);
    }

    /// <summary>Vertices affected by the selection, per mesh. This is what transforms move.</summary>
    public Dictionary<PolyMesh, HashSet<int>> VertexSets(Document doc)
    {
        var result = new Dictionary<PolyMesh, HashSet<int>>();
        foreach (var g in Items.GroupBy(i => i.Mesh))
            if (doc.Find(g.Key) is { } mesh)
                result[mesh] = VerticesOf(mesh, g.ToList(), Mode);
        return result;
    }

    /// <summary>Faces addressed by the selection: selected faces, or every face of selected objects.</summary>
    public Dictionary<PolyMesh, List<int>> FaceSets(Document doc)
    {
        var result = new Dictionary<PolyMesh, List<int>>();
        foreach (var g in Items.GroupBy(i => i.Mesh))
        {
            if (doc.Find(g.Key) is not { } mesh) continue;
            if (Mode == SelectMode.Face) result[mesh] = g.Select(i => i.A).Order().ToList();
            else if (Mode == SelectMode.Object) result[mesh] = Enumerable.Range(0, mesh.Faces.Count).ToList();
        }
        return result;
    }

    public HashSet<int> MeshIds() => Items.Select(i => i.Mesh).ToHashSet();

    /// <summary>Selected entities (object mode only), in document order.</summary>
    public List<Entity> Entities(Document doc)
    {
        if (Mode != SelectMode.Object || Items.Count == 0) return [];
        var ids = MeshIds();
        return doc.Entities.Where(e => ids.Contains(e.Id)).ToList();
    }

    static IEnumerable<ElementRef> SelectableEntities(Document doc) =>
        doc.Entities.Where(e => !e.Hidden && !e.Locked).Select(e => ElementRef.Object(e.Id));

    /// <summary>Re-points one mesh's element references after its vertices/faces were renumbered (-1 = removed).</summary>
    public void Remap(int meshId, int[] vertexMap, int[] faceMap)
    {
        ElementRef? Map(ElementRef e) => Mode switch
        {
            SelectMode.Vertex => vertexMap[e.A] < 0 ? null : ElementRef.Vertex(meshId, vertexMap[e.A]),
            SelectMode.Edge => vertexMap[e.A] < 0 || vertexMap[e.B] < 0 || vertexMap[e.A] == vertexMap[e.B]
                ? null : ElementRef.EdgeOf(meshId, new Edge(vertexMap[e.A], vertexMap[e.B])),
            SelectMode.Face => faceMap[e.A] < 0 ? null : ElementRef.Face(meshId, faceMap[e.A]),
            _ => e,
        };
        var affected = Items.Where(i => i.Mesh == meshId).ToList();
        Items.ExceptWith(affected);
        foreach (var e in affected)
            if (Map(e) is { } mapped) Items.Add(mapped);
        if (Active is { } a && a.Mesh == meshId) Active = Map(a);
    }

    /// <summary>Drops references that no longer resolve (after undo or topology edits).</summary>
    public void Prune(Document doc)
    {
        Items.RemoveWhere(i => doc.Find(i.Mesh) is not { } m ? !IsLiveEntity(doc, i) : m.Hidden || m.Locked || !IsValid(m, i));
        if (Active is { } a && !Items.Contains(a)) Active = null;
    }

    bool IsLiveEntity(Document doc, ElementRef i) =>
        Mode == SelectMode.Object && doc.FindEntity(i.Mesh) is { } e && !e.Hidden && !e.Locked;

    bool IsValid(PolyMesh m, ElementRef i) => Mode switch
    {
        SelectMode.Vertex => i.A < m.Positions.Count,
        SelectMode.Edge => i.B < m.Positions.Count,
        SelectMode.Face => i.A < m.Faces.Count,
        _ => true,
    };

    static HashSet<int> VerticesOf(PolyMesh mesh, List<ElementRef> items, SelectMode mode) => mode switch
    {
        SelectMode.Vertex => items.Select(i => i.A).ToHashSet(),
        SelectMode.Edge => items.SelectMany(i => new[] { i.A, i.B }).ToHashSet(),
        SelectMode.Face => items.SelectMany(i => mesh.Faces[i.A].Verts).ToHashSet(),
        _ => Enumerable.Range(0, mesh.Positions.Count).ToHashSet(),
    };

    public static IEnumerable<ElementRef> Elements(PolyMesh m, SelectMode mode) => mode switch
    {
        SelectMode.Vertex => Enumerable.Range(0, m.Positions.Count).Select(v => ElementRef.Vertex(m.Id, v)),
        SelectMode.Edge => m.Edges().Select(e => ElementRef.EdgeOf(m.Id, e)),
        SelectMode.Face => Enumerable.Range(0, m.Faces.Count).Select(f => ElementRef.Face(m.Id, f)),
        _ => [ElementRef.Object(m.Id)],
    };
}
