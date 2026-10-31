using System.Numerics;
using Chip.Core.Geometry;
using Chip.Core.Texturing;

namespace Chip.Core.Scene;

/// <summary>
/// What a gizmo drag does besides moving. The select tool maps modifiers to a kind (Shift = extrude,
/// Ctrl+Shift = push/pull); the gesture decides what that means for the current selection mode:
/// faces extrude or push/pull, open edges extrude, everything else just moves.
/// </summary>
public enum GestureKind { Transform, Extrude, PushPull }

/// <summary>
/// One transform of the current selection as an undoable gesture (gizmo drag, nudge, numeric entry).
/// <see cref="Apply"/> is absolute relative to the start state, so repeated calls never accumulate error.
/// Object-mode rigid moves keep textures locked to the geometry (unless <see cref="Session.TextureLock"/> is off);
/// everything else stays world-projected. Selected entities (object mode) move with the selection and turn with rotations.
/// </summary>
public sealed class TransformGesture
{
    readonly Session _session;
    readonly List<(PolyMesh Mesh, int[] Verts, Vector3[] Start)> _parts = new();
    readonly List<(PolyMesh Mesh, int Face, TextureMapping Start)> _lockedFaces = new();
    readonly List<(PolyMesh Mesh, List<int> Faces)> _extrudeSides = new();
    readonly List<(PolyMesh Mesh, HashSet<int> Pushed)> _pushed = new();
    readonly List<(PolyMesh Mesh, PolyMesh Start)> _pushStarts = new();
    readonly Selection? _pushSelection;
    readonly List<(Entity Entity, Vector3 Position, Vector3 Rotation)> _entities = new();
    /// <summary>Origins of meshes and whole groups moving with the selection (object mode), with their start.</summary>
    readonly List<(OriginTarget Target, ObjectOrigin Start)> _origins = new();
    /// <summary>Push/pull only moves along this direction (the selected faces' average normal).</summary>
    readonly Vector3? _constraint;

    Matrix4x4 _last = Matrix4x4.Identity;

    public Vector3 Pivot { get; }
    public (Vector3 Min, Vector3 Max) StartBounds { get; }

    public GestureKind Kind { get; }

