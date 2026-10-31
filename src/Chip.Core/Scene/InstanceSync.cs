using System.Numerics;
using Chip.Core.Geometry;
using Chip.Core.Texturing;

namespace Chip.Core.Scene;

/// <summary>
/// Keeps linked copies the same. The <see cref="Session"/> runs it after every live change and step: a copy whose meshes,
/// members or origin changed since the last run is read back into its definition's space; if that differs from the
/// definition, it becomes the definition and every other copy is rebuilt from it. Moving a whole copy changes nothing
/// in its own space, so it changes no other copy. Other copies' meshes are replaced by new objects, never edited in
/// place, so the step's undo snapshot keeps the old ones; the ids replaced are reported for the session's scope check.
/// </summary>
public sealed class InstanceSync
{
    /// <summary>Per copy (group id): what it was at the last run.</summary>
    readonly Dictionary<int, ulong> _seen = new();

    /// <summary>Records every copy as it is now, without syncing (after load, undo, redo, cancel).</summary>
    public void Refresh(Document doc, Func<PolyMesh, int> revision)
    {
        _seen.Clear();
        foreach (var g in doc.Groups)
            if (g.IsInstance) _seen[g.Id] = Signature(doc, g, revision);
    }

    /// <summary>
    /// Carries the edit of a changed copy to the others. Returns the ids of meshes replaced or removed in other copies
    /// (empty when nothing propagated).
    /// </summary>
    public List<int> Run(Document doc, Func<PolyMesh, int> revision)
    {
        var touched = new List<int>();
        if (doc.Definitions.Count == 0) { _seen.Clear(); return touched; }
        var changed = new List<Group>();
        foreach (var g in doc.Groups)
        {
            if (!g.IsInstance || g.Origin == null) continue;
            ulong sig = Signature(doc, g, revision);
            if (!_seen.TryGetValue(g.Id, out var was) || was != sig) changed.Add(g);
            _seen[g.Id] = sig;
        }
        if (changed.Count == 0) return touched;

        foreach (var def in doc.Definitions.ToList())
        {
            // The first changed copy that no longer matches the definition is the one edited.
            foreach (var source in changed.Where(g => g.Instance == def.Id))
            {
                var parts = LocalParts(doc, source, def, touched);
                if (Same(parts, def.Parts)) continue;
                var updated = def with { Parts = parts };
                doc.Definitions = doc.Definitions.Select(d => d.Id == def.Id ? updated : d).ToList();
                foreach (var other in InstanceOps.Copies(doc, def.Id))
                    if (other != source && other.Origin != null) touched.AddRange(InstanceOps.Rebuild(doc, other, updated));
                break;
            }
        }
        return touched; // the caller re-records (Refresh) once the replaced meshes have revisions
    }

    /// <summary>A copy's members, origin and mesh revisions.</summary>
    static ulong Signature(Document doc, Group g, Func<PolyMesh, int> revision)
    {
        var h = new HashCode();
        h.Add(g.Instance);
        h.Add(g.Origin);
        foreach (var id in InstanceOps.MeshIds(doc, g))
        {
            h.Add(id);
            if (doc.Find(id) is { } m) { h.Add(revision(m)); h.Add(m.Part); }
        }
        return (ulong)(uint)h.ToHashCode();
    }

    /// <summary>
    /// The copy's meshes in the definition's space. A mesh without a part key (added to the copy) or with a key another
    /// member already has (a mesh duplicated inside the copy) becomes a new part: it is retagged, as a replacement
    /// object, whose id goes to <paramref name="touched"/>.
    /// </summary>
    static List<DefinitionPart> LocalParts(Document doc, Group copy, InstanceDefinition def, List<int> touched)
    {
        var origin = copy.Origin!.Value;
        int next = def.Parts.Count == 0 ? 0 : def.Parts.Max(p => p.Key);
        var keys = new HashSet<int>();
        var parts = new List<DefinitionPart>();
        foreach (var id in InstanceOps.MeshIds(doc, copy))
        {
            if (doc.Find(id) is not { } mesh) continue;
            if (mesh.Part == 0 || !keys.Add(mesh.Part))
            {
                var tagged = mesh.Clone();
                tagged.Part = ++next;
                keys.Add(tagged.Part);
                InstanceOps.Replace(doc, mesh, tagged);
                touched.Add(id);
                mesh = tagged;
            }
            next = Math.Max(next, mesh.Part);
            parts.Add(new DefinitionPart(mesh.Part, InstanceOps.ToLocal(mesh, origin)));
        }
        return parts;
    }

    // ---- Comparing a copy read back with the definition: float noise from the round trip through its origin isn't an edit ----

    const float Tolerance = 1e-4f;

    static bool Same(IReadOnlyList<DefinitionPart> a, IReadOnlyList<DefinitionPart> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (a[i].Key != b[i].Key || !SameMesh(a[i].Mesh, b[i].Mesh)) return false;
        return true;
    }

    static bool SameMesh(PolyMesh a, PolyMesh b)
    {
        if (a.Name != b.Name || a.Collision != b.Collision || a.Tags != b.Tags || !a.Dressing.Equals(b.Dressing) ||
            a.Positions.Count != b.Positions.Count || a.Faces.Count != b.Faces.Count || !a.SoftEdges.SetEquals(b.SoftEdges)) return false;
        for (int i = 0; i < a.Positions.Count; i++)
            if (!Near(a.Positions[i], b.Positions[i])) return false;
        for (int i = 0; i < a.Faces.Count; i++)
        {
            var x = a.Faces[i];
            var y = b.Faces[i];
            if (x.Material != y.Material || x.Dominant != y.Dominant || x.Tags != y.Tags || !x.Verts.AsSpan().SequenceEqual(y.Verts) ||
                !SameMap(x.Map, y.Map) || (x.UVs == null) != (y.UVs == null)) return false;
            if (x.UVs != null && !x.UVs.AsSpan().SequenceEqual(y.UVs)) return false;
        }
        return true;
    }

    static bool Near(Vector3 a, Vector3 b) => Vector3.DistanceSquared(a, b) <= Tolerance * Tolerance;

    static bool NearF(float a, float b) => MathF.Abs(a - b) <= Tolerance * MathF.Max(1, MathF.Max(MathF.Abs(a), MathF.Abs(b)));

    static bool SameMap(TextureMapping a, TextureMapping b) =>
        Near(a.UAxis, b.UAxis) && Near(a.VAxis, b.VAxis) && NearF(a.ShiftU, b.ShiftU) && NearF(a.ShiftV, b.ShiftV) &&
        NearF(a.ScaleU, b.ScaleU) && NearF(a.ScaleV, b.ScaleV) && NearF(a.Rotation, b.Rotation);
}
