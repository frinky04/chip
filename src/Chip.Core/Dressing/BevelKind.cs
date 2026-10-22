using System.Numerics;
using Chip.Core.Geometry;

namespace Chip.Core.Dressing;

/// <summary>
/// <c>bevel</c> (shape): bevels a copy of each mesh along the edges between matching faces, so the blockout keeps its
/// sharp, easy-to-edit corners and the shipped level gets soft ones. An edge counts when both its faces match
/// <c>faces</c>, it is <c>convex</c> (an outside corner) or <c>concave</c> (inside), and the faces meet at
/// <c>minDeg</c> or more.
/// </summary>
public sealed class BevelKind : RuleKind
{
    public override string Name => "bevel";
    public override string DisplayName => "Bevel corners";
    public override Stage Stage => Stage.Shape;
    public override string Summary => "Bevel the edges between matching faces (outside corners by default) on a copy of the mesh.";

    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        new("faces", ParamType.Faces, null, "Faces whose shared edges are bevelled; both faces of an edge must match.", Required: true, Label: "Faces"),
        new("edges", ParamType.Choice, "convex", "convex: outside corners. concave: inside corners.", Choices: ["convex", "concave"],
            Label: "Corners", ChoiceLabels: ["Outside corners", "Inside corners"]),
        new("minDeg", ParamType.Number, 30f, "Only edges where the faces meet at this angle or more (degrees; 90 for a box corner).", 0, 180, Label: "Sharper than", Unit: ParamUnit.Degrees),
        new("width", ParamType.Number, 0.02f, "How far the bevel reaches into each face (metres).", 0.0005, 10, Label: "Width", Unit: ParamUnit.Length),
        new("segments", ParamType.Int, 1, "1 is a flat chamfer; more is rounded.", 1, 16, Label: "Segments"),
    ];

    /// <summary>
    /// The edges this rule bevels on <paramref name="m"/>. Faces carrying the rule's skip tag never match.
    /// </summary>
    internal static List<Edge> Edges(PolyMesh m, Rule rule, Vector3[] normals, out int matchedFaces)
    {
        var faces = rule.Get<FaceSelector>("faces");
        bool convex = rule.Text("edges") != "concave";
        float minCos = MathF.Cos(rule.Number("minDeg") * MathF.PI / 180f);
        string skip = rule.SkipTag;
        var matched = new bool[m.Faces.Count];
        matchedFaces = 0;
        for (int f = 0; f < m.Faces.Count; f++)
            if (matched[f] = !m.Faces[f].Tags.Contains(skip) && faces.Matches(m, f, normals[f])) matchedFaces++;

        var result = new List<Edge>();
        if (matchedFaces < 2) return result;
        foreach (var (edge, fs) in Topology.EdgeFaces(m))
        {
            if (fs.Count != 2 || !matched[fs[0]] || !matched[fs[1]]) continue;
            var n0 = normals[fs[0]];
            var n1 = normals[fs[1]];
            if (Vector3.Dot(n0, n1) > minCos + 1e-5f) continue; // flatter than minDeg
            float side = Vector3.Dot(m.FaceCenter(fs[1]) - m.FaceCenter(fs[0]), n0);
            if (convex ? side < -1e-5f : side > 1e-5f) result.Add(edge);
        }
        return result;
    }

    /// <summary>
    /// A bevelled copy of <paramref name="source"/>, or null when no edge qualifies. Problems (too wide for the shortest edge,
    /// a failed bevel) come back as a diagnostic, and the mesh stays as it is.
    /// </summary>
    internal static (PolyMesh? Result, int Matched, int Edges, Diagnostic? Problem) Apply(PolyMesh source, Rule rule)
    {
        var normals = Enumerable.Range(0, source.Faces.Count).Select(source.FaceNormal).ToArray();
        var edges = Edges(source, rule, normals, out int matched);
        if (edges.Count == 0) return (null, matched, 0, null);
        float width = rule.Number("width");
        float shortest = edges.Min(e => Vector3.Distance(source.Positions[e.A], source.Positions[e.B]));
        if (width * 2 > shortest + 1e-5f)
            return (null, matched, edges.Count, new Diagnostic(Severity.Warning, "W310",
                $"width {width} is more than half the shortest bevelled edge ({shortest:0.###}); \"{source.Name}\" left unbevelled",
                $"lower width to {shortest / 2:0.###} or less, or tag the faces \"{rule.SkipTag}\"", MeshId: source.Id, Rule: rule.Id, Path: $"rules.{rule.Id}.width"));

        var copy = source.Clone();
        MeshOps.BevelEdges(copy, edges, width, rule.Int("segments"));
        if (copy.Positions.Any(p => !float.IsFinite(p.X + p.Y + p.Z)))
            return (null, matched, edges.Count, new Diagnostic(Severity.Warning, "W311", $"the bevel broke \"{source.Name}\"; left unbevelled",
                $"lower width, or tag the faces \"{rule.SkipTag}\"", MeshId: source.Id, Rule: rule.Id));
        return (copy, matched, edges.Count, null);
    }
}
