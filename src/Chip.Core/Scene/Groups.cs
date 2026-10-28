namespace Chip.Core.Scene;

/// <summary>
/// A named set of objects (meshes and entities) in the outliner. Groups nest through <see cref="Parent"/> (0 = top level).
/// An object belongs to at most one group. A plain group is Hammer's: picking a member selects all of it. A
/// <see cref="Folder"/> only organises (Unreal's outliner folders): its members select one by one, and it stays when
/// emptied. <see cref="Hidden"/> and <see cref="Locked"/> record the last state set on the group; setting them pushes the
/// flag down to every member, whose own flags are what rendering and picking read.
/// </summary>
public sealed class Group
{
    public int Id;
    public string Name = "Group";
    public int Parent;
    /// <summary>Direct member object ids (meshes or entities). Ids that no longer resolve are ignored.</summary>
    public List<int> Members = new();
    public bool Hidden;
    public bool Locked;
    /// <summary>Organises without binding: members select on their own, and an empty folder isn't pruned.</summary>
    public bool Folder;

    public Group Clone() => new()
    {
        Id = Id, Name = Name, Parent = Parent, Members = new List<int>(Members), Hidden = Hidden, Locked = Locked, Folder = Folder,
    };
}

/// <summary>Group queries and edits. Every edit expects to run inside a <see cref="Session"/> step.</summary>
public static class GroupOps
{
    /// <summary>The group an object directly belongs to.</summary>
    public static Group? GroupOf(Document doc, int objectId) => doc.Groups.Find(g => g.Members.Contains(objectId));

    public static Group? ParentOf(Document doc, Group g) => g.Parent == 0 ? null : doc.FindGroup(g.Parent);

    /// <summary>Groups directly inside <paramref name="parent"/> (null = top level; a missing parent counts as top level).</summary>
    public static IEnumerable<Group> Children(Document doc, Group? parent)
    {
        int id = parent?.Id ?? 0;
        return doc.Groups.Where(g => g != parent && (ParentOf(doc, g)?.Id ?? 0) == id);
    }

    /// <summary>The group itself and every group nested in it, parents first.</summary>
    public static List<Group> Subtree(Document doc, Group root)
    {
        var result = new List<Group> { root };
        for (int i = 0; i < result.Count; i++)
            foreach (var c in doc.Groups)
                if (c.Parent == result[i].Id && !result.Contains(c)) result.Add(c);
        return result;
    }

    /// <summary>Every object in the group or its nested groups that still exists.</summary>
    public static List<int> AllMembers(Document doc, Group g) =>
        Subtree(doc, g).SelectMany(s => s.Members).Where(id => SceneOps.IsObject(doc, id)).Distinct().ToList();

    /// <summary>Nesting chain from <paramref name="g"/> up to its top-level group.</summary>
    public static List<Group> Ancestry(Document doc, Group g)
    {
        var chain = new List<Group>();
        for (var c = g; c != null && !chain.Contains(c); c = ParentOf(doc, c)) chain.Add(c);
        return chain;
    }

    public static bool IsInside(Document doc, Group g, Group container) => Ancestry(doc, g).Contains(container);

    /// <summary>
    /// The outermost groups whose every member is in <paramref name="ids"/>: what a selection "is" when it covers whole groups.
    /// Objects not covered by such a group are returned separately. Folders are looked through (a selection never "is" a
    /// folder, so grouping or duplicating what's in one leaves the folder alone) unless <paramref name="folders"/>.
    /// </summary>
    public static (List<Group> Groups, List<int> Loose) Cover(Document doc, IEnumerable<int> ids, bool folders = false)
    {
        var set = ids.Where(id => SceneOps.IsObject(doc, id)).ToHashSet();
        var groups = new List<Group>();
        var loose = new List<int>();
        foreach (var id in set)
        {
            Group? best = null;
            for (var g = GroupOf(doc, id); g != null; g = ParentOf(doc, g))
            {
                var all = AllMembers(doc, g);
                if (all.Count == 0 || !all.All(set.Contains)) break;
                if (folders || !g.Folder) best = g;
            }
            if (best == null) loose.Add(id);
            else if (!groups.Contains(best)) groups.Add(best);
        }
        return (groups, loose);
    }

    /// <summary>
    /// Groups the objects (Ctrl+G). Whole groups inside the selection nest under the new group instead of being split up.
    /// The new group goes where all the grouped items already sat (or at top level). Null when nothing was given.
    /// </summary>
    public static Group? Create(Document doc, IEnumerable<int> ids, string? name = null)
    {
        var (groups, loose) = Cover(doc, ids);
        if (groups.Count + loose.Count == 0) return null;
        var parents = groups.Select(g => ParentOf(doc, g)?.Id ?? 0).Concat(loose.Select(id => GroupOf(doc, id)?.Id ?? 0)).Distinct().ToList();
        var group = doc.Add(new Group { Name = name ?? NextName(doc), Parent = parents.Count == 1 ? parents[0] : 0 });
        foreach (var g in groups) g.Parent = group.Id;
        foreach (var id in loose) AddMember(doc, group, id);
        return group;
    }

