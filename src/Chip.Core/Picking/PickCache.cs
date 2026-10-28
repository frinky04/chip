using System.Numerics;
using Chip.Core.Geometry;
using Chip.Core.Scene;

namespace Chip.Core.Picking;

/// <summary>
/// Session-owned picking acceleration. Unchanged queries allocate nothing; changed meshes rebuild independently.
/// The static <see cref="Picker"/> remains the brute-force reference. Like Session, this cache is single-threaded.
/// </summary>
public sealed class PickCache(Session session)
{
    sealed class MeshData
    {
        public PolyMesh Mesh;
        public readonly int Revision;
        public readonly Vector3[] Positions, Normals, Centers;
        public readonly Edge[] Edges;
        public readonly int[][] Triangles;
        public readonly PickBounds Bounds;
        public readonly PickTree Faces;

        public MeshData(PolyMesh mesh, int revision)
        {
            Mesh = mesh;
            Revision = revision;
            Positions = mesh.Positions.ToArray();
            Edges = mesh.Edges().ToArray(); // Preserve the legacy enumeration order for equal-distance hits.
            Normals = new Vector3[mesh.Faces.Count];
            Centers = new Vector3[mesh.Faces.Count];
            Triangles = new int[mesh.Faces.Count][];
            var faceBounds = new PickBounds[mesh.Faces.Count];
            var tris = new List<int>();
            for (int f = 0; f < mesh.Faces.Count; f++)
            {
                Normals[f] = mesh.FaceNormal(f);
                Centers[f] = mesh.FaceCenter(f);
                tris.Clear();
                Triangulator.Triangulate(mesh, f, tris);
                var verts = mesh.Faces[f].Verts;
                Triangles[f] = tris.Select(i => verts[i]).ToArray();
                var first = verts.Length > 0 ? Positions[verts[0]] : Vector3.Zero;
                var box = new PickBounds(first, first);
                foreach (int v in verts) box = PickBounds.Union(box, new PickBounds(Positions[v], Positions[v]));
                faceBounds[f] = box;
            }
            var bounds = mesh.Bounds();
            Bounds = new PickBounds(bounds.Min, bounds.Max);
            Faces = new PickTree(faceBounds);
        }
    }

    readonly Dictionary<int, MeshData> _byId = new();
    MeshData[] _meshes = [];
    PickTree _scene = new([]);
    Document? _doc;
    int _version = -1;

    /// <summary>Diagnostic counter for verifying that a single-mesh edit rebuilds just one mesh.</summary>
    public int MeshBuildCount { get; private set; }

    void Sync()
    {
        var doc = session.Doc;
        if (_version == session.Version && ReferenceEquals(doc, _doc)) return;
        bool membership = _meshes.Length != doc.Meshes.Count;
        if (!membership)
            for (int i = 0; i < _meshes.Length; i++)
                if (_meshes[i].Mesh.Id != doc.Meshes[i].Id) { membership = true; break; }
        if (membership) _meshes = new MeshData[doc.Meshes.Count];
        for (int i = 0; i < doc.Meshes.Count; i++)
        {
            var mesh = doc.Meshes[i];
            int revision = session.MeshRevision(mesh);
            bool changed = !_byId.TryGetValue(mesh.Id, out var data) || data.Revision != revision;
            if (changed)
            {
                data = new MeshData(mesh, revision);
                _byId[mesh.Id] = data;
                MeshBuildCount++;
            }
            data!.Mesh = mesh; // Undo/cancel can replace an object without changing its content revision.
            _meshes[i] = data;
            if (changed && !membership) _scene.Refit(i, data.Bounds);
        }
        if (membership)
        {
            _scene = new PickTree(_meshes.Select(m => m.Bounds).ToArray());
            var live = doc.Meshes.Select(m => m.Id).ToHashSet();
            foreach (int id in _byId.Keys.Where(id => !live.Contains(id)).ToArray()) _byId.Remove(id);
        }
        _doc = doc;
        _version = session.Version;
    }

    /// <inheritdoc cref="Picker.RaycastFace"/>
    public FaceHit? RaycastFace(Ray ray, bool backfaces = false, bool selectable = false) =>
        Raycast(ray, backfaces, selectable, PickFilter.All, out _);

    /// <summary>The nearest face of a mesh passing <paramref name="filter"/> (read through <see cref="_owners"/> unless All).</summary>
    FaceHit? Raycast(Ray ray, bool backfaces, bool selectable, PickFilter filter, out int meshIndex)
    {
        Sync();
        FaceHit? best = null;
        int bestMesh = int.MaxValue;
        if (_scene.Nodes.Length > 0) VisitScene(0, ray, backfaces, selectable, filter, ref best, ref bestMesh);
        meshIndex = bestMesh;
        return best;
    }

