using System.Numerics;

namespace Chip.Core.Scene;

/// <summary>
/// Mesh entities (Hammer's brush entities): an <see cref="Entity"/> that owns meshes. Tying meshes to a class makes one;
/// its origin starts at the bottom centre of the meshes. Selecting any part selects the whole entity, so it moves, turns
/// and duplicates as one. Every edit expects to run inside a <see cref="Session"/> step.
/// </summary>
public static class MeshEntityOps
{
    /// <summary>The mesh entity a mesh belongs to, or null.</summary>
    public static Entity? OwnerOf(Document doc, int meshId) => doc.Entities.Find(e => e.Meshes.Contains(meshId));

    /// <summary>
    /// Makes a mesh entity of <paramref name="className"/> from the meshes (taking them from any entity they belonged
    /// to; an entity left without meshes goes). Null when none of the ids is a mesh.
    /// </summary>
    public static Entity? Tie(Document doc, IEnumerable<int> ids, string className)
    {
        var meshes = ids.Select(doc.Find).OfType<Geometry.PolyMesh>().Distinct().ToList();
        if (meshes.Count == 0) return null;
        foreach (var m in meshes) Release(doc, m.Id);
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var p in meshes.SelectMany(m => m.Positions)) { min = Vector3.Min(min, p); max = Vector3.Max(max, p); }
        return doc.Add(new Entity
        {
            ClassName = className.Trim(),
            Position = new Vector3((min.X + max.X) * 0.5f, min.Y, (min.Z + max.Z) * 0.5f),
            Meshes = meshes.Select(m => m.Id).ToList(),
        });
    }

    /// <summary>Turns a mesh entity back into plain meshes: the entity goes, its meshes stay where they are.</summary>
    public static void Untie(Document doc, Entity e)
    {
        doc.Entities.Remove(e);
        GroupOps.Prune(doc);
    }

    /// <summary>Takes a mesh out of its entity; the entity goes when it has no meshes left.</summary>
    static void Release(Document doc, int meshId)
    {
        if (OwnerOf(doc, meshId) is not { } owner) return;
        owner.Meshes.Remove(meshId);
        if (owner.Meshes.Count == 0) doc.Entities.Remove(owner);
    }

    /// <summary>
    /// Selecting part of a mesh entity selects all of it: a mesh adds its entity and the entity's other meshes, an entity
    /// adds its meshes. Hidden or locked parts stay out.
    /// </summary>
    public static void Expand(Document doc, HashSet<int> ids)
    {
        foreach (var id in ids.ToList())
        {
            var owner = doc.FindEntity(id) is { IsMeshEntity: true } e ? e : OwnerOf(doc, id);
            if (owner == null) continue;
            if (SceneOps.IsSelectable(doc, owner.Id)) ids.Add(owner.Id);
            foreach (var m in owner.Meshes)
                if (SceneOps.IsSelectable(doc, m)) ids.Add(m);
        }
    }

    /// <summary>
    /// After meshes are deleted: entities lose the ids, and an entity that had meshes and has none left goes too
    /// (deleting a door's geometry deletes the door).
    /// </summary>
    public static void Prune(Document doc)
    {
        foreach (var e in doc.Entities.Where(e => e.IsMeshEntity).ToList())
        {
            e.Meshes.RemoveAll(id => doc.Find(id) == null);
            if (e.Meshes.Count == 0) doc.Entities.Remove(e);
        }
    }
}
