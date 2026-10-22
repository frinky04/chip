using Chip.Core.Geometry;
using Chip.Core.Scene;

namespace Chip.Core.Dressing;

/// <summary>
/// Level edits that go with rule edits: renaming or deleting a rule carries its per-mesh overrides, switches and
/// <c>skip:</c> face tags along (or drops them), and "Dress with" tags the selection so a rule takes it. Callers run these
/// inside one undo step with the rules file change, scoped to <see cref="Mentioning"/> or the selection's meshes.
/// </summary>
public static class DressingOps
{
    /// <summary>Meshes whose overrides, switches or face tags mention <paramref name="rule"/>.</summary>
    public static List<int> Mentioning(Document doc, string rule)
    {
        string skip = TagSet.SkipPrefix + rule;
        return doc.Meshes.Where(m => m.Dressing.Mentions(rule) || m.Faces.Any(f => f.Tags.Contains(skip))).Select(m => m.Id).ToList();
    }

    /// <summary>Moves every mention of rule <paramref name="from"/> to <paramref name="to"/>, or drops them when it is null.</summary>
    public static int RenameRule(Document doc, string from, string? to)
    {
        string skip = TagSet.SkipPrefix + from;
        int changed = 0;
        foreach (var m in doc.Meshes)
        {
            bool touched = false;
            var dressing = m.Dressing.Renamed(from, to);
            if (!ReferenceEquals(dressing, m.Dressing)) { m.Dressing = dressing; touched = true; }
            foreach (var f in m.Faces)
                if (f.Tags.Contains(skip))
                {
                    f.Tags = to == null ? f.Tags.Remove(skip) : f.Tags.Remove(skip).Add(TagSet.SkipPrefix + to);
                    touched = true;
                }
            if (touched) changed++;
        }
        return changed;
    }

    /// <summary>
    /// What "Dress with" tags for a rule selecting by <paramref name="tag"/>: meshes for a mesh tag (selected meshes, or the
    /// meshes of selected faces); for a face tag, the selected faces, or in object mode the faces of each selected mesh that
    /// the rule would take if they had the tag (<see cref="ParamJson.Taggable"/>).
    /// </summary>
    public static IEnumerable<(PolyMesh Mesh, List<int>? Faces)> DressTargets(Document doc, Selection sel, bool meshTag, FaceSelector faces)
    {
        foreach (var g in sel.Items.GroupBy(i => i.Mesh))
        {
            if (doc.Find(g.Key) is not { } mesh) continue;
            if (meshTag) yield return (mesh, null);
            else if (sel.Mode == SelectMode.Face)
                yield return (mesh, g.Select(i => i.A).Where(f => f >= 0 && f < mesh.Faces.Count).Distinct().Order().ToList());
            else if (sel.Mode == SelectMode.Object) yield return (mesh, ParamJson.Taggable(mesh, faces).ToList());
        }
    }

    /// <summary>How many of the targets carry the tag already, out of how many.</summary>
    public static (int With, int Total) DressCoverage(Document doc, Selection sel, string tag, bool meshTag, FaceSelector faces)
    {
        int with = 0, total = 0;
        foreach (var (mesh, list) in DressTargets(doc, sel, meshTag, faces))
        {
            if (list == null) { total++; if (mesh.Tags.Contains(tag)) with++; continue; }
            foreach (var f in list) { total++; if (mesh.Faces[f].Tags.Contains(tag)) with++; }
        }
        return (with, total);
    }

    /// <summary>Adds (or removes) the tag on the targets; returns how many changed.</summary>
    public static int Dress(Document doc, Selection sel, string tag, bool meshTag, FaceSelector faces, bool on)
    {
        int changed = 0;
        foreach (var (mesh, list) in DressTargets(doc, sel, meshTag, faces).ToList())
        {
            if (list == null)
            {
                var next = on ? mesh.Tags.Add(tag) : mesh.Tags.Remove(tag);
                if (next != mesh.Tags) { mesh.Tags = next; changed++; }
                continue;
            }
            foreach (var f in list)
            {
                var face = mesh.Faces[f];
                var next = on ? face.Tags.Add(tag) : face.Tags.Remove(tag);
                if (next != face.Tags) { face.Tags = next; changed++; }
            }
        }
        return changed;
    }
}