    TransformGesture(Session session, string label, GestureKind kind)
    {
        Kind = kind;
        _session = session;
        session.Begin(label, session.Sel.MeshIds());
        var doc = session.Doc;
        var sel = session.Sel;

        if (kind == GestureKind.Extrude && sel.Mode == SelectMode.Face)
        {
            foreach (var (mesh, faces) in sel.FaceSets(doc))
            {
                var (_, sides) = MeshOps.ExtrudeFaces(mesh, faces);
                _extrudeSides.Add((mesh, sides));
            }
        }
        else if (kind == GestureKind.PushPull && sel.Mode == SelectMode.Face)
        {
            var sets = sel.FaceSets(doc);
            var sum = Vector3.Zero;
            foreach (var (mesh, faces) in sets) sum += MeshOps.RegionNormal(mesh, faces);
            if (sum.LengthSquared() > 1e-8f)
            {
                var dir = Vector3.Normalize(sum);
                _constraint = dir;
                foreach (var (mesh, faces) in sets)
                {
                    var (_, sides) = MeshOps.PushPullFaces(mesh, faces, dir);
                    _extrudeSides.Add((mesh, sides));
                    _pushed.Add((mesh, faces.ToHashSet()));
                }
            }
        }
        else if (kind == GestureKind.Extrude && sel.Mode == SelectMode.Edge)
        {
            // Open edges extrude; the new outer edges become the selection, so they are what moves.
            var added = new List<ElementRef>();
            foreach (var g in sel.Items.GroupBy(i => i.Mesh).ToList())
            {
                if (doc.Find(g.Key) is not { } mesh) continue;
                var (edges, sides) = MeshOps.ExtrudeEdges(mesh, g.Select(i => i.AsEdge));
                if (sides.Count == 0) continue;
                _extrudeSides.Add((mesh, sides));
                added.AddRange(edges.Select(e => ElementRef.EdgeOf(mesh.Id, e)));
            }
            if (added.Count > 0)
            {
                sel.Clear();
                foreach (var e in added) sel.Add(e);
            }
        }

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var (mesh, verts) in sel.VertexSets(doc))
        {
            var arr = verts.ToArray();
            var start = arr.Select(v => mesh.Positions[v]).ToArray();
            foreach (var p in start) { min = Vector3.Min(min, p); max = Vector3.Max(max, p); }
            _parts.Add((mesh, arr, start));
            // A linked copy's textures always move with it: sliding them would be an edit of every copy.
            if (sel.Mode == SelectMode.Object && (session.TextureLock || mesh.Part != 0))
                for (int f = 0; f < mesh.Faces.Count; f++) _lockedFaces.Add((mesh, f, mesh.Faces[f].Map));
        }
        foreach (var e in sel.Entities(doc))
        {
            _entities.Add((e, e.Position, e.Rotation));
            min = Vector3.Min(min, e.Position);
            max = Vector3.Max(max, e.Position);
        }
        StartBounds = _parts.Count + _entities.Count == 0 ? (Vector3.Zero, Vector3.Zero) : (min, max);
        Pivot = OriginOps.UnitOrigin(doc, sel)?.Position ?? (StartBounds.Min + StartBounds.Max) * 0.5f;
        if (sel.Mode == SelectMode.Object)
        {
            // An origin travels with its object: a mesh's with the mesh, a group's when all of the group moves.
            foreach (var (mesh, _, _) in _parts)
                if (mesh.Origin is { } o) _origins.Add((new OriginTarget(null, mesh), o));
            var moved = sel.MeshIds();
            foreach (var g in doc.Groups)
                if (g.Origin is { } o && GroupOps.AllMembers(doc, g) is { Count: > 0 } all && all.All(moved.Contains))
                    _origins.Add((new OriginTarget(g, null), o));
        }
        if (_pushed.Count > 0)
        {
            _pushSelection = sel.Clone();
            foreach (var (mesh, _) in _pushed) _pushStarts.Add((mesh, mesh.Clone()));
        }
    }

    /// <summary>Starts a gesture on the session's selection. Returns null when nothing is selected.</summary>
    public static TransformGesture? Start(Session session, string label, GestureKind kind)
    {
        if (session.Sel.IsEmpty) return null;
        return new TransformGesture(session, label, kind);
    }

    public static TransformGesture? Start(Session session, string label, bool extrude = false) =>
        Start(session, label, extrude ? GestureKind.Extrude : GestureKind.Transform);

    /// <summary>Sets every affected vertex to <c>m * start</c>. <paramref name="rigid"/> = rotation/translation only.</summary>
    public void Apply(Matrix4x4 m, bool rigid)
    {
        if (_constraint is { } n) m = Matrix4x4.CreateTranslation(n * Vector3.Dot(m.Translation, n));
        // Rebuild each preview from the initial extrusion: trimming changes indices and must never accumulate.
        foreach (var (mesh, start) in _pushStarts)
        {
            mesh.Positions = new(start.Positions);
            mesh.Faces = start.Faces.Select(f => f.Clone()).ToList();
            mesh.SoftEdges = new(start.SoftEdges);
        }
        if (_pushSelection != null)
        {
            _session.Sel.Clear();
            foreach (var item in _pushSelection.Items) _session.Sel.Add(item);
        }
        foreach (var (mesh, verts, start) in _parts)
            for (int i = 0; i < verts.Length; i++)
                mesh.Positions[verts[i]] = Vector3.Transform(start[i], m);
        foreach (var (mesh, face, start) in _lockedFaces)
            mesh.Faces[face].Map = rigid ? start.MovedRigidly(m) : start;
        foreach (var (target, start) in _origins) target.Origin = start.Transformed(m);
        if (_entities.Count > 0)
        {
            // Entities keep their size: only the rotation part of the transform turns them.
            var turn = Matrix4x4.Decompose(m, out _, out var q, out _) ? Quaternion.Normalize(q) : Quaternion.Identity;
            bool turned = MathF.Abs(turn.W) < 0.999999f;
            foreach (var (e, pos, rot) in _entities)
            {
                e.Position = Vector3.Transform(pos, m);
                e.Rotation = turned ? EntityOps.ToEuler(Quaternion.Concatenate(EntityOps.Orientation(rot), turn)) : rot;
            }
        }
        _last = m;
        if (!m.IsIdentity)
            foreach (var (mesh, pushed) in _pushed)
            {
                var sides = _extrudeSides.First(s => s.Mesh == mesh).Faces;
                MeshOps.AlignToGrid(mesh, sides);
                if (MeshOps.ResolvePushPullSides(mesh, sides, pushed) is { } resolved)
                    _session.Sel.Remap(mesh.Id, resolved.VertexMap, resolved.FaceMap);
            }
        _session.Touch();
    }

    /// <summary>
    /// Commits one undo step. An extrude or push/pull that never moved is discarded; push/pull's trimmed preview is
    /// retained; component edits that drop vertices onto each other merge them (so pulling
    /// a box's top edge down to the floor yields a clean wedge).
    /// </summary>
    public void Commit()
    {
        if ((_extrudeSides.Count > 0 || _constraint != null) && _last.IsIdentity) { _session.Cancel(); return; }
        foreach (var (mesh, sides) in _extrudeSides)
            if (!_pushed.Any(p => p.Mesh == mesh)) MeshOps.AlignToGrid(mesh, sides);
        if (_session.Sel.Mode != SelectMode.Object)
            foreach (var mesh in _parts.Select(p => p.Mesh).Distinct())
                if (MeshOps.CollapseCoincident(mesh, 1e-3f) is { } maps)
                    _session.Sel.Remap(mesh.Id, maps.VertexMap, maps.FaceMap);
        _session.End();
    }

    public void Cancel() => _session.Cancel();

    public static Matrix4x4 About(Vector3 pivot, Matrix4x4 m) =>
        Matrix4x4.CreateTranslation(-pivot) * m * Matrix4x4.CreateTranslation(pivot);
}