    void VisitScene(int node, Ray ray, bool backfaces, bool selectable, PickFilter filter, ref FaceHit? best, ref int bestMesh)
    {
        ref readonly var n = ref _scene.Nodes[node];
        if (!n.Bounds.Intersects(ray, best?.Distance ?? float.MaxValue)) return;
        if (n.Item < 0)
        {
            VisitScene(n.Left, ray, backfaces, selectable, filter, ref best, ref bestMesh);
            VisitScene(n.Right, ray, backfaces, selectable, filter, ref best, ref bestMesh);
            return;
        }
        var data = _meshes[n.Item];
        if (data.Mesh.Hidden || (selectable && data.Mesh.Locked) || data.Faces.Nodes.Length == 0) return;
        if (filter != PickFilter.All && !ObjectRanking.Accepts(filter, _owners.ContainsKey(data.Mesh.Id))) return;
        VisitFaces(data, n.Item, 0, ray, backfaces, ref best, ref bestMesh);
    }

    // Object picking scratch, reused so hover allocates nothing once warm.
    readonly Dictionary<int, int> _owners = new();
    readonly List<ObjectRanking.Candidate> _objects = new();
    readonly HashSet<int> _units = new();

    // Entity models (reference models, class previews) by entity id. Looking one up resolves a path, so hover reuses them
    // until the document or the loaded models change; the class is checked per entry, as entity state is read live.
    readonly Dictionary<int, (string Class, ReferenceModel? Model)> _models = new();
    Document? _modelsDoc;
    int _modelsVersion = -1, _modelsRevision = -1;
    Func<Entity, ReferenceModel?>? _modelOf;

    Func<Entity, ReferenceModel?> Models()
    {
        if (!ReferenceEquals(_modelsDoc, session.Doc) || _modelsVersion != session.Version || _modelsRevision != ReferenceModels.DrawRevision)
        {
            _models.Clear();
            _modelsDoc = session.Doc;
            _modelsVersion = session.Version;
            _modelsRevision = ReferenceModels.DrawRevision;
        }
        return _modelOf ??= e =>
        {
            if (e.IsMeshEntity) return null;
            if (_models.TryGetValue(e.Id, out var hit) && hit.Class == e.ClassName) return hit.Model;
            var model = ObjectRanking.Uncached(e);
            _models[e.Id] = (e.ClassName, model);
            return model;
        };
    }

    /// <summary>Fills <see cref="_objects"/> with the entities and the nearest mesh (with <paramref name="allMeshes"/>, every mesh) under the mouse.</summary>
    void CollectObjects(ViewInfo view, Vector2 mouse, float pixelScale, bool backfaces, PickFilter filter, bool allMeshes)
    {
        Sync();
        var doc = session.Doc;
        var ray = view.ScreenRay(mouse);
        ObjectRanking.Owners(doc, _owners);
        _objects.Clear();
        // Any drawn surface, locked or not, hides the entities behind it.
        var surface = Raycast(ray, backfaces, false, PickFilter.All, out int surfaceMesh);
        if (filter != PickFilter.Geometry) ObjectRanking.AddEntities(doc, view, ray, mouse, pixelScale, surface?.Distance, _objects, Models());
        if (allMeshes)
        {
            if (_scene.Nodes.Length > 0) VisitAllMeshes(0, ray, backfaces, filter);
            return;
        }
        var hit = surface;
        int meshIndex = surfaceMesh;
        if (hit is { } s && (s.Mesh.Locked || !ObjectRanking.Accepts(filter, _owners.ContainsKey(s.Mesh.Id))))
            hit = Raycast(ray, backfaces, true, filter, out meshIndex);
        if (hit is { } h) ObjectRanking.AddMesh(h.Mesh.Id, _owners.TryGetValue(h.Mesh.Id, out var o) ? o : -1, meshIndex, h.Distance, _objects);
    }

    /// <summary>Every selectable mesh the ray hits, at its nearest face.</summary>
    void VisitAllMeshes(int node, Ray ray, bool backfaces, PickFilter filter)
    {
        ref readonly var n = ref _scene.Nodes[node];
        if (!n.Bounds.Intersects(ray, float.MaxValue)) return;
        if (n.Item < 0)
        {
            VisitAllMeshes(n.Left, ray, backfaces, filter);
            VisitAllMeshes(n.Right, ray, backfaces, filter);
            return;
        }
        var data = _meshes[n.Item];
        if (data.Mesh.Hidden || data.Mesh.Locked || data.Faces.Nodes.Length == 0) return;
        int owner = _owners.TryGetValue(data.Mesh.Id, out var o) ? o : -1;
        if (!ObjectRanking.Accepts(filter, owner >= 0)) return;
        FaceHit? best = null;
        int bestMesh = int.MaxValue;
        VisitFaces(data, n.Item, 0, ray, backfaces, ref best, ref bestMesh);
        if (best is { } b) ObjectRanking.AddMesh(data.Mesh.Id, owner, n.Item, b.Distance, _objects);
    }

