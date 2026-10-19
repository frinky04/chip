using System.Numerics;
using Chip.Core.Geometry;
using Chip.Core.Geometry.Primitives;
using Chip.Core.Texturing;

namespace Chip.Core.Dressing;

/// <summary>
/// A shape rule that replaces whole meshes (picked by mesh tag) with something built from their bounding box: the box is the
/// editable form, the build is what ships. The run is the box's longer horizontal side, up is Y. Collision for export stays
/// the box, so a railing collides as a solid barrier and stairs as a smooth ramp.
/// </summary>
public abstract class ProxyKind : RuleKind
{
    public override Stage Stage => Stage.Shape;

    protected static readonly ParamSpec MeshesParam = new("meshes", ParamType.Meshes, null, "Meshes to replace, by their mesh tags (Ctrl+T in object mode).", Required: true);
    protected static readonly ParamSpec MaterialParam = new("material", ParamType.Text, null, "Material for what's built. Default: the material of the mesh it replaces.");

    /// <summary>Builds the replacement for <paramref name="source"/>, or returns a reason it can't.</summary>
    internal abstract (PolyMesh? Mesh, string? Problem) Build(PolyMesh source, Rule rule);

    /// <summary>The box, its run axis (signed), across axis and sizes along them.</summary>
    internal readonly record struct Box(Vector3 Min, Vector3 Max, Vector3 Run, Vector3 Across)
    {
        public float Length => MathF.Abs(Vector3.Dot(Max - Min, Run));
        public float Width => MathF.Abs(Vector3.Dot(Max - Min, Across));
        public float Height => Max.Y - Min.Y;
        /// <summary>The run starts at this point (the box's corner at the start of the run, mid-width, on the ground).</summary>
        public Vector3 Start
        {
            get
            {
                var c = (Min + Max) * 0.5f;
                var s = c - Run * (Length * 0.5f);
                return new Vector3(s.X, Min.Y, s.Z);
            }
        }
    }

    internal static Box BoxOf(PolyMesh m, Vector3? run = null)
    {
        var (min, max) = m.Bounds();
        var r = run ?? (max.X - min.X >= max.Z - min.Z ? Vector3.UnitX : Vector3.UnitZ);
        var across = MathF.Abs(r.X) > 0.5f ? Vector3.UnitZ : Vector3.UnitX;
        return new Box(min, max, r, across);
    }

    internal static string MaterialOf(PolyMesh source, Rule rule) =>
        rule.Text("material") ?? source.Faces.GroupBy(f => f.Material).OrderByDescending(g => g.Count()).First().Key;

    internal static TextureMapping MapOf(PolyMesh source) => source.Faces.Count > 0 ? source.Faces[0].Map : TextureMapping.Default;
}

/// <summary>
/// <c>railing</c>: a box becomes a railing of its length and height: posts at the ends (and every <c>postEvery</c>), a
/// handrail along the top, an optional bottom rail, and balusters between.
/// </summary>
public sealed class RailingKind : ProxyKind
{
    public override string Name => "railing";
    public override string Summary => "Replace tagged boxes with a railing: posts, handrail, bottom rail and balusters, sized by the box.";

    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        MeshesParam,
        new("balusterEvery", ParamType.Number, 0.12f, "Spacing of the balusters (metres).", 0.03, 10),
        new("baluster", ParamType.Number, 0.025f, "Baluster thickness (square, metres).", 0.005, 1),
        new("postEvery", ParamType.Number, 1.5f, "Spacing of the posts; 0 for posts only at the ends (metres).", 0, 100),
        new("post", ParamType.Number, 0.07f, "Post thickness (square, metres).", 0.01, 2),
        new("rail", ParamType.Profile, Profile.Round(0.06f, 8), "Handrail cross-section, centred on the top of the box."),
        new("bottomRail", ParamType.Number, 0.1f, "Height of a bottom rail; 0 for none (metres).", 0, 10),
        MaterialParam,
    ];

    internal override (PolyMesh?, string?) Build(PolyMesh source, Rule rule)
    {
        var box = BoxOf(source);
        float length = box.Length, height = box.Height;
        float post = rule.Number("post"), baluster = rule.Number("baluster");
        if (length < post * 2 || height < 0.05f) return (null, $"\"{source.Name}\" is too small for a railing ({length:0.##} × {height:0.##} m)");
        string material = MaterialOf(source, rule);
        var map = MapOf(source);
        var parts = new List<PolyMesh>();
        var start = box.Start;
        Vector3 At(float along, float up) => start + box.Run * along + Vector3.UnitY * up;
        void Block(float along, float size, float bottom, float top)
        {
            var a = At(along, bottom) - (box.Run + box.Across) * (size * 0.5f);
            var b = At(along, top) + (box.Run + box.Across) * (size * 0.5f);
            parts.Add(MeshOps.CreateBox(Vector3.Min(a, b), Vector3.Max(a, b), material, map));
        }

        // Handrail: the profile centred on the box's top centre line, run end to end.
        var rail = rule.Get<Profile>("rail");
        var lo = rail.Points.Aggregate(Vector2.Min);
        var hi = rail.Points.Aggregate(Vector2.Max);
        var centre = (lo + hi) * 0.5f;
        float railHeight = hi.Y - lo.Y;
        var loop = rail.Points.Select(p => At(0, height - railHeight * 0.5f) + box.Across * (p.X - centre.X) + Vector3.UnitY * (p.Y - centre.Y)).ToList();
        if (Primitive.Prism(loop, box.Run * length, material, map) is { } handrail) parts.Add(handrail);

        // Posts: ends always, evenly between when postEvery > 0.
        float postTop = height - railHeight * 0.5f;
        float postEvery = rule.Number("postEvery");
        int bays = postEvery > 0 ? Math.Max(1, (int)MathF.Round((length - post) / postEvery)) : 1;
        var posts = Enumerable.Range(0, bays + 1).Select(i => post * 0.5f + (length - post) * i / bays).ToList();
        foreach (var p in posts) Block(p, post, 0, postTop);

        // Bottom rail and balusters, per bay.
        float bottom = rule.Number("bottomRail");
        float barSize = MathF.Max(baluster * 1.5f, 0.02f);
        float balusterEvery = rule.Number("balusterEvery");
        for (int i = 0; i < bays; i++)
        {
            float from = posts[i] + post * 0.5f, to = posts[i + 1] - post * 0.5f;
            if (to - from < baluster) continue;
            if (bottom > 0)
            {
                var a = At(from, bottom - barSize * 0.5f) - box.Across * (barSize * 0.5f);
                var b = At(to, bottom + barSize * 0.5f) + box.Across * (barSize * 0.5f);
                parts.Add(MeshOps.CreateBox(Vector3.Min(a, b), Vector3.Max(a, b), material, map));
            }
            int count = Math.Max(0, (int)MathF.Floor((to - from) / balusterEvery) - 1);
            float step = (to - from) / (count + 1);
            for (int k = 1; k <= count; k++) Block(from + step * k, baluster, bottom > 0 ? bottom + barSize * 0.5f : 0, postTop);
        }

        var result = parts[0];
        MeshOps.MergeMeshes(result, parts.Skip(1));
        result.SoftEdges.Clear();
        MeshOps.AutoSmooth(result, 50f); // round handrails shade smooth; boxes stay hard
        return (result, null);
    }
}

