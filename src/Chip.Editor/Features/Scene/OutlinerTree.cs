using Chip.Core.Scene;

namespace Chip.Editor.Features.Scene;

public enum OutlinerKind { Group, Folder, Mesh, Entity }

/// <summary>Which objects the outliner lists (the folders and groups holding them stay).</summary>
public enum OutlinerShow { All, Meshes, Entities }

/// <summary>One outliner row: a group or folder (with children), a mesh, or an entity (a mesh entity has its meshes as children).</summary>
public sealed class OutlinerNode
{
    public required OutlinerKind Kind { get; init; }
    public required int Id { get; init; }
    public required string Name { get; init; }
    /// <summary>Dim text after the name: an entity's class when it has a name, or a group's object count.</summary>
    public string Detail { get; init; } = "";
    public bool Hidden { get; init; }
    public bool Locked { get; init; }
    public int Depth { get; init; }
    public List<OutlinerNode> Children { get; } = new();

    /// <summary>A group or folder: a row that holds others and is not an object itself.</summary>
    public bool IsContainer => Kind is OutlinerKind.Group or OutlinerKind.Folder;

    public OutlinerNode CopyWith(IEnumerable<OutlinerNode> children)
    {
        var copy = new OutlinerNode { Kind = Kind, Id = Id, Name = Name, Detail = Detail, Hidden = Hidden, Locked = Locked, Depth = Depth };
        copy.Children.AddRange(children);
        return copy;
    }
}

/// <summary>
/// Builds the outliner's tree from the document: top-level groups and folders (nested ones first, then members in
/// order), then ungrouped meshes, then ungrouped entities. A mesh entity lists its meshes under it, wherever those meshes
/// are grouped. Pure, so it is tested without a window.
/// </summary>
public static class OutlinerTree
{
    /// <summary>
    /// The tree, optionally narrowed to one kind of object (<paramref name="show"/>) and filtered by text: a row stays when
    /// its name (or entity class) contains the filter, or a descendant matches. Empty folders show only when nothing narrows the tree.
    /// </summary>
    public static List<OutlinerNode> Build(Document doc, string filter = "", OutlinerShow show = OutlinerShow.All)
    {
        filter = filter.Trim();
        var grouped = doc.Groups.SelectMany(g => g.Members).ToHashSet();
        var owned = doc.Entities.SelectMany(e => e.Meshes).ToHashSet();
        var visited = new HashSet<Group>();
        var roots = new List<OutlinerNode>();
        foreach (var g in GroupOps.Children(doc, null)) roots.Add(GroupNode(doc, g, 0, visited, owned));
        foreach (var m in doc.Meshes)
            if (!grouped.Contains(m.Id) && !owned.Contains(m.Id)) roots.Add(ObjectNode(doc, m.Id, 0)!);
        foreach (var e in doc.Entities)
            if (!grouped.Contains(e.Id)) roots.Add(ObjectNode(doc, e.Id, 0)!);
        if (show != OutlinerShow.All)
            roots = Keep(roots, n => n.Kind == (show == OutlinerShow.Meshes ? OutlinerKind.Mesh : OutlinerKind.Entity));
        return filter.Length == 0 ? roots : Filter(roots, filter);
    }

    static OutlinerNode GroupNode(Document doc, Group g, int depth, HashSet<Group> visited, HashSet<int> owned)
    {
        visited.Add(g);
        int count = GroupOps.AllMembers(doc, g).Count;
        var node = new OutlinerNode
        {
            Kind = g.Folder ? OutlinerKind.Folder : OutlinerKind.Group, Id = g.Id, Name = g.Name, Hidden = g.Hidden, Locked = g.Locked,
            Depth = depth, Detail = count == 0 && g.Folder ? "empty" : Objects(count),
        };
        foreach (var child in GroupOps.Children(doc, g))
            if (!visited.Contains(child)) node.Children.Add(GroupNode(doc, child, depth + 1, visited, owned));
        foreach (var id in g.Members)
            if (!owned.Contains(id) && ObjectNode(doc, id, depth + 1) is { } n) node.Children.Add(n);
        return node;
    }