    /// <inheritdoc cref="Picker.PickObjects"/>
    public void PickObjects(ViewInfo view, Vector2 mouse, List<ElementRef> result, float pixelScale = 1, bool backfaces = false,
        PickFilter filter = PickFilter.All)
    {
        CollectObjects(view, mouse, pixelScale, backfaces, filter, allMeshes: true);
        ObjectRanking.Finish(_objects, _units);
        result.Clear();
        foreach (var c in _objects) result.Add(ElementRef.Object(c.Id));
    }

    static void VisitFaces(MeshData data, int meshIndex, int node, Ray ray, bool backfaces, ref FaceHit? best, ref int bestMesh)
    {
        ref readonly var n = ref data.Faces.Nodes[node];
        if (!n.Bounds.Intersects(ray, best?.Distance ?? float.MaxValue)) return;
        if (n.Item < 0)
        {
            VisitFaces(data, meshIndex, n.Left, ray, backfaces, ref best, ref bestMesh);
            VisitFaces(data, meshIndex, n.Right, ray, backfaces, ref best, ref bestMesh);
            return;
        }
        int f = n.Item;
        var normal = data.Normals[f];
        if (!backfaces && Vector3.Dot(normal, ray.Dir) >= 0) return;
        var tris = data.Triangles[f];
        for (int i = 0; i < tris.Length; i += 3)
        {
            if (!Picker.RayTriangle(ray, data.Positions[tris[i]], data.Positions[tris[i + 1]], data.Positions[tris[i + 2]], out float t)) continue;
            if (best is { } b && (b.Distance < t || (b.Distance == t &&
                (bestMesh < meshIndex || (bestMesh == meshIndex && b.Face <= f))))) continue;
            best = new FaceHit(data.Mesh, f, t, ray.At(t), normal);
            bestMesh = meshIndex;
        }
    }

    /// <inheritdoc cref="Picker.Pick"/>
    public ElementRef? Pick(ViewInfo view, Vector2 mouse, SelectMode mode, float pixelScale = 1, bool backfaces = false,
        PickFilter filter = PickFilter.All)
    {
        if (mode == SelectMode.Object)
        {
            CollectObjects(view, mouse, pixelScale, backfaces, filter, allMeshes: false);
            return ObjectRanking.Best(_objects) is var best && best >= 0 ? ElementRef.Object(_objects[best].Id) : null;
        }
        if (mode == SelectMode.Face)
            return RaycastFace(view.ScreenRay(mouse), backfaces, selectable: true) is { } hf ? ElementRef.Face(hf.Mesh.Id, hf.Face) : null;

        // Same rule as Picker.Pick: every element within the radius, nearest first; the first visible one wins.
        Sync();
        float radius = (mode == SelectMode.Vertex ? Picker.VertexRadius : Picker.EdgeRadius) * pixelScale;
        var box = new PickScreenBox(view, mouse - new Vector2(radius), mouse + new Vector2(radius));
        _candidates.Clear();
        if (_scene.Nodes.Length > 0) CollectElements(0, box, view, mouse, mode, radius);
        _candidates.Sort(Picker.ElementCandidate.Order);
        for (int i = 0; i < Math.Min(_candidates.Count, Picker.MaxVisibilityTests); i++)
            if (_candidates[i] is var c && Picker.Visible(view, c.Point, _occluder, c.Mesh, c.A, c.B))
                return mode == SelectMode.Vertex ? ElementRef.Vertex(c.Mesh.Id, c.A) : ElementRef.EdgeOf(c.Mesh.Id, new Edge(c.A, c.B));
        return null;
    }

    readonly List<Picker.ElementCandidate> _candidates = new();
    Func<Ray, FaceHit?>? _occluderField;
    /// <summary>Visibility rays: unlocked or not, any drawn (front) face hides what's behind it.</summary>
    Func<Ray, FaceHit?> _occluder => _occluderField ??= r => RaycastFace(r);

