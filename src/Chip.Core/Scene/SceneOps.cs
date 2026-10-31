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

    /// <summary>The entity's name, or when unnamed its class (a reference model: its model's name).</summary>
    public static string DisplayName(Entity e) =>
        !string.IsNullOrWhiteSpace(e.Name) ? e.Name
        : ReferenceModels.Is(e) && ReferenceModels.DisplayName(ReferenceModels.ModelOf(e)) is { Length: > 0 } model ? model
        : e.ClassName;

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

    /// <summary>
    /// Removes the meshes and entities, then any group left empty. A mesh entity takes its meshes with it, and one whose
    /// meshes all go goes too.
    /// </summary>
    public static void Delete(Document doc, IEnumerable<int> ids)
    {
        var set = ids.ToHashSet();
        foreach (var e in doc.Entities.Where(e => set.Contains(e.Id))) set.UnionWith(e.Meshes);
        doc.Meshes.RemoveAll(m => set.Contains(m.Id));
        doc.Entities.RemoveAll(e => set.Contains(e.Id));
        MeshEntityOps.Prune(doc);
        GroupOps.Prune(doc);
    }

    /// <summary>
    /// Copies meshes and entities, returning the copies' ids. Whole groups in the selection are copied as new groups;
    /// other copies join their original's group. References between copied entities point at the copies (a duplicated
    /// pair of doors stays a pair); references to anything else are kept.
    /// </summary>
    public static List<int> Duplicate(Document doc, IEnumerable<int> ids)
    {
        // A mesh entity is copied whole: the copy owns copies of its meshes.
        var set = ids.ToHashSet();
        foreach (var e in doc.Entities.Where(e => set.Contains(e.Id))) set.UnionWith(e.Meshes);
        var (groups, loose) = GroupOps.Cover(doc, set);
        var copies = new List<int>();
        var map = new Dictionary<int, int>();
        foreach (var g in groups) CopyGroup(doc, g, GroupOps.ParentOf(doc, g)?.Id ?? 0, copies, map);
        foreach (var id in loose)
            if (CopyObject(doc, id, map) is { } copy)
            {
                copies.Add(copy);
                if (GroupOps.GroupOf(doc, id) is { } g) g.Members.Add(copy);
            }
        var copiedEntities = map.Values.Select(doc.FindEntity).OfType<Entity>().ToList();
        foreach (var e in copiedEntities)
            e.Meshes = e.Meshes.Select(m => map.TryGetValue(m, out var to) ? to : -1).Where(m => m >= 0).ToList();
        RemapReferences(copiedEntities, map, dropOthers: false);
        return copies;
    }

    static void CopyGroup(Document doc, Group src, int parent, List<int> copies, Dictionary<int, int> map)
    {
        var dst = doc.Add(new Group { Name = src.Name, Parent = parent, Origin = src.Origin, Instance = src.Instance });
        foreach (var id in src.Members)
            if (CopyObject(doc, id, map) is { } copy) { dst.Members.Add(copy); copies.Add(copy); }
        foreach (var child in doc.Groups.Where(c => c.Parent == src.Id && c != dst).ToList()) CopyGroup(doc, child, dst.Id, copies, map);
    }

    static int? CopyObject(Document doc, int id, Dictionary<int, int> map)
    {
        if (doc.Find(id) is { } m) return map[id] = doc.Add(m.Clone()).Id;
        if (doc.FindEntity(id) is { } e) return map[id] = doc.Add(e.Clone()).Id;
        return null;
    }

    /// <summary>
    /// Adds entities that refer to each other by the ids they had elsewhere (an imported file): each gets a fresh id and
    /// its references follow. References to ids outside the batch are removed, since here they'd name something else.
    /// </summary>
    public static void AddEntities(Document doc, IEnumerable<Entity> entities)
    {
        var added = new List<Entity>();
        var map = new Dictionary<int, int>();
        foreach (var e in entities)
        {
            int old = e.Id;
            doc.Add(e);
            map.TryAdd(old, e.Id);
            added.Add(e);
        }
        RemapReferences(added, map, dropOthers: true);
    }

    /// <summary>
    /// Points <c>{ "entity": id }</c> values (and those in lists), connection targets and connection arguments through
    /// <paramref name="map"/>; unmapped ones stay, or go when <paramref name="dropOthers"/> (a connection with them).
    /// </summary>
    public static void RemapReferences(IEnumerable<Entity> entities, IReadOnlyDictionary<int, int> map, bool dropOthers)
    {
        int? Map(int id) => map.TryGetValue(id, out var to) ? to : dropOthers ? null : id;
        foreach (var e in entities)
        {
            Remap(e.Properties);
            foreach (var c in e.Outputs.ToList())
            {
                if (c.Target.Kind == TargetKind.Entity)
                {
                    if (Map(c.Target.Entity) is not { } to) { e.Outputs.Remove(c); continue; }
                    c.Target = c.Target with { Entity = to };
                }
                Remap(c.Args);
            }
        }

        void Remap(Dictionary<string, System.Text.Json.JsonElement> values)
        {
            foreach (var (key, value) in values.ToList())
            {
                var mapped = EntityValues.MapRefs(value, Map, out _);
                if (mapped is { } v) values[key] = v;
                else values.Remove(key);
            }
        }
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
