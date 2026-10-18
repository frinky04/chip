using Chip.Core.Geometry;
using Chip.Core.Scene;
using Chip.Editor.Features.View;
using Chip.Editor.Framework;
using Ops = Chip.Core.Geometry.MeshOps;

namespace Chip.Editor.Features.Normals;

/// <summary>
/// Stream L, normals and smoothing (SPEC §6): soft / hard / dominant normals, auto-smooth by angle, clear smoothing and a
/// "Show normals" overlay. Smoothing lives on edges (<see cref="PolyMesh.SoftEdges"/>) and faces (<see cref="Face.Dominant"/>);
/// the viewport and the glTF export shade from <see cref="Topology.CornerNormals"/>. Scope follows the selection mode:
/// edges act on themselves, vertices on the edges between selected vertices, faces on every edge of the faces (inside and
/// outline), objects on the whole mesh. Each command is one undo step; one that changes nothing leaves no step.
/// </summary>
public sealed class NormalsFeature : IEditorFeature
{
    EditorContext _ctx = null!;

    Selection Sel => _ctx.Sel;
    Document Doc => _ctx.Doc;

    /// <summary>Auto-smooth threshold in degrees: edges whose faces meet at a smaller angle become soft.</summary>
    public float SmoothAngle { get; set; } = Ops.DefaultSmoothAngle;

    public void Register(EditorContext ctx)
    {
        _ctx = ctx;
        var c = ctx.Commands;
        c.Add(new Command
        {
            Id = "mesh.softNormals", Label = "Soft normals", Menu = "Mesh", Group = "5-normals", Order = 1,
            Hotkeys = [Hotkey.Parse("J")], CanExecute = HasTargets, Execute = () => SetSmoothing(soft: true),
            Description = "Shade smoothly across the selected edges (faces: all their edges; objects: the whole mesh)",
        });
        c.Add(new Command
        {
            Id = "mesh.hardNormals", Label = "Hard normals", Menu = "Mesh", Group = "5-normals", Order = 2,
            Hotkeys = [Hotkey.Parse("Ctrl+J")], CanExecute = HasTargets, Execute = () => SetSmoothing(soft: false),
            Description = "Crease the selected edges so each face shades flat (H is hide in Chip)",
        });
        c.Add(new Command
        {
            Id = "mesh.dominantNormals", Label = "Dominant normals", Menu = "Mesh", Group = "5-normals", Order = 3,
            Hotkeys = [Hotkey.Parse("K")], CanExecute = () => Sel.Mode == SelectMode.Face && !Sel.IsEmpty, Execute = Dominant,
            Description = "The selected faces stay flat; neighbours meeting them at under 60° (bevels, chamfers) bend to match",
        });
        c.Add(new Command
        {
            Id = "mesh.autoSmooth", Label = "Auto-smooth", Menu = "Mesh", Group = "5-normals", Order = 4,
            CanExecute = HasTargets, Execute = AutoSmooth, DisplayLabel = () => $"Auto-smooth ({SmoothAngle:0}°)",
            Description = "Soft where faces meet at less than the smoothing angle, hard elsewhere (angle in the Smoothing section)",
        });
        c.Add(new Command
        {
            Id = "mesh.clearSmoothing", Label = "Clear smoothing", Menu = "Mesh", Group = "5-normals", Order = 5,
            CanExecute = HasTargets, Execute = ClearSmoothing,
            Description = "Make every edge of the selected meshes hard and drop dominant faces",
        });
        c.Add(new Command
        {
            Id = "view.showNormals", Label = "Show normals", Menu = "View", Group = "4-shading", Order = 12,
            Description = "Draw short shading-normal lines at the corners of the selection",
            Execute = () => ViewSettings.Of(ctx).ShowNormals = !ViewSettings.Of(ctx).ShowNormals,
            IsChecked = () => ViewSettings.Of(ctx).ShowNormals,
        });
        ctx.AddSection(new SmoothingSection(this));
    }

    bool HasTargets() => !Sel.IsEmpty;

