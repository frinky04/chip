using System.Numerics;
using Chip.Core.Geometry;

namespace Chip.Core.Scene;

/// <summary>
/// Linked copies (<see cref="InstanceDefinition"/>): making them, placing them, breaking the link. A copy is a group with
/// an origin and an <see cref="Group.Instance"/>; its meshes are built from the definition's parts and stay ordinary
/// meshes, so every tool works on them. Edits expect to run inside a <see cref="Session"/> step, whose
/// <see cref="InstanceSync"/> carries an edit of one copy to the others.
/// </summary>
public static class InstanceOps
{
    /// <summary>The linked copy an object belongs to (its innermost instance group), or null.</summary>
    public static Group? CopyOf(Document doc, int objectId)
    {
        if (doc.Definitions.Count == 0) return null;
        for (var g = GroupOps.GroupOf(doc, objectId); g != null; g = GroupOps.ParentOf(doc, g))
            if (g.IsInstance) return g;
        return null;
    }

    /// <summary>
    /// The ids of a copy's meshes, including those in groups nested in it (grouping meshes inside a copy doesn't take
    /// them out of it).
    /// </summary>
    public static List<int> MeshIds(Document doc, Group copy)
    {
        if (!doc.Groups.Any(g => g.Parent == copy.Id)) return copy.Members.Where(id => doc.Find(id) != null).ToList();
        return GroupOps.Subtree(doc, copy).SelectMany(g => g.Members).Where(id => doc.Find(id) != null).Distinct().ToList();
    }

    /// <summary>Every linked copy of a definition, in document order.</summary>
    public static List<Group> Copies(Document doc, int definitionId) => doc.Groups.Where(g => g.Instance == definitionId).ToList();

    /// <summary>
    /// The copies the selection touches: whole copies in object mode, and the copy each touched mesh is in otherwise.
    /// </summary>
    public static List<Group> SelectedCopies(Document doc, Selection sel)
    {
        var result = new List<Group>();
        if (doc.Definitions.Count == 0) return result;
        foreach (var id in sel.MeshIds())
            if (CopyOf(doc, id) is { } g && !result.Contains(g)) result.Add(g);
        return result;
    }

    /// <summary>
    /// Turns the objects into a definition and its first linked copy: the group they make (or the one whole group they
    /// are) becomes the copy, with its origin at their bottom centre unless it has one. Only meshes go into a
    /// definition: null (and nothing changes) when the objects include entities, a mesh entity's meshes, or already
    /// linked copies. Returns the copy.
    /// </summary>
    public static Group? Make(Document doc, IEnumerable<int> ids, string? name = null)
    {
        var set = ids.Where(id => SceneOps.IsObject(doc, id)).ToHashSet();
        if (set.Count == 0) return null;
        if (set.Any(id => doc.Find(id) == null || MeshEntityOps.OwnerOf(doc, id) != null || CopyOf(doc, id) != null)) return null;
        var (groups, loose) = GroupOps.Cover(doc, set);
        // One whole plain group with nothing nested becomes the copy itself; anything else is grouped first.
        Group? copy = groups.Count == 1 && loose.Count == 0 && !groups[0].Folder && GroupOps.Subtree(doc, groups[0]).Count == 1 ? groups[0] : null;
        if (copy == null)
        {
            // Nested groups would be flattened by the definition; take their meshes out into the new group.
            foreach (var g in groups.SelectMany(g => GroupOps.Subtree(doc, g))) g.Members.RemoveAll(set.Contains);
            copy = GroupOps.Create(doc, set, name)!;
            foreach (var id in set)
                if (!copy.Members.Contains(id)) copy.Members.Add(id);
            GroupOps.Prune(doc);
        }
        if (name != null) copy.Name = name;
        var target = new OriginTarget(copy, null);
        if (copy.Origin == null) OriginOps.Place(doc, [target], OriginPlacement.BottomCentre);
        var origin = copy.Origin!.Value;

        int defId = doc.NextId++;
        var parts = new List<DefinitionPart>();
        int key = 0;
        foreach (var memberId in copy.Members.ToList())
        {
            if (doc.Find(memberId) is not { } mesh) continue;
            var tagged = mesh.Clone();
            tagged.Part = ++key;
            tagged.Origin = null;
            Replace(doc, mesh, tagged);
            parts.Add(new DefinitionPart(key, ToLocal(tagged, origin)));
        }
        copy.Instance = defId;
        doc.Definitions = [.. doc.Definitions, new InstanceDefinition(defId, copy.Name, parts)];
        return copy;
    }

