using System.Numerics;
using Chip.Core.Geometry;

namespace Chip.Core.Dressing;

/// <summary>
/// <c>repeat</c> (dress): places a prop every <c>every</c> metres along the same edges a sweep would follow (wall feet,
/// corners, outlines): posts, wall lights, brackets. Each run fits a whole number of props, evenly spaced and centred.
/// The prop's +X runs along the edge, +Y along the surface it sits on (up from a floor), +Z out from the face.
/// </summary>
public sealed class RepeatKind : RuleKind
{
    public override string Name => "repeat";
    public override string DisplayName => "Props along edges";
    public override Stage Stage => Stage.Dress;
    public override string Summary => "Place a prop at even spacing along edges of matching faces: posts, lights, brackets.";

    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        new("faces", ParamType.Faces, null, "Faces whose edges get the props.", Required: true, Label: "Faces"),
        new("edges", ParamType.Choice, "contact", "Which edges, as for sweep: contact, boundary, convex, concave or border.", Choices: SweepKind.EdgeKinds,
            Label: "Edges", ChoiceLabels: SweepKind.EdgeLabels),
        new("onto", ParamType.Faces, FaceSelector.Up, "For contact: the faces they sit on. Default: up-facing faces (floors).", Label: "Touching"),
        new("minDeg", ParamType.Number, 30f, "For convex / concave: only corners at this angle or more (degrees).", 0, 180, Label: "Sharper than", Unit: ParamUnit.Degrees),
        new("prop", ParamType.Prop, null, "What to place: \"props/lamp.glb\", { \"box\": [w, h, d] } or { \"cylinder\": [diameter, height] }.", Required: true, Label: "Prop"),
        new("every", ParamType.Number, 1f, "Spacing along each run; rounded so a whole number fits (metres).", 0.05, 1000, Label: "Every", Unit: ParamUnit.Length),
        new("offset", ParamType.Pair, Vector2.Zero, "Moves each prop [out, along] from the edge (metres): [0, 1.8] puts wall lights at head height.", Label: "Offset", Unit: ParamUnit.Length),
        new("material", ParamType.Text, null, "Material for every prop face. Default: the prop's own.", Label: "Material"),
    ];

    internal static PolyMesh Build(Rule rule, List<SweepKind.Segment> segments, PropLibrary props, string name, out string? problem)
    {
        var spec = rule.Get<PropSpec>("prop");
        var (prop, error) = props.Get(spec);
        problem = error;
        var m = new PolyMesh { Name = name };
        if (prop == null) return m;
        var offset = rule.Get<Vector2>("offset");
        float every = rule.Number("every");
        string? material = rule.Text("material");
        foreach (var s in segments)
        {
            int n = Math.Max(1, (int)MathF.Round(s.Length / every));
            for (int i = 0; i < n; i++)
            {
                var at = Vector3.Lerp(s.A, s.B, (i + 0.5f) / n) + s.Out * offset.X + s.Up * offset.Y;
                PropLibrary.Place(m, prop, PropLibrary.Frame(at, s.Dir, s.Up, s.Out), material);
            }
        }
        MeshOps.AlignToGrid(m, Enumerable.Range(0, m.Faces.Count).Where(f => m.Faces[f].UVs == null), segments.Count > 0 ? segments[0].Map : null);
        return m;
    }
}

