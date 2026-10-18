using Chip.Core.Scene;

namespace Chip.Editor.Features.Scene;

public enum OutlinerKind { Group, Mesh, Entity }

/// <summary>One outliner row: a group (with children), a mesh or an entity.</summary>
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
}

/// <summary>
/// Builds the outliner's tree from the document: top-level groups (nested groups first, then members in order), then
/// ungrouped meshes, then ungrouped entities. Pure, so it is tested without a window.
/// </summary>
public static class OutlinerTree
{
    /// <summary>The tree, optionally filtered: a row stays when its name (or entity class) contains the filter, or a descendant matches.</summary>
    public static List<OutlinerNode> Build(Document doc, string filter = "")
    {
        filter = filter.Trim();
        var grouped = doc.Groups.SelectMany(g => g.Members).ToHashSet();
        var visited = new HashSet<Group>();
        var roots = new List<OutlinerNode>();
        foreach (var g in GroupOps.Children(doc, null)) roots.Add(GroupNode(doc, g, 0, visited));
        foreach (var m in doc.Meshes)
            if (!grouped.Contains(m.Id)) roots.Add(ObjectNode(doc, m.Id, 0)!);
        foreach (var e in doc.Entities)
            if (!grouped.Contains(e.Id)) roots.Add(ObjectNode(doc, e.Id, 0)!);
        return filter.Length == 0 ? roots : Filter(roots, filter);
    }

    static OutlinerNode GroupNode(Document doc, Group g, int depth, HashSet<Group> visited)
    {
        visited.Add(g);
        var node = new OutlinerNode
        {
            Kind = OutlinerKind.Group, Id = g.Id, Name = g.Name, Hidden = g.Hidden, Locked = g.Locked, Depth = depth,
            Detail = Objects(GroupOps.AllMembers(doc, g).Count),
        };
        foreach (var child in GroupOps.Children(doc, g))
            if (!visited.Contains(child)) node.Children.Add(GroupNode(doc, child, depth + 1, visited));
        foreach (var id in g.Members)
            if (ObjectNode(doc, id, depth + 1) is { } n) node.Children.Add(n);
        return node;
    }

    static OutlinerNode? ObjectNode(Document doc, int id, int depth)
    {
        if (doc.Find(id) is { } m)
            return new OutlinerNode { Kind = OutlinerKind.Mesh, Id = id, Name = m.Name, Hidden = m.Hidden, Locked = m.Locked, Depth = depth };
        if (doc.FindEntity(id) is { } e)
            return new OutlinerNode
            {
                Kind = OutlinerKind.Entity, Id = id, Name = SceneOps.DisplayName(e), Hidden = e.Hidden, Locked = e.Locked, Depth = depth,
                Detail = string.IsNullOrWhiteSpace(e.Name) ? "" : e.ClassName,
            };
        return null;
    }

    static List<OutlinerNode> Filter(List<OutlinerNode> nodes, string filter)
    {
        var result = new List<OutlinerNode>();
        foreach (var n in nodes)
        {
            if (Matches(n, filter)) { result.Add(n); continue; } // a matching group keeps all its contents
            if (n.Kind != OutlinerKind.Group) continue;
            var kept = Filter(n.Children, filter);
            if (kept.Count == 0) continue;
            var copy = new OutlinerNode { Kind = n.Kind, Id = n.Id, Name = n.Name, Detail = n.Detail, Hidden = n.Hidden, Locked = n.Locked, Depth = n.Depth };
            copy.Children.AddRange(kept);
            result.Add(copy);
        }
        return result;
    }

    public static bool Matches(OutlinerNode n, string filter) =>
        n.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) || n.Detail.Contains(filter, StringComparison.OrdinalIgnoreCase);

    /// <summary>Rows in display order. Children of collapsed groups are skipped unless <paramref name="expandAll"/> (while searching).</summary>
    public static List<OutlinerNode> Flatten(IEnumerable<OutlinerNode> roots, IReadOnlySet<int> collapsed, bool expandAll = false)
    {
        var rows = new List<OutlinerNode>();
        void Walk(OutlinerNode n)
        {
            rows.Add(n);
            if (n.Kind == OutlinerKind.Group && (expandAll || !collapsed.Contains(n.Id)))
                foreach (var c in n.Children) Walk(c);
        }
        foreach (var r in roots) Walk(r);
        return rows;
    }

    /// <summary>The objects a row stands for: itself, or every member of a group (nested included).</summary>
    public static List<int> Objects(Document doc, OutlinerNode n) =>
        n.Kind == OutlinerKind.Group ? doc.FindGroup(n.Id) is { } g ? GroupOps.AllMembers(doc, g) : [] : [n.Id];

    /// <summary>"2 groups, 12 meshes, 3 entities" (empty kinds left out).</summary>
    public static string Summary(Document doc)
    {
        var parts = new List<string>();
        if (doc.Groups.Count > 0) parts.Add(Plural(doc.Groups.Count, "group"));
        parts.Add(Plural(doc.Meshes.Count, "mesh", "meshes"));
        if (doc.Entities.Count > 0) parts.Add(Plural(doc.Entities.Count, "entity", "entities"));
        return string.Join(", ", parts);
    }

    static string Objects(int n) => Plural(n, "object");

    static string Plural(int n, string one, string? many = null) => n == 1 ? $"1 {one}" : $"{n} {many ?? one + "s"}";
}
