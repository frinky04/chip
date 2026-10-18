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
    public FaceHit? RaycastFace(Ray ray, bool backfaces = false, bool selectable = false)
    {
        Sync();
        FaceHit? best = null;
        int bestMesh = int.MaxValue;
        if (_scene.Nodes.Length > 0) VisitScene(0, ray, backfaces, selectable, ref best, ref bestMesh);
        return best;
    }

    void VisitScene(int node, Ray ray, bool backfaces, bool selectable, ref FaceHit? best, ref int bestMesh)
    {
        ref readonly var n = ref _scene.Nodes[node];
        if (!n.Bounds.Intersects(ray, best?.Distance ?? float.MaxValue)) return;
        if (n.Item < 0)
        {
            VisitScene(n.Left, ray, backfaces, selectable, ref best, ref bestMesh);
            VisitScene(n.Right, ray, backfaces, selectable, ref best, ref bestMesh);
            return;
        }
        var data = _meshes[n.Item];
        if (data.Mesh.Hidden || (selectable && data.Mesh.Locked) || data.Faces.Nodes.Length == 0) return;
        VisitFaces(data, n.Item, 0, ray, backfaces, ref best, ref bestMesh);
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
    public ElementRef? Pick(ViewInfo view, Vector2 mouse, SelectMode mode, float pixelScale = 1, bool backfaces = false)
    {
        if (mode is SelectMode.Object or SelectMode.Face)
        {
            var hit = RaycastFace(view.ScreenRay(mouse), backfaces, selectable: true);
            if (mode == SelectMode.Face) return hit is { } hf ? ElementRef.Face(hf.Mesh.Id, hf.Face) : null;
            if (PickEntity(view, mouse, pixelScale, hit?.Distance) is { } entity) return ElementRef.Object(entity.Id);
            return hit is { } h ? ElementRef.Object(h.Mesh.Id) : null;
        }

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
    public List<ElementRef> Marquee(ViewInfo view, Vector2 a, Vector2 b, SelectMode mode, bool backfaces = false)
    {
        var result = new List<ElementRef>();
        Marquee(view, a, b, mode, result, backfaces);
        return result;
    }

    /// <summary>Writes into a reusable list (cleared first) to avoid allocations while dragging a marquee.</summary>
    public void Marquee(ViewInfo view, Vector2 a, Vector2 b, SelectMode mode, List<ElementRef> result, bool backfaces = false)
    {
        Sync();
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
                    foreach (var center in data.Centers)
                        if (Inside(view, min, max, center)) { result.Add(ElementRef.Object(mesh.Id)); break; }
                    break;
            }
        }
        if (mode == SelectMode.Object)
            foreach (var e in session.Doc.Entities)
                if (!e.Hidden && !e.Locked && Inside(view, min, max, e.Position)) result.Add(ElementRef.Object(e.Id));
    }

    static bool Inside(ViewInfo view, Vector2 min, Vector2 max, Vector3 p) => view.Project(p, out var s) &&
        s.X >= min.X && s.Y >= min.Y && s.X <= max.X && s.Y <= max.Y;

    /// <inheritdoc cref="Picker.PickEntity"/>
    public Entity? PickEntity(ViewInfo view, Vector2 mouse, float pixelScale = 1, float? surfaceDistance = null)
    {
        // Entities are few and mutable independently of mesh revisions. Read their current transforms and flags.
        // Avoid EntityClasses.Get's LINQ/unknown-class allocations in this per-frame path.
        var ray = view.ScreenRay(mouse);
        float limit = surfaceDistance is { } s ? s * 1.02f + 0.02f : float.MaxValue;
        Entity? best = null;
        float bestT = float.MaxValue;
        foreach (var e in session.Doc.Entities)
        {
            if (e.Hidden || e.Locked) continue;
            Vector3 min = new(-0.2f), max = new(0.2f);
            for (int i = 0; i < EntityClasses.BuiltIn.Count; i++)
            {
                var c = EntityClasses.BuiltIn[i];
                if (c.Name == e.ClassName) { min = c.BoxMin; max = c.BoxMax; break; }
            }
            var inv = Quaternion.Conjugate(EntityOps.Orientation(e));
            var localRay = new Ray(Vector3.Transform(ray.Origin - e.Position, inv), Vector3.Transform(ray.Dir, inv));
            float? t = EntityBoxHit(localRay, min, max);
            var center = Vector3.Transform((min + max) * 0.5f, EntityOps.World(e));
            if (t == null && view.Project(center, out var sc) && Vector2.Distance(sc, mouse) < Picker.EntityGlyphRadius * pixelScale)
                t = MathF.Max(0, Vector3.Dot(center - ray.Origin, ray.Dir));
            if (t is not { } d || d > limit || d >= bestT) continue;
            bestT = d;
            best = e;
        }
        return best;
    }

    static float? EntityBoxHit(Ray ray, Vector3 min, Vector3 max)
    {
        float near = 0, far = float.MaxValue;
        for (int axis = 0; axis < 3; axis++)
        {
            float o = ray.Origin[axis], d = ray.Dir[axis];
            if (MathF.Abs(d) < 1e-9f) { if (o < min[axis] || o > max[axis]) return null; continue; }
            float a = (min[axis] - o) / d, b = (max[axis] - o) / d;
            if (a > b) (a, b) = (b, a);
            near = MathF.Max(near, a);
            far = MathF.Min(far, b);
            if (near > far) return null;
        }
        return near;
    }
}
