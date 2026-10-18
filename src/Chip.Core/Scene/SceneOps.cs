namespace Chip.Core.Scene;

/// <summary>
/// Object-level edits that treat meshes and entities alike (ids are unique across both): delete, duplicate, hide, lock,
/// rename. Every edit expects to run inside a <see cref="Session"/> step.
/// </summary>
public static class SceneOps
{
    public static bool IsObject(Document doc, int id) => doc.Find(id) != null || doc.FindEntity(id) != null;

    /// <summary>Visible and unlocked: can be picked and selected.</summary>
    public static bool IsSelectable(Document doc, int id) =>
        doc.Find(id) is { } m ? !m.Hidden && !m.Locked : doc.FindEntity(id) is { } e && !e.Hidden && !e.Locked;

    public static string? NameOf(Document doc, int id) =>
        doc.Find(id)?.Name ?? (doc.FindEntity(id) is { } e ? DisplayName(e) : doc.FindGroup(id)?.Name);

    /// <summary>The entity's name, or its class when unnamed.</summary>
    public static string DisplayName(Entity e) => string.IsNullOrWhiteSpace(e.Name) ? e.ClassName : e.Name;

    /// <summary>Renames a mesh, entity or group. Returns false when the id is unknown.</summary>
    public static bool Rename(Document doc, int id, string name)
    {
        name = name.Trim();
        if (doc.Find(id) is { } m) m.Name = name;
        else if (doc.FindEntity(id) is { } e) e.Name = name;
        else if (doc.FindGroup(id) is { } g) g.Name = name.Length > 0 ? name : g.Name;
        else return false;
        return true;
    }

    /// <summary>Removes the meshes and entities, then any group left empty.</summary>
    public static void Delete(Document doc, IEnumerable<int> ids)
    {
        var set = ids.ToHashSet();
        doc.Meshes.RemoveAll(m => set.Contains(m.Id));
        doc.Entities.RemoveAll(e => set.Contains(e.Id));
        GroupOps.Prune(doc);
    }

    /// <summary>
    /// Copies meshes and entities, returning the copies' ids. Whole groups in the selection are copied as new groups;
    /// other copies join their original's group.
    /// </summary>
    public static List<int> Duplicate(Document doc, IEnumerable<int> ids)
    {
        var (groups, loose) = GroupOps.Cover(doc, ids);
        var copies = new List<int>();
        foreach (var g in groups) CopyGroup(doc, g, GroupOps.ParentOf(doc, g)?.Id ?? 0, copies);
        foreach (var id in loose)
            if (CopyObject(doc, id) is { } copy)
            {
                copies.Add(copy);
                if (GroupOps.GroupOf(doc, id) is { } g) g.Members.Add(copy);
            }
        return copies;
    }

    static void CopyGroup(Document doc, Group src, int parent, List<int> copies)
    {
        var dst = doc.Add(new Group { Name = src.Name, Parent = parent });
        foreach (var id in src.Members)
            if (CopyObject(doc, id) is { } copy) { dst.Members.Add(copy); copies.Add(copy); }
        foreach (var child in doc.Groups.Where(c => c.Parent == src.Id && c != dst).ToList()) CopyGroup(doc, child, dst.Id, copies);
    }

    static int? CopyObject(Document doc, int id)
    {
        if (doc.Find(id) is { } m) return doc.Add(m.Clone()).Id;
        if (doc.FindEntity(id) is { } e) return doc.Add(e.Clone()).Id;
        return null;
    }

    public static void SetHidden(Document doc, IEnumerable<int> ids, bool hidden)
    {
        foreach (var id in ids)
            if (doc.Find(id) is { } m) m.Hidden = hidden;
            else if (doc.FindEntity(id) is { } e) e.Hidden = hidden;
    }

    public static void SetLocked(Document doc, IEnumerable<int> ids, bool locked)
    {
        foreach (var id in ids)
            if (doc.Find(id) is { } m) m.Locked = locked;
            else if (doc.FindEntity(id) is { } e) e.Locked = locked;
    }

    public static bool AnyHidden(Document doc) =>
        doc.Meshes.Any(m => m.Hidden) || doc.Entities.Any(e => e.Hidden) || doc.Groups.Any(g => g.Hidden);

    public static bool AnyLocked(Document doc) =>
        doc.Meshes.Any(m => m.Locked) || doc.Entities.Any(e => e.Locked) || doc.Groups.Any(g => g.Locked);

    /// <summary>Shows every mesh, entity and group.</summary>
    public static void UnhideAll(Document doc)
    {
        foreach (var m in doc.Meshes) m.Hidden = false;
        foreach (var e in doc.Entities) e.Hidden = false;
        foreach (var g in doc.Groups) g.Hidden = false;
    }

    /// <summary>Unlocks every mesh, entity and group.</summary>
    public static void UnlockAll(Document doc)
    {
        foreach (var m in doc.Meshes) m.Locked = false;
        foreach (var e in doc.Entities) e.Locked = false;
        foreach (var g in doc.Groups) g.Locked = false;
    }
}