/// <summary>
/// <c>stairs</c>: a ramp (or any box) becomes stairs filling its bounding box, climbing toward the ramp's high end, with
/// steps of about <c>stepHeight</c> (rounded to a whole number that fits). Collision stays the ramp.
/// </summary>
public sealed class StairsKind : ProxyKind
{
    public override string Name => "stairs";
    public override string Summary => "Replace tagged ramps with stairs filling their box, climbing toward the high end.";

    public static readonly string[] Rises = ["auto", "+x", "-x", "+z", "-z"];

    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        MeshesParam,
        new("stepHeight", ParamType.Number, 0.18f, "Target height of one step; the count is rounded to fit the box (metres).", 0.02, 2),
        new("rise", ParamType.Choice, "auto", "Which way the stairs climb: auto follows the ramp's slope; +x, -x, +z or -z force it.", Choices: Rises),
        MaterialParam,
    ];

    internal override (PolyMesh?, string?) Build(PolyMesh source, Rule rule)
    {
        var run = rule.Text("rise") switch
        {
            "+x" => Vector3.UnitX, "-x" => -Vector3.UnitX, "+z" => Vector3.UnitZ, "-z" => -Vector3.UnitZ,
            _ => Climb(source),
        };
        var box = BoxOf(source, run);
        if (box.Height < 0.02f || box.Length < 0.02f) return (null, $"\"{source.Name}\" is too flat for stairs");
        int steps = Math.Clamp((int)MathF.Round(box.Height / rule.Number("stepHeight")), 1, 256);
        var frame = PrimitiveFrame.FromBox(box.Min, box.Max, Vector3.UnitY, run);
        var mesh = Primitive.Build(PrimitiveShape.Stairs, new PrimitiveOptions { Steps = steps }, frame, MaterialOf(source, rule), MapOf(source));
        return (mesh, null);
    }

    /// <summary>The horizontal direction the mesh climbs: toward the end whose vertices sit highest (a box climbs along its longer side).</summary>
    static Vector3 Climb(PolyMesh m)
    {
        var (min, max) = m.Bounds();
        (Vector3 Dir, float Rise) best = (max.X - min.X >= max.Z - min.Z ? Vector3.UnitX : Vector3.UnitZ, 0);
        foreach (var axis in (Vector3[])[Vector3.UnitX, Vector3.UnitZ])
        {
            float lo = Vector3.Dot(min, axis), hi = Vector3.Dot(max, axis), span = hi - lo;
            if (span < 1e-4f) continue;
            float Top(bool far) => m.Positions.Where(p => MathF.Abs(Vector3.Dot(p, axis) - (far ? hi : lo)) < span * 0.01f + 1e-4f).Select(p => p.Y).DefaultIfEmpty(min.Y).Max();
            float rise = Top(true) - Top(false);
            if (MathF.Abs(rise) > MathF.Abs(best.Rise) + 1e-3f) best = (rise >= 0 ? axis : -axis, rise);
        }
        return best.Dir;
    }
}
