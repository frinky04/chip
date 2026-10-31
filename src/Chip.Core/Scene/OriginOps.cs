using System.Numerics;
using Chip.Core.Geometry;

namespace Chip.Core.Scene;

/// <summary>Where <see cref="OriginOps.Place"/> puts an origin, measured in the origin's own axes.</summary>
public enum OriginPlacement { BottomCentre, Centre }

/// <summary>
/// What owns an origin: a plain group (one that selects as a unit) or a loose mesh. A mesh entity's meshes have none of
/// their own; the entity's position and rotation are their origin.
/// </summary>
public sealed record OriginTarget(Group? Group, PolyMesh? Mesh)
{
    public ObjectOrigin? Origin
    {
        get => Group != null ? Group.Origin : Mesh!.Origin;
        set { if (Group != null) Group.Origin = value; else Mesh!.Origin = value; }
    }

    public string Name => Group?.Name ?? Mesh!.Name;

    /// <summary>The meshes the origin's object is made of.</summary>
    public List<PolyMesh> Meshes(Document doc) => Mesh != null ? [Mesh] : GroupOps.AllMembers(doc, Group!).Select(doc.Find).OfType<PolyMesh>().ToList();

    /// <summary>Every point the object is made of: mesh vertices and the positions of entities in a group.</summary>
    public IEnumerable<Vector3> Points(Document doc)
    {
        if (Mesh != null) return Mesh.Positions;
        var ids = GroupOps.AllMembers(doc, Group!);
        return ids.Select(doc.Find).OfType<PolyMesh>().SelectMany(m => m.Positions)
            .Concat(ids.Select(doc.FindEntity).OfType<Entity>().Where(e => !e.IsMeshEntity).Select(e => e.Position));
    }
}

/// <summary>
/// Object origins (<see cref="ObjectOrigin"/>): which object a selection's origin belongs to, and setting it. Origins are
/// opt-in: an object without one keeps the bounds-centre pivot and exports at the world origin. Edits expect to run
/// inside a <see cref="Session"/> step.
/// </summary>
public static class OriginOps
{
    /// <summary>
    /// The objects whose origins an origin command edits. Object mode: the whole groups the selection covers and its
    /// loose meshes. Component modes: the object each touched mesh belongs to (its outermost group, else itself), so
    /// "origin to selection" on a vertex of a grouped prop moves the prop's origin. Meshes of mesh entities are skipped.
    /// </summary>
    public static List<OriginTarget> Targets(Document doc, Selection sel)
    {
        var result = new List<OriginTarget>();
        if (sel.IsEmpty) return result;
        if (sel.Mode == SelectMode.Object)
        {
            var (groups, loose) = GroupOps.Cover(doc, sel.MeshIds());
            result.AddRange(groups.Select(g => new OriginTarget(g, null)));
            foreach (var id in loose)
                if (doc.Find(id) is { } m && MeshEntityOps.OwnerOf(doc, id) == null) result.Add(new OriginTarget(null, m));
            return result;
        }
        foreach (var id in sel.MeshIds())
        {
            if (doc.Find(id) is not { } m || MeshEntityOps.OwnerOf(doc, id) != null) continue;
            var t = GroupOps.UnitOf(doc, id) is { } g ? new OriginTarget(g, null) : new OriginTarget(null, m);
            if (!result.Contains(t)) result.Add(t);
        }
        return result;
    }

    /// <summary>
    /// The origin the selection pivots on and takes its local axes from, when it is one object that has one: a loose
    /// mesh or whole group with an origin, or one mesh entity (its position and rotation). Object mode only; null
    /// otherwise, so the pivot falls back to the bounds centre.
    /// </summary>
    public static ObjectOrigin? UnitOrigin(Document doc, Selection sel)
    {
        if (sel.Mode != SelectMode.Object || sel.IsEmpty) return null;
        var ids = sel.MeshIds();
        // Cheap checks first: most selections have no origin anywhere, and this runs every frame.
        bool candidate = doc.Groups.Any(g => g.Origin != null);
        if (!candidate)
            foreach (var id in ids)
                if (doc.Find(id) is { Origin: not null } || doc.FindEntity(id) is { IsMeshEntity: true }) { candidate = true; break; }
        if (!candidate) return null;

        var (groups, loose) = GroupOps.Cover(doc, ids);
        if (groups.Count == 1 && loose.Count == 0) return groups[0].Origin;
        if (groups.Count > 0 || loose.Count == 0) return null;
        if (loose.Count == 1 && doc.Find(loose[0]) is { } mesh) return mesh.Origin;
        // One mesh entity: the entity and its meshes (whichever of them can be selected).
        var owner = doc.FindEntity(loose[0]) is { IsMeshEntity: true } e ? e : MeshEntityOps.OwnerOf(doc, loose[0]);
        if (owner == null || !loose.All(id => id == owner.Id || owner.Meshes.Contains(id))) return null;
        return new ObjectOrigin(owner.Position, EntityOps.Orientation(owner));
    }

    /// <summary>
    /// Puts each target's origin at the bottom centre or centre of its bounds, measured along the origin's own axes (a
    /// turned crate's bottom is its own bottom). The rotation is kept; a target without an origin gets an unrotated one.
    /// </summary>
    public static int Place(Document doc, IEnumerable<OriginTarget> targets, OriginPlacement placement)
    {
        int n = 0;
        foreach (var t in targets)
        {
            var rotation = t.Origin?.Rotation ?? Quaternion.Identity;
            var toLocal = Quaternion.Conjugate(rotation);
            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            bool any = false;
            foreach (var p in t.Points(doc))
            {
                var q = Vector3.Transform(p, toLocal);
                min = Vector3.Min(min, q);
                max = Vector3.Max(max, q);
                any = true;
            }
            if (!any) continue;
            var local = (min + max) * 0.5f;
            if (placement == OriginPlacement.BottomCentre) local.Y = min.Y;
            t.Origin = new ObjectOrigin(Vector3.Transform(local, rotation), rotation);
            n++;
        }
        return n;
    }

    /// <summary>Moves each target's origin to <paramref name="position"/>, keeping its rotation (or none).</summary>
    public static void MoveTo(IEnumerable<OriginTarget> targets, Vector3 position)
    {
        foreach (var t in targets) t.Origin = new ObjectOrigin(position, t.Origin?.Rotation ?? Quaternion.Identity);
    }

    /// <summary>Removes the origins: the objects go back to the bounds-centre pivot and export at the world origin.</summary>
    public static int Clear(IEnumerable<OriginTarget> targets)
    {
        int n = 0;
        foreach (var t in targets)
            if (t.Origin != null) { t.Origin = null; n++; }
        return n;
    }
}