    /// <summary>
    /// A new linked copy of <paramref name="definition"/> at <paramref name="origin"/>, as a top-level group (or under
    /// <paramref name="parent"/>). Its meshes are fresh objects with fresh ids.
    /// </summary>
    public static Group Place(Document doc, InstanceDefinition definition, ObjectOrigin origin, int parent = 0)
    {
        var copy = doc.Add(new Group { Name = definition.Name, Parent = parent, Origin = origin, Instance = definition.Id });
        foreach (var part in definition.Parts) copy.Members.Add(doc.Add(Realize(part, origin)).Id);
        return copy;
    }

    /// <summary>
    /// Breaks the link: the copies become plain groups of plain meshes (they keep their origins). A definition with no
    /// copies left goes.
    /// </summary>
    public static int MakeUnique(Document doc, IEnumerable<Group> copies)
    {
        int n = 0;
        foreach (var g in copies.Where(g => g.IsInstance).ToList())
        {
            g.Instance = 0;
            foreach (var id in MeshIds(doc, g))
                if (doc.Find(id) is { Part: not 0 } mesh)
                {
                    var plain = mesh.Clone();
                    plain.Part = 0;
                    Replace(doc, mesh, plain);
                }
            n++;
        }
        PruneDefinitions(doc);
        return n;
    }

    /// <summary>Drops definitions no copy uses any more (the last copy was deleted, ungrouped or made unique).</summary>
    public static void PruneDefinitions(Document doc)
    {
        if (doc.Definitions.Count == 0) return;
        var used = doc.Groups.Where(g => g.IsInstance).Select(g => g.Instance).ToHashSet();
        if (doc.Definitions.All(d => used.Contains(d.Id))) return;
        doc.Definitions = doc.Definitions.Where(d => used.Contains(d.Id)).ToList();
    }

    /// <summary>A part placed in the world at <paramref name="origin"/>: a new mesh (id 0) tagged with the part's key.</summary>
    public static PolyMesh Realize(DefinitionPart part, ObjectOrigin origin)
    {
        var m = Transformed(part.Mesh, origin.World);
        m.Part = part.Key;
        return m;
    }

    /// <summary>A copy's mesh in the definition's space (relative to the copy's origin), as a new untagged mesh with id 0.</summary>
    public static PolyMesh ToLocal(PolyMesh mesh, ObjectOrigin origin)
    {
        var m = Transformed(mesh, origin.Local);
        m.Part = 0;
        return m;
    }

    /// <summary>
    /// A copy of <paramref name="mesh"/> moved rigidly by <paramref name="rigid"/>, projected textures moving with it.
    /// Per-copy state (id, visibility, lock, own origin) is cleared.
    /// </summary>
    static PolyMesh Transformed(PolyMesh mesh, Matrix4x4 rigid)
    {
        var m = mesh.Clone();
        m.Id = 0;
        m.Hidden = m.Locked = false;
        m.Origin = null;
        for (int i = 0; i < m.Positions.Count; i++) m.Positions[i] = Vector3.Transform(m.Positions[i], rigid);
        foreach (var f in m.Faces) f.Map = f.Map.MovedRigidly(rigid);
        return m;
    }

    /// <summary>Puts <paramref name="with"/> in <paramref name="old"/>'s place (same id and list position).</summary>
    internal static void Replace(Document doc, PolyMesh old, PolyMesh with)
    {
        with.Id = old.Id;
        int i = doc.Meshes.IndexOf(old);
        if (i >= 0) doc.Meshes[i] = with;
    }

    /// <summary>
    /// Rebuilds a copy from the definition: each part's mesh is replaced by a new object (so undo snapshots keep the old
    /// one), parts the copy lacks are added, and meshes whose part is gone are removed. The copy's own visibility and
    /// lock stay. Returns the ids of the meshes replaced or removed.
    /// </summary>
    internal static IEnumerable<int> Rebuild(Document doc, Group copy, InstanceDefinition def)
    {
        var origin = copy.Origin!.Value;
        var byKey = new Dictionary<int, PolyMesh>();
        foreach (var id in MeshIds(doc, copy))
            if (doc.Find(id) is { Part: not 0 } m) byKey.TryAdd(m.Part, m);
        var touched = new List<int>();
        foreach (var part in def.Parts)
        {
            var built = Realize(part, origin);
            if (byKey.Remove(part.Key, out var old))
            {
                built.Hidden = old.Hidden;
                built.Locked = old.Locked;
                Replace(doc, old, built);
                touched.Add(old.Id);
            }
            else copy.Members.Add(doc.Add(built).Id);
        }
        // Parts the definition no longer has (deleted from the copy that changed) go from this copy too.
        foreach (var m in byKey.Values)
        {
            doc.Meshes.Remove(m);
            touched.Add(m.Id); // the session drops the id from whichever group held it
        }
        return touched;
    }
}
