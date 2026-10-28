using Chip.Core.Geometry;

namespace Chip.Core.Scene;

/// <summary>How many faces and objects (meshes and entities) in a level carry a tag.</summary>
public readonly record struct TagUse(string Tag, int Faces, int Objects);

/// <summary>
/// Tag edits and queries. The selection's mode decides the target: faces in face mode, whole objects in object mode
/// (a mesh's <see cref="PolyMesh.Tags"/>, an entity's <see cref="Entity.Tags"/>, which connections can target). Vertex
/// and edge selections have no tag target. Edits change nothing outside the selection and return how much changed, so
/// callers can skip empty undo steps.
/// </summary>
public static class TagOps
{
    /// <summary>Every tag in the level with how many faces and objects carry it, sorted by name.</summary>
    public static List<TagUse> Usage(Document doc)
    {
        var faces = new Dictionary<string, int>(StringComparer.Ordinal);
        var objects = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var m in doc.Meshes)
        {
            foreach (var t in m.Tags) objects[t] = objects.GetValueOrDefault(t) + 1;
            foreach (var f in m.Faces)
                foreach (var t in f.Tags) faces[t] = faces.GetValueOrDefault(t) + 1;
        }
        foreach (var e in doc.Entities)
            foreach (var t in e.Tags) objects[t] = objects.GetValueOrDefault(t) + 1;
        return faces.Keys.Union(objects.Keys).Order(StringComparer.Ordinal)
            .Select(t => new TagUse(t, faces.GetValueOrDefault(t), objects.GetValueOrDefault(t))).ToList();
    }

    /// <summary>Whether the selection's mode has something to tag (faces or objects).</summary>
    public static bool CanTag(SelectMode mode) => mode is SelectMode.Face or SelectMode.Object;

    /// <summary>How many things the selection would tag: faces in face mode, meshes and entities in object mode.</summary>
    public static int TargetCount(Document doc, Selection sel) => Targets(doc, sel).Sum(t => t.Faces?.Count ?? 1) + Entities(doc, sel).Count();

    /// <summary>How many of the selection's tag targets carry <paramref name="tag"/>, out of how many targets.</summary>
    public static (int With, int Total) Coverage(Document doc, Selection sel, string tag)
    {
        int with = 0, total = 0;
        foreach (var (mesh, faces) in Targets(doc, sel))
        {
            if (faces == null) { total++; if (mesh.Tags.Contains(tag)) with++; continue; }
            foreach (var f in faces) { total++; if (mesh.Faces[f].Tags.Contains(tag)) with++; }
        }
        foreach (var e in Entities(doc, sel)) { total++; if (e.Tags.Contains(tag)) with++; }
        return (with, total);
    }

    /// <summary>Tags carried by any of the selection's targets, with how many targets carry each, sorted by name.</summary>
    public static List<(string Tag, int Count)> OnSelection(Document doc, Selection sel)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (mesh, faces) in Targets(doc, sel))
        {
            if (faces == null) { foreach (var t in mesh.Tags) counts[t] = counts.GetValueOrDefault(t) + 1; continue; }
            foreach (var f in faces)
                foreach (var t in mesh.Faces[f].Tags) counts[t] = counts.GetValueOrDefault(t) + 1;
        }
        foreach (var e in Entities(doc, sel))
            foreach (var t in e.Tags) counts[t] = counts.GetValueOrDefault(t) + 1;
        return counts.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => (kv.Key, kv.Value)).ToList();
    }

    /// <summary>
    /// Adds (<paramref name="on"/>) or removes <paramref name="tag"/> on the selection's targets. Returns how many targets
    /// changed. Throws <see cref="ArgumentException"/> for an invalid name (see <see cref="TagSet.IsValid"/>).
    /// </summary>
    public static int Set(Document doc, Selection sel, string tag, bool on)
    {
        if (on && !TagSet.IsValid(tag, out var why)) throw new ArgumentException($"Invalid tag \"{tag}\": {why}", nameof(tag));
        int changed = 0;
        foreach (var (mesh, faces) in Targets(doc, sel))
        {
            if (faces == null)
            {
                var next = on ? mesh.Tags.Add(tag) : mesh.Tags.Remove(tag);
                if (next != mesh.Tags) { mesh.Tags = next; changed++; }
                continue;
            }
            foreach (var f in faces)
            {
                var face = mesh.Faces[f];
                var next = on ? face.Tags.Add(tag) : face.Tags.Remove(tag);
                if (next != face.Tags) { face.Tags = next; changed++; }
            }
        }
        foreach (var e in Entities(doc, sel))
        {
            var next = on ? e.Tags.Add(tag) : e.Tags.Remove(tag);
            if (next != e.Tags) { e.Tags = next; changed++; }
        }
        return changed;
    }

    /// <summary>
    /// Selects every visible, unlocked face (face mode) or mesh and entity (object mode) carrying <paramref name="tag"/>,
    /// switching to that mode. Returns the count.
    /// </summary>
    public static int Select(Document doc, Selection sel, string tag, SelectMode mode)
    {
        var items = new List<ElementRef>();
        foreach (var m in doc.Meshes.Where(m => !m.Hidden && !m.Locked))
        {
            if (mode == SelectMode.Object) { if (m.Tags.Contains(tag)) items.Add(ElementRef.Object(m.Id)); continue; }
            for (int f = 0; f < m.Faces.Count; f++)
                if (m.Faces[f].Tags.Contains(tag)) items.Add(ElementRef.Face(m.Id, f));
        }
        if (mode == SelectMode.Object)
            items.AddRange(doc.Entities.Where(e => !e.Hidden && !e.Locked && e.Tags.Contains(tag)).Select(e => ElementRef.Object(e.Id)));
        SelectionOps.Replace(sel, mode == SelectMode.Object ? SelectMode.Object : SelectMode.Face, items, null);
        return items.Count;
    }

    /// <summary>The selection's tag targets: (mesh, faces) in face mode, (mesh, null) for the mesh itself in object mode.</summary>
    static IEnumerable<(PolyMesh Mesh, List<int>? Faces)> Targets(Document doc, Selection sel)
    {
        if (!CanTag(sel.Mode)) yield break;
        foreach (var g in sel.Items.GroupBy(i => i.Mesh))
        {
            if (doc.Find(g.Key) is not { } mesh) continue;
            if (sel.Mode == SelectMode.Object) yield return (mesh, null);
            else yield return (mesh, g.Select(i => i.A).Where(f => f >= 0 && f < mesh.Faces.Count).Distinct().Order().ToList());
        }
    }

    /// <summary>The selected entities, which object mode tags with the meshes.</summary>
    static IEnumerable<Entity> Entities(Document doc, Selection sel) => sel.Mode != SelectMode.Object ? []
        : sel.Items.Select(i => i.Mesh).Distinct().Select(doc.FindEntity).OfType<Entity>();
}