    void CollectElements(int node, PickScreenBox box, ViewInfo view, Vector2 mouse, SelectMode mode, float radius)
    {
        ref readonly var n = ref _scene.Nodes[node];
        if (!box.Intersects(n.Bounds)) return;
        if (n.Item < 0)
        {
            CollectElements(n.Left, box, view, mouse, mode, radius);
            CollectElements(n.Right, box, view, mouse, mode, radius);
            return;
        }
        var data = _meshes[n.Item];
        var mesh = data.Mesh;
        if (mesh.Hidden || mesh.Locked) return;
        if (mode == SelectMode.Vertex)
        {
            for (int v = 0; v < data.Positions.Length; v++)
                if (view.Project(data.Positions[v], out var s) && Vector2.Distance(s, mouse) is var d && d < radius)
                    _candidates.Add(new(d, n.Item, v, mesh, data.Positions[v], v, -1));
            return;
        }
        for (int i = 0; i < data.Edges.Length; i++)
        {
            var e = data.Edges[i];
            if (Picker.EdgeUnderMouse(view, data.Positions[e.A], data.Positions[e.B], mouse, out float d, out var q) && d < radius)
                _candidates.Add(new(d, n.Item, i, mesh, q, e.A, e.B));
        }
    }

    /// <inheritdoc cref="Picker.Marquee"/>
    public List<ElementRef> Marquee(ViewInfo view, Vector2 a, Vector2 b, SelectMode mode, bool backfaces = false, PickFilter filter = PickFilter.All)
    {
        var result = new List<ElementRef>();
        Marquee(view, a, b, mode, result, backfaces, filter);
        return result;
    }

    /// <summary>Writes into a reusable list (cleared first) to avoid allocations while dragging a marquee.</summary>
    public void Marquee(ViewInfo view, Vector2 a, Vector2 b, SelectMode mode, List<ElementRef> result, bool backfaces = false,
        PickFilter filter = PickFilter.All)
    {
        Sync();
        if (mode == SelectMode.Object) ObjectRanking.Owners(session.Doc, _owners);
        result.Clear();
        var min = Vector2.Min(a, b);
        var max = Vector2.Max(a, b);
        var box = new PickScreenBox(view, min, max);
        foreach (var data in _meshes)
        {
            var mesh = data.Mesh;
            if (mesh.Hidden || mesh.Locked || !box.Intersects(data.Bounds)) continue;
            switch (mode)
            {
                case SelectMode.Vertex:
                    for (int v = 0; v < data.Positions.Length; v++)
                        if (Inside(view, min, max, data.Positions[v])) result.Add(ElementRef.Vertex(mesh.Id, v));
                    break;
                case SelectMode.Edge:
                    foreach (var e in data.Edges)
                        if (Inside(view, min, max, data.Positions[e.A]) && Inside(view, min, max, data.Positions[e.B])) result.Add(ElementRef.EdgeOf(mesh.Id, e));
                    break;
                case SelectMode.Face:
                    for (int f = 0; f < data.Centers.Length; f++)
                        if ((backfaces || Vector3.Dot(data.Normals[f], data.Centers[f] - view.Eye) < 0) && Inside(view, min, max, data.Centers[f]))
                            result.Add(ElementRef.Face(mesh.Id, f));
                    break;
                case SelectMode.Object:
                    if (!ObjectRanking.Accepts(filter, _owners.ContainsKey(mesh.Id))) break;
                    foreach (var center in data.Centers)
                        if (Inside(view, min, max, center)) { result.Add(ElementRef.Object(mesh.Id)); break; }
                    break;
            }
        }
        if (mode == SelectMode.Object && filter != PickFilter.Geometry)
        {
            var models = Models();
            foreach (var e in session.Doc.Entities)
                if (!e.Hidden && !e.Locked && Inside(view, min, max, EntityOps.Center(e, models(e)))) result.Add(ElementRef.Object(e.Id));
        }
    }

    static bool Inside(ViewInfo view, Vector2 min, Vector2 max, Vector3 p) => view.Project(p, out var s) &&
        s.X >= min.X && s.Y >= min.Y && s.X <= max.X && s.Y <= max.Y;

    /// <inheritdoc cref="Picker.PickEntity"/>
    public Entity? PickEntity(ViewInfo view, Vector2 mouse, float pixelScale = 1, float? surfaceDistance = null)
    {
        // Entities are few and mutable independently of mesh revisions: read their current transforms and flags.
        _objects.Clear();
        ObjectRanking.AddEntities(session.Doc, view, view.ScreenRay(mouse), mouse, pixelScale, surfaceDistance, _objects, Models());
        _objects.RemoveAll(static c => !c.Visible);
        return ObjectRanking.Best(_objects) is var i && i >= 0 ? session.Doc.FindEntity(_objects[i].Id) : null;
    }
}