    /// <summary>"Group 3" (or "Folder 3"): the first free number.</summary>
    public static string NextName(Document doc, bool folder = false)
    {
        var names = doc.Groups.Select(g => g.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string stem = folder ? "Folder" : "Group";
        for (int i = 1; ; i++)
            if (!names.Contains($"{stem} {i}")) return $"{stem} {i}";
    }

    /// <summary>
    /// Puts the objects in a new folder. Whole groups and folders in the selection move in intact; the folder goes where
    /// all of them already sat (or at the top level). With no objects it makes an empty folder under <paramref name="parent"/>.
    /// </summary>
    public static Group CreateFolder(Document doc, IEnumerable<int> ids, string? name = null, Group? parent = null)
    {
        var (groups, loose) = Cover(doc, ids, folders: true);
        var parents = groups.Select(g => ParentOf(doc, g)?.Id ?? 0).Concat(loose.Select(id => GroupOf(doc, id)?.Id ?? 0)).Distinct().ToList();
        int at = groups.Count + loose.Count == 0 ? parent?.Id ?? 0 : parents.Count == 1 ? parents[0] : 0;
        var folder = doc.Add(new Group { Name = name ?? NextName(doc, folder: true), Parent = at, Folder = true });
        foreach (var g in groups) g.Parent = folder.Id;
        foreach (var id in loose) AddMember(doc, folder, id);
        return folder;
    }

    /// <summary>
    /// Ungroups (Ctrl+Alt+G): dissolves the outermost groups the objects fully cover, moving their contents up one level.
    /// Loose objects inside a partly selected group move up out of it. Returns how many groups were dissolved.
    /// </summary>
    public static int Ungroup(Document doc, IEnumerable<int> ids)
    {
        var (groups, loose) = Cover(doc, ids);
        foreach (var g in groups) Dissolve(doc, g);
        foreach (var id in loose)
            if (GroupOf(doc, id) is { Folder: false } g) MoveTo(doc, id, ParentOf(doc, g));
        Prune(doc);
        return groups.Count;
    }

    /// <summary>Removes a group, handing its members and nested groups to its parent.</summary>
    public static void Dissolve(Document doc, Group g)
    {
        var parent = ParentOf(doc, g);
        foreach (var c in doc.Groups.Where(c => c.Parent == g.Id)) c.Parent = parent?.Id ?? 0;
        if (parent != null) parent.Members.AddRange(g.Members.Where(id => !parent.Members.Contains(id)));
        doc.Groups.Remove(g);
    }

    /// <summary>Puts an object directly into <paramref name="target"/> (null = top level), leaving its old group.</summary>
    public static void MoveTo(Document doc, int objectId, Group? target)
    {
        foreach (var g in doc.Groups) g.Members.Remove(objectId);
        if (target != null) AddMember(doc, target, objectId);
    }

    /// <summary>
    /// Moves a group under <paramref name="target"/> (null = top level). Refused (false) when that would put a group inside
    /// itself.
    /// </summary>
    public static bool MoveTo(Document doc, Group g, Group? target)
    {
        if (target != null && (target == g || IsInside(doc, target, g))) return false;
        g.Parent = target?.Id ?? 0;
        return true;
    }

    static void AddMember(Document doc, Group group, int id)
    {
        foreach (var g in doc.Groups) g.Members.Remove(id);
        group.Members.Add(id);
    }

    /// <summary>Hides or shows the group, its nested groups and all their members.</summary>
    public static void SetHidden(Document doc, Group g, bool hidden)
    {
        foreach (var s in Subtree(doc, g)) s.Hidden = hidden;
        SceneOps.SetHidden(doc, AllMembers(doc, g), hidden);
    }

    /// <summary>Locks or unlocks the group, its nested groups and all their members.</summary>
    public static void SetLocked(Document doc, Group g, bool locked)
    {
        foreach (var s in Subtree(doc, g)) s.Locked = locked;
        SceneOps.SetLocked(doc, AllMembers(doc, g), locked);
    }

    /// <summary>
    /// Selecting a grouped object selects its whole group: every id is replaced by all members of its outermost group
    /// (<see cref="UnitOf"/>; folders don't count). Locked or hidden members stay out (they can't be selected).
    /// </summary>
    public static HashSet<int> ExpandToGroups(Document doc, IEnumerable<int> ids)
    {
        var result = new HashSet<int>();
        foreach (var id in ids)
        {
            result.Add(id);
            if (UnitOf(doc, id) is not { } top) continue;
            result.UnionWith(AllMembers(doc, top).Where(m => SceneOps.IsSelectable(doc, m)));
        }
        return result;
    }

    /// <summary>The outermost group (not folder) around an object: what selecting it selects. Null when there is none.</summary>
    public static Group? UnitOf(Document doc, int objectId) =>
        GroupOf(doc, objectId) is { } g ? Ancestry(doc, g).LastOrDefault(a => !a.Folder) : null;

    /// <summary>Drops members that no longer exist, then groups (not folders) left with nothing in them (innermost first).</summary>
    public static void Prune(Document doc)
    {
        foreach (var g in doc.Groups) g.Members.RemoveAll(id => !SceneOps.IsObject(doc, id));
        foreach (var g in doc.Groups.Where(g => g.Parent != 0 && doc.FindGroup(g.Parent) == null)) g.Parent = 0;
        bool removed;
        do
        {
            removed = doc.Groups.RemoveAll(g => !g.Folder && g.Members.Count == 0 && !doc.Groups.Any(c => c.Parent == g.Id)) > 0;
        } while (removed);
    }
}
