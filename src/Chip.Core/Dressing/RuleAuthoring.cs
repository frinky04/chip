using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using Chip.Core.Geometry;

namespace Chip.Core.Dressing;

/// <summary>
/// A ready-made rule the editor offers under "Add rule" and "Dress with": a definition with sensible values and the tag it
/// selects by (<see cref="MeshTag"/>: a mesh tag, for proxies).
/// </summary>
public sealed record Recipe(string Id, string Label, string Description, string Tag, bool MeshTag, string Json)
{
    public JsonObject Rule() => (JsonObject)JsonNode.Parse(Json)!;
    public RuleKind Kind => RuleKind.Find(Rule()["kind"]!.GetValue<string>())!;
}

/// <summary>The recipes, in menu order. Each passes <c>--rules-check</c> as written.</summary>
public static class Recipes
{
    public static readonly IReadOnlyList<Recipe> All =
    [
        new("baseboard", "Baseboard", "A strip along the foot of every wall, where it stands on a floor.", "wall", false,
            """{ "kind": "sweep", "faces": { "tags": ["wall"], "facing": "upright" }, "profile": { "rect": [0.015, 0.12] } }"""),
        new("crown", "Crown moulding", "A moulding where walls meet the ceiling.", "wall", false,
            """{ "kind": "sweep", "faces": { "tags": ["wall"], "facing": "upright" }, "onto": { "facing": "down" }, "profile": { "cove": 0.08 } }"""),
        new("frame", "Frame", "A trim round the outline of the tagged faces: door and window frames, panels.", "frame", false,
            """{ "kind": "sweep", "faces": { "tags": ["frame"] }, "edges": "border", "profile": { "rect": [0.02, 0.06] } }"""),
        new("corner-pipe", "Corner pipe", "A pipe along the inside corners of the tagged faces.", "pipe", false,
            """{ "kind": "sweep", "faces": { "tags": ["pipe"] }, "edges": "concave", "profile": { "round": 0.1, "sides": 12 } }"""),
        new("corner-bevel", "Corner bevel", "Softens the outside corners between tagged faces.", "wall", false,
            """{ "kind": "bevel", "faces": { "tags": ["wall"] }, "width": 0.02 }"""),
        new("wall-lights", "Wall lights", "A light every few metres along the walls, at head height.", "wall", false,
            """{ "kind": "repeat", "faces": { "tags": ["wall"], "facing": "upright" }, "prop": { "box": [0.2, 0.12, 0.08] }, "every": 3, "offset": [0, 1.8] }"""),
        new("floor-clutter", "Floor clutter", "Boxes and rubble scattered over the floors.", "floor", false,
            """{ "kind": "scatter", "faces": { "tags": ["floor"], "facing": "up" }, "props": [{ "box": [0.3, 0.2, 0.3] }, { "box": [0.5, 0.3, 0.4] }], "perM2": 0.3, "minGap": 0.4, "scale": [0.8, 1.2] }"""),
        new("railing", "Railing", "Tagged boxes become railings of their size.", "railing", true,
            """{ "kind": "railing", "meshes": { "tags": ["railing"] } }"""),
        new("stairs", "Stairs", "Tagged ramps become stairs climbing the same slope.", "ramp", true,
            """{ "kind": "stairs", "meshes": { "tags": ["ramp"] } }"""),
    ];

    public static Recipe? Find(string id) => All.FirstOrDefault(r => r.Id == id);
}

/// <summary>Where a trim or a row of props runs, in words: the common combinations of <c>edges</c> and <c>onto</c>.</summary>
public sealed record EdgePlace(string Id, string Label, string Tip, string Edges, string? OntoFacing);

public static class EdgePlaces
{
    public static readonly IReadOnlyList<EdgePlace> All =
    [
        new("floor", "Along the floor", "Where the faces stand on a floor (an up-facing face of any mesh): baseboards.", "contact", "up"),
        new("ceiling", "Along the ceiling", "Where the faces meet a ceiling (a down-facing face of any mesh): crown moulding.", "contact", "down"),
        new("outside", "Outside corners", "Edges where the faces meet sticking out (box corners).", "convex", null),
        new("inside", "Inside corners", "Edges where the faces meet folding in (room corners).", "concave", null),
        new("outline", "Outline", "The outline of the faces: frames round doors, windows and panels.", "border", null),
        new("open", "Open edges", "Edges with no face on the other side (the rim of a single plane).", "boundary", null),
    ];