    /// <summary>Edges the current selection addresses, per mesh (see the class summary for the per-mode rule).</summary>
    public Dictionary<PolyMesh, HashSet<Edge>> ScopeEdges()
    {
        var result = new Dictionary<PolyMesh, HashSet<Edge>>();
        foreach (var group in Sel.Items.GroupBy(i => i.Mesh))
        {
            if (Doc.Find(group.Key) is not { } m) continue;
            HashSet<Edge> edges;
            switch (Sel.Mode)
            {
                case SelectMode.Edge:
                    edges = group.Select(i => i.AsEdge).ToHashSet();
                    break;
                case SelectMode.Vertex:
                    var verts = group.Select(i => i.A).ToHashSet();
                    edges = m.Edges().Where(e => verts.Contains(e.A) && verts.Contains(e.B)).ToHashSet();
                    break;
                case SelectMode.Face:
                    edges = Ops.EdgesOfFaces(m, group.Select(i => i.A));
                    break;
                default:
                    edges = m.Edges().ToHashSet();
                    break;
            }
            result[m] = edges;
        }
        return result;
    }

    /// <summary>Runs one edit as an undo step; when <paramref name="edit"/> returns a message nothing changed and the step is dropped.</summary>
    void Edit(string label, Func<string?> edit)
    {
        _ctx.Session.Begin(label);
        string? nothing;
        try { nothing = edit(); }
        catch { _ctx.Session.Cancel(); throw; }
        if (nothing != null)
        {
            _ctx.Session.Cancel();
            _ctx.Status(nothing);
            return;
        }
        _ctx.Session.End();
    }

    static string Edges(int n) => n == 1 ? "1 edge" : $"{n} edges";
    static string Faces(int n) => n == 1 ? "1 face" : $"{n} faces";

    void SetSmoothing(bool soft) => Edit(soft ? "Soft Normals" : "Hard Normals", () =>
    {
        int edges = 0, faces = 0;
        bool clearsDominant = Sel.Mode is SelectMode.Face or SelectMode.Object;
        var faceSets = clearsDominant ? Sel.FaceSets(Doc) : new();
        foreach (var (mesh, scope) in ScopeEdges())
        {
            edges += Ops.SetEdgeSmoothing(mesh, scope, soft);
            if (faceSets.TryGetValue(mesh, out var fs)) faces += Ops.ClearDominantFaces(mesh, fs);
        }
        if (edges == 0 && faces == 0)
            return soft ? "Already soft: those edges shade smoothly (open edges can't be soft)" : "Already hard: those edges shade flat";
        _ctx.Status(edges > 0 ? $"{(soft ? "Softened" : "Hardened")} {Edges(edges)}" : $"Cleared dominant normals on {Faces(faces)}");
        return null;
    });

    void Dominant() => Edit("Dominant Normals", () =>
    {
        int changed = 0, total = 0;
        foreach (var (mesh, faces) in Sel.FaceSets(Doc))
        {
            int softBefore = mesh.SoftEdges.Count;
            changed += Ops.SetDominantFaces(mesh, faces);
            changed += mesh.SoftEdges.Count - softBefore;
            total += faces.Count;
        }
        if (changed == 0) return "Already dominant: neighbouring faces take these faces' normals";
        _ctx.Status($"Made {Faces(total)} dominant");
        return null;
    });

    void AutoSmooth() => Edit("Auto-smooth", () =>
    {
        int soft = 0, total = 0;
        bool changed = false;
        foreach (var (mesh, scope) in ScopeEdges())
        {
            var before = new HashSet<Edge>(mesh.SoftEdges);
            soft += Ops.AutoSmooth(mesh, SmoothAngle, scope);
            total += scope.Count;
            changed |= !before.SetEquals(mesh.SoftEdges);
        }
        string summary = $"{Edges(soft)} of {total} soft below {SmoothAngle:0}°";
        if (!changed) return $"Smoothing already matches: {summary}";
        _ctx.Status($"Auto-smoothed: {summary}");
        return null;
    });

    void ClearSmoothing() => Edit("Clear Smoothing", () =>
    {
        int meshes = 0;
        foreach (var id in Sel.MeshIds())
        {
            if (Doc.Find(id) is not { } m || (m.SoftEdges.Count == 0 && !m.Faces.Any(f => f.Dominant))) continue;
            Ops.ClearSmoothing(m);
            meshes++;
        }
        if (meshes == 0) return "Nothing to clear: the selected meshes already shade flat";
        _ctx.Status(meshes == 1 ? "Cleared smoothing on 1 mesh" : $"Cleared smoothing on {meshes} meshes");
        return null;
    });
}