/// <summary>
/// <c>scatter</c> (dress): sprinkles props over matching faces: rubble, crates, grass tufts. Placement is seeded by each
/// face's own position (and the rule's id and <c>seed</c>), so editing one face never reshuffles another, and the same
/// level always scatters the same way.
/// </summary>
public sealed class ScatterKind : RuleKind
{
    public override string Name => "scatter";
    public override string DisplayName => "Scatter props";
    public override Stage Stage => Stage.Dress;
    public override string Summary => "Sprinkle props over matching faces, seeded by each face so edits elsewhere never reshuffle them.";

    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        new("faces", ParamType.Faces, null, "Faces to scatter over.", Required: true, Label: "Faces"),
        new("props", ParamType.Props, null, "Props to choose from (a list, or one prop).", Required: true, Label: "Props"),
        new("weights", ParamType.Numbers, null, "How often each prop is chosen, in the same order (default: equally).", Label: "Weights"),
        new("perM2", ParamType.Number, 0.5f, "Props per square metre (on average).", 0, 100, Label: "Density", Unit: ParamUnit.PerM2),
        new("minGap", ParamType.Number, 0.25f, "No two props on a face closer than this (metres).", 0, 100, Label: "Min gap", Unit: ParamUnit.Length),
        new("scale", ParamType.Pair, Vector2.One, "Random uniform scale range [min, max].", Label: "Scale", Unit: ParamUnit.Scale),
        new("yawDeg", ParamType.Pair, new Vector2(0, 360), "Random turn range [min, max] (degrees).", Label: "Turn", Unit: ParamUnit.Degrees),
        new("align", ParamType.Choice, "up", "up: props stand upright (floors). normal: props follow the face (walls, slopes).", Choices: ["up", "normal"],
            Label: "Stand", ChoiceLabels: ["Upright", "On the surface"]),
        new("offset", ParamType.Number, 0f, "Raise each prop off the face (metres; negative sinks it).", -100, 100, Label: "Raise", Unit: ParamUnit.Length),
        new("seed", ParamType.Int, 0, "Change for a different arrangement.", int.MinValue, int.MaxValue, Label: "Seed"),
        new("material", ParamType.Text, null, "Material for every prop face. Default: the props' own.", Label: "Material"),
    ];

    /// <summary>Most props one face may get, so a typo in perM2 can't hang the editor.</summary>
    const int MaxPerFace = 2000;

    internal static PolyMesh Build(PolyMesh source, Rule rule, PropLibrary props, string name, out int faces, out string? problem)
    {
        var m = new PolyMesh { Name = name };
        problem = null;
        faces = 0;
        var specs = rule.Get<PropSpec[]>("props");
        var loaded = new List<PolyMesh>();
        var weights = new List<float>();
        var given = rule.Find<float[]>("weights");
        for (int i = 0; i < specs.Length; i++)
        {
            var (mesh, error) = props.Get(specs[i]);
            if (mesh == null) { problem ??= error; continue; }
            loaded.Add(mesh);
            weights.Add(given != null && i < given.Length ? given[i] : 1);
        }
        if (loaded.Count == 0 || weights.Sum() <= 0) return m;

        var select = rule.Get<FaceSelector>("faces");
        string skip = rule.SkipTag;
        float perM2 = rule.Number("perM2"), minGap = rule.Number("minGap"), offset = rule.Number("offset");
        var scale = rule.Get<Vector2>("scale");
        var yaw = rule.Get<Vector2>("yawDeg");
        bool alignNormal = rule.Text("align") == "normal";
        string? material = rule.Text("material");
        int seed = rule.Int("seed");
        var tri = new List<int>();
        float total = weights.Sum();

        for (int f = 0; f < source.Faces.Count; f++)
        {
            var face = source.Faces[f];
            var normal = source.FaceNormal(f);
            if (normal == Vector3.Zero || face.Tags.Contains(skip) || !select.Matches(source, f, normal)) continue;
            faces++;
            tri.Clear();
            Triangulator.Triangulate(source, f, tri);
            var corners = face.Verts.Select(v => source.Positions[v]).ToArray();
            var areas = new List<float>();
            for (int t = 0; t + 2 < tri.Count; t += 3)
                areas.Add(Vector3.Cross(corners[tri[t + 1]] - corners[tri[t]], corners[tri[t + 2]] - corners[tri[t]]).Length() * 0.5f);
            float area = areas.Sum();
            if (area < 1e-6f) continue;

            var rng = new Random(FaceSeed(corners, rule.Id, seed));
            double expected = Math.Min(area * perM2, MaxPerFace);
            int count = (int)expected + (rng.NextDouble() < expected - Math.Floor(expected) ? 1 : 0);
            var placed = new List<Vector3>();
            for (int attempt = 0; placed.Count < count && attempt < count * 30; attempt++)
            {
                // Area-weighted triangle, uniform point in it.
                float pick = (float)rng.NextDouble() * area;
                int t = 0;
                while (t < areas.Count - 1 && pick > areas[t]) pick -= areas[t++];
                float u = (float)rng.NextDouble(), v = (float)rng.NextDouble();
                if (u + v > 1) { u = 1 - u; v = 1 - v; }
                var a = corners[tri[t * 3]];
                var p = a + (corners[tri[t * 3 + 1]] - a) * u + (corners[tri[t * 3 + 2]] - a) * v;
                if (placed.Any(q => Vector3.DistanceSquared(p, q) < minGap * minGap)) continue;
                placed.Add(p);

                float w = (float)rng.NextDouble() * total;
                int which = 0;
                while (which < weights.Count - 1 && w > weights[which]) w -= weights[which++];
                float s = scale.X + (float)rng.NextDouble() * (scale.Y - scale.X);
                float turn = (yaw.X + (float)rng.NextDouble() * (yaw.Y - yaw.X)) * MathF.PI / 180f;
                var up = alignNormal ? normal : Vector3.UnitY;
                var side = MathF.Abs(up.Y) < 0.9f ? Vector3.Normalize(Vector3.Cross(Vector3.UnitY, up)) : Vector3.UnitX;
                var fwd = Vector3.Cross(side, up);
                var along = side * MathF.Cos(turn) + fwd * MathF.Sin(turn);
                var outward = Vector3.Cross(along, up);
                PropLibrary.Place(m, loaded[which], PropLibrary.Frame(p + normal * offset, along, up, outward, s), material);
            }
        }
        MeshOps.AlignToGrid(m, Enumerable.Range(0, m.Faces.Count).Where(i => m.Faces[i].UVs == null));
        return m;
    }

    /// <summary>A seed from the face's corners (to the millimetre, order-independent), the rule and its seed parameter.</summary>
    static int FaceSeed(Vector3[] corners, string rule, int seed)
    {
        ulong h = 14695981039346656037UL;
        void Add(ulong x) { h ^= x; h *= 1099511628211UL; }
        ulong sum = 0;
        foreach (var c in corners)
            sum += (ulong)(long)MathF.Round(c.X * 1000) * 73856093UL ^ (ulong)(long)MathF.Round(c.Y * 1000) * 19349663UL ^ (ulong)(long)MathF.Round(c.Z * 1000) * 83492791UL;
        Add(sum);
        foreach (var ch in rule) Add(ch);
        Add((ulong)(uint)seed);
        return (int)(h ^ (h >> 32));
    }
}
