namespace Chip.Core.Scene;

/// <summary>
/// A named set of objects (meshes and entities) shown as a folder in the outliner. Groups nest through
/// <see cref="Parent"/> (0 = top level). An object belongs to at most one group. <see cref="Hidden"/> and
/// <see cref="Locked"/> record the last state set on the group; setting them pushes the flag down to every member,
/// whose own flags are what rendering and picking read.
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

    public Group Clone() => new()
    {
        Id = Id, Name = Name, Parent = Parent, Members = new List<int>(Members), Hidden = Hidden, Locked = Locked,
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
    /// Objects not covered by such a group are returned separately.
    /// </summary>
    public static (List<Group> Groups, List<int> Loose) Cover(Document doc, IEnumerable<int> ids)
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
                best = g;
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

    /// <summary>"Group 3": the first free number.</summary>
    public static string NextName(Document doc)
    {
        var names = doc.Groups.Select(g => g.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; ; i++)
            if (!names.Contains($"Group {i}")) return $"Group {i}";
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
            if (GroupOf(doc, id) is { } g) MoveTo(doc, id, ParentOf(doc, g));
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
    /// Selecting a grouped object selects its whole group: every id is replaced by all members of its outermost group.
    /// Locked or hidden members stay out (they can't be selected).
    /// </summary>
    public static HashSet<int> ExpandToGroups(Document doc, IEnumerable<int> ids)
    {
        var result = new HashSet<int>();
        foreach (var id in ids)
        {
            result.Add(id);
            if (GroupOf(doc, id) is not { } g) continue;
            var top = Ancestry(doc, g)[^1];
            result.UnionWith(AllMembers(doc, top).Where(m => SceneOps.IsSelectable(doc, m)));
        }
        return result;
    }

    /// <summary>Drops members that no longer exist, then groups left with nothing in them (innermost first).</summary>
    public static void Prune(Document doc)
    {
        foreach (var g in doc.Groups) g.Members.RemoveAll(id => !SceneOps.IsObject(doc, id));
        foreach (var g in doc.Groups.Where(g => g.Parent != 0 && doc.FindGroup(g.Parent) == null)) g.Parent = 0;
        bool removed;
        do
        {
            removed = doc.Groups.RemoveAll(g => g.Members.Count == 0 && !doc.Groups.Any(c => c.Parent == g.Id)) > 0;
        } while (removed);
    }
}
