using System.Numerics;
using Chip.Core.Geometry;
using Chip.Core.Texturing;

namespace Chip.Core.Scene;

/// <summary>
/// What a gizmo drag does besides moving. The select tool maps modifiers to a kind (Shift = extrude,
/// Ctrl+Shift = push/pull); the gesture decides what that means for the current selection mode.
/// </summary>
public enum GestureKind { Transform, Extrude, PushPull }

/// <summary>
/// One transform of the current selection as an undoable gesture (gizmo drag, nudge, numeric entry).
/// <see cref="Apply"/> is absolute relative to the start state, so repeated calls never accumulate error.
/// Object-mode rigid moves keep textures locked to the geometry (unless <see cref="Session.TextureLock"/> is off);
/// everything else stays world-projected.
/// </summary>
public sealed class TransformGesture
{
    readonly Session _session;
    readonly List<(PolyMesh Mesh, int[] Verts, Vector3[] Start)> _parts = new();
    readonly List<(PolyMesh Mesh, int Face, TextureMapping Start)> _lockedFaces = new();
    readonly List<(PolyMesh Mesh, List<int> Faces)> _extrudeSides = new();

    Matrix4x4 _last = Matrix4x4.Identity;

    public Vector3 Pivot { get; }
    public (Vector3 Min, Vector3 Max) StartBounds { get; }

    public GestureKind Kind { get; }

    TransformGesture(Session session, string label, GestureKind kind)
    {
        Kind = kind;
        _session = session;
        session.Begin(label);
        var doc = session.Doc;
        var sel = session.Sel;

        // Face extrude today; edge extrude and true push/pull (stream A) plug in here by mode and kind.
        if (kind != GestureKind.Transform && sel.Mode == SelectMode.Face)
        {
            foreach (var (mesh, faces) in sel.FaceSets(doc))
            {
                var (_, sides) = MeshOps.ExtrudeFaces(mesh, faces);
                _extrudeSides.Add((mesh, sides));
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
            if (sel.Mode == SelectMode.Object && session.TextureLock)
                for (int f = 0; f < mesh.Faces.Count; f++) _lockedFaces.Add((mesh, f, mesh.Faces[f].Map));
        }
        StartBounds = _parts.Count == 0 ? (Vector3.Zero, Vector3.Zero) : (min, max);
        Pivot = (StartBounds.Min + StartBounds.Max) * 0.5f;
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
        foreach (var (mesh, verts, start) in _parts)
            for (int i = 0; i < verts.Length; i++)
                mesh.Positions[verts[i]] = Vector3.Transform(start[i], m);
        foreach (var (mesh, face, start) in _lockedFaces)
            mesh.Faces[face].Map = rigid ? start.MovedRigidly(m) : start;
        _last = m;
        _session.Touch();
    }

    /// <summary>
    /// Commits one undo step. An extrude that never moved is discarded; component edits that drop vertices onto
    /// each other merge them (so pulling a box's top edge down to the floor yields a clean wedge).
    /// </summary>
    public void Commit()
    {
        if (_extrudeSides.Count > 0 && _last.IsIdentity) { _session.Cancel(); return; }
        foreach (var (mesh, sides) in _extrudeSides) MeshOps.AlignToGrid(mesh, sides);
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
