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

    /// <summary>
    /// How many of the targets <paramref name="rule"/> dresses already (they carry the tag, and neither the face's
    /// <c>skip:</c> tag nor the mesh's switch turns the rule off), out of how many.
    /// </summary>
    public static (int With, int Total) DressCoverage(Document doc, Selection sel, string rule, string tag, bool meshTag, FaceSelector faces)
    {
        int with = 0, total = 0;
        string skip = TagSet.SkipPrefix + rule;
        foreach (var (mesh, list) in DressTargets(doc, sel, meshTag, faces))
        {
            bool meshOff = mesh.Dressing.Skips(rule);
            if (list == null) { total++; if (mesh.Tags.Contains(tag) && !meshOff) with++; continue; }
            foreach (var f in list)
            {
                total++;
                var t = mesh.Faces[f].Tags;
                if (t.Contains(tag) && !t.Contains(skip) && !meshOff) with++;
            }
        }
        return (with, total);
    }

    /// <summary>
    /// Dresses the targets with <paramref name="rule"/> (adds its tag and clears the rule's <c>skip:</c> tags and mesh
    /// switch), or with <paramref name="on"/> false takes them out of just that rule: faces get <c>skip:rule</c>, meshes
    /// switch the rule off. The tag stays, so other rules selecting by it keep the selection. Returns how many changed.
    /// </summary>
    public static int Dress(Document doc, Selection sel, string rule, string tag, bool meshTag, FaceSelector faces, bool on)
    {
        int changed = 0;
        string skip = TagSet.SkipPrefix + rule;
        foreach (var (mesh, list) in DressTargets(doc, sel, meshTag, faces).ToList())
        {
            bool touched = false;
            if (on && mesh.Dressing.Skip.Contains(rule)) { mesh.Dressing = mesh.Dressing.WithSkip(rule, false); touched = true; }
            if (list == null)
            {
                if (on && !mesh.Tags.Contains(tag)) { mesh.Tags = mesh.Tags.Add(tag); touched = true; }
                if (!on && mesh.Tags.Contains(tag) && !mesh.Dressing.Skips(rule)) { mesh.Dressing = mesh.Dressing.WithSkip(rule, true); touched = true; }
                if (touched) changed++;
                continue;
            }
            foreach (var f in list)
            {
                var face = mesh.Faces[f];
                var next = on ? face.Tags.Add(tag).Remove(skip) : face.Tags.Contains(tag) ? face.Tags.Add(skip) : face.Tags;
                if (next != face.Tags) { face.Tags = next; changed++; }
            }
            if (touched && list.Count == 0) changed++;
        }
        return changed;
    }
}