    static OutlinerNode? ObjectNode(Document doc, int id, int depth)
    {
        if (doc.Find(id) is { } m)
            return new OutlinerNode { Kind = OutlinerKind.Mesh, Id = id, Name = m.Name, Hidden = m.Hidden, Locked = m.Locked, Depth = depth };
        if (doc.FindEntity(id) is not { } e) return null;
        var node = new OutlinerNode
        {
            Kind = OutlinerKind.Entity, Id = id, Name = SceneOps.DisplayName(e), Hidden = e.Hidden, Locked = e.Locked, Depth = depth,
            Detail = string.IsNullOrWhiteSpace(e.Name) ? "" : e.ClassName,
        };
        foreach (int mesh in e.Meshes)
            if (doc.Find(mesh) is { } part)
                node.Children.Add(new OutlinerNode { Kind = OutlinerKind.Mesh, Id = mesh, Name = part.Name, Hidden = part.Hidden, Locked = part.Locked, Depth = depth + 1 });
        return node;
    }

    /// <summary>Rows <paramref name="keep"/> accepts (with their children), and the containers that still hold some.</summary>
    static List<OutlinerNode> Keep(List<OutlinerNode> nodes, Func<OutlinerNode, bool> keep)
    {
        var result = new List<OutlinerNode>();
        foreach (var n in nodes)
        {
            if (!n.IsContainer)
            {
                if (keep(n)) result.Add(n);
                continue;
            }
            var kept = Keep(n.Children, keep);
            if (kept.Count > 0) result.Add(n.CopyWith(kept));
        }
        return result;
    }

    static List<OutlinerNode> Filter(List<OutlinerNode> nodes, string filter)
    {
        var result = new List<OutlinerNode>();
        foreach (var n in nodes)
        {
            if (Matches(n, filter)) { result.Add(n); continue; } // a matching row keeps all its contents
            if (n.Children.Count == 0) continue;
            var kept = Filter(n.Children, filter);
            if (kept.Count > 0) result.Add(n.CopyWith(kept));
        }
        return result;
    }

    public static bool Matches(OutlinerNode n, string filter) =>
        n.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) || n.Detail.Contains(filter, StringComparison.OrdinalIgnoreCase);

    /// <summary>Rows in display order. Children of collapsed rows are skipped unless <paramref name="expandAll"/> (while searching).</summary>
    public static List<OutlinerNode> Flatten(IEnumerable<OutlinerNode> roots, IReadOnlySet<int> collapsed, bool expandAll = false)
    {
        var rows = new List<OutlinerNode>();
        void Walk(OutlinerNode n)
        {
            rows.Add(n);
            if (n.Children.Count > 0 && (expandAll || !collapsed.Contains(n.Id)))
                foreach (var c in n.Children) Walk(c);
        }
        foreach (var r in roots) Walk(r);
        return rows;
    }

    /// <summary>The objects a row stands for: itself, or every member of a group or folder (nested included).</summary>
    public static List<int> Objects(Document doc, OutlinerNode n) =>
        n.IsContainer ? doc.FindGroup(n.Id) is { } g ? GroupOps.AllMembers(doc, g) : [] : [n.Id];

    /// <summary>
    /// The rows that must be expanded for an object or group's row to show: its mesh entity, then its groups and folders,
    /// innermost first.
    /// </summary>
    public static List<int> Ancestors(Document doc, int id)
    {
        var result = new List<int>();
        if (MeshEntityOps.OwnerOf(doc, id) is { } owner)
        {
            result.Add(owner.Id);
            id = owner.Id;
        }
        var g = doc.FindGroup(id) is { } self ? GroupOps.ParentOf(doc, self) : GroupOps.GroupOf(doc, id);
        foreach (var a in g == null ? [] : GroupOps.Ancestry(doc, g)) result.Add(a.Id);
        return result;
    }

    /// <summary>"2 folders, 1 group, 12 meshes, 3 entities" (empty kinds left out, except meshes).</summary>
    public static string Summary(Document doc)
    {
        var parts = new List<string>();
        int folders = doc.Groups.Count(g => g.Folder);
        if (folders > 0) parts.Add(Plural(folders, "folder"));
        if (doc.Groups.Count > folders) parts.Add(Plural(doc.Groups.Count - folders, "group"));
        parts.Add(Plural(doc.Meshes.Count, "mesh", "meshes"));
        if (doc.Entities.Count > 0) parts.Add(Plural(doc.Entities.Count, "entity", "entities"));
        return string.Join(", ", parts);
    }

    static string Objects(int n) => Plural(n, "object");

    static string Plural(int n, string one, string? many = null) => n == 1 ? $"1 {one}" : $"{n} {many ?? one + "s"}";
}