    /// <summary>The place a rule's (resolved) <c>edges</c> and <c>onto</c> describe, or null when <c>onto</c> is anything else.</summary>
    public static EdgePlace? Of(JsonNode? edges, JsonNode? onto)
    {
        string kind = ParamJson.Text(edges) ?? "contact";
        if (kind != "contact") return All.FirstOrDefault(p => p.Edges == kind);
        string? facing = onto switch
        {
            null => "up",
            JsonObject o when o.Count == 1 && ParamJson.Text(o["facing"]) is { } f => f,
            _ => null,
        };
        return All.FirstOrDefault(p => p.Edges == "contact" && p.OntoFacing == facing);
    }

    /// <summary>The <c>edges</c> and <c>onto</c> values for <paramref name="place"/> (null: leave the key out).</summary>
    public static (JsonNode? Edges, JsonNode? Onto) Values(EdgePlace place) =>
        (JsonValue.Create(place.Edges), place.OntoFacing is "down" ? new JsonObject { ["facing"] = "down" } : null);
}

/// <summary>Reading and writing parameter values as JSON, for the editor's fields.</summary>
public static class ParamJson
{
    public static string? Text(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    public static double? Number(JsonNode? n) =>
        n is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.Number ? double.Parse(v.ToJsonString(), CultureInfo.InvariantCulture) : null;

    public static bool? Bool(JsonNode? n) => n is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

    public static Vector2? Pair(JsonNode? n) =>
        n is JsonArray { Count: 2 } a && Number(a[0]) is { } x && Number(a[1]) is { } y ? new Vector2((float)x, (float)y) : null;

    public static double[]? Numbers(JsonNode? n) =>
        n is JsonArray a && a.All(i => Number(i) != null) ? a.Select(i => Number(i)!.Value).ToArray() : null;

    public static string[] Names(JsonNode? n) => n is JsonArray a ? a.Select(Text).OfType<string>().ToArray() : [];

    /// <summary>A number rounded to what a rules file needs (tenths of a millimetre for lengths).</summary>
    public static JsonValue Num(double v) => JsonValue.Create(Math.Round(v, 5))!;

    public static JsonArray Pair(Vector2 v) => new(Num(v.X), Num(v.Y));

    public static JsonArray List(IEnumerable<string> names) => new(names.Select(s => (JsonNode)JsonValue.Create(s)!).ToArray());

    public static bool IsPropFile(string s) => s.EndsWith(".glb", StringComparison.OrdinalIgnoreCase) || s.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase);

    /// <summary>The spec's default as JSON (what the loader uses when the key is left out), or null when it has none.</summary>
    public static JsonNode? Default(ParamSpec spec) => spec.Default switch
    {
        null => null,
        float f => Num(f),
        int i => JsonValue.Create(i),
        bool b => JsonValue.Create(b),
        string s => JsonValue.Create(s),
        Vector2 v => Pair(v),
        FaceSelector fs => Faces(fs),
        Profile { Json: { } json } => JsonNode.Parse(json),
        _ => null,
    };

    public static JsonObject Faces(FaceSelector s)
    {
        var o = new JsonObject();
        if (s.Tags.Length > 0) o["tags"] = List(s.Tags);
        if (s.NotTags.Length > 0) o["notTags"] = List(s.NotTags);
        if (s.Materials.Length > 0) o["materials"] = List(s.Materials);
        if (s.Facing != null) o["facing"] = s.Facing;
        return o;
    }

    /// <summary>A face selector written as JSON, read leniently (bad parts are ignored; the loader reports them).</summary>
    public static FaceSelector ReadFaces(JsonNode? n) => n is JsonObject o
        ? new FaceSelector(Names(o["tags"]), Names(o["notTags"]), Names(o["materials"]), Text(o["facing"]) is { } f && FaceSelector.Facings.Contains(f) ? f : null)
        : FaceSelector.Any;

    /// <summary>
    /// The tag a rule selects by (the first of its face or mesh <c>tags</c>), and whether it is a mesh tag. Null when the
    /// rule picks faces some other way (by material or facing only).
    /// </summary>
    public static (string? Tag, bool Mesh) PrimaryTag(JsonObject raw, Func<JsonNode?, JsonNode?> resolve)
    {
        if (resolve(raw["meshes"]) is JsonObject m) return (Names(resolve(m["tags"])).FirstOrDefault(), true);
        if (resolve(raw["faces"]) is JsonObject f) return (Names(resolve(f["tags"])).FirstOrDefault(), false);
        return (null, false);
    }

    /// <summary>
    /// The faces of <paramref name="mesh"/> that would match <paramref name="faces"/> if they carried its tags (its facing and
    /// materials hold, its notTags don't): what "Dress with" tags when a whole mesh is selected.
    /// </summary>
    public static IEnumerable<int> Taggable(PolyMesh mesh, FaceSelector faces)
    {
        var loose = faces with { Tags = [] };
        for (int f = 0; f < mesh.Faces.Count; f++)
            if (loose.Matches(mesh, f, mesh.FaceNormal(f))) yield return f;
    }
}
