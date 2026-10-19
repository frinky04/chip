using System.Numerics;
using System.Text.Json.Nodes;
using Chip.Core.Dressing;
using Chip.Core.Geometry;
using Chip.Core.Geometry.Primitives;
using Chip.Core.IO;
using Chip.Core.Scene;
using Chip.Core.Texturing;
using static Chip.Core.Tests.ClipTests;

namespace Chip.Core.Tests;

/// <summary>Dressing phase 2: proxies, repeat, scatter, props, per-mesh overrides and bake.</summary>
public class DressingPhase2Tests
{
    const string RulesPath = "levels/room.dressing.json";

    static MemoryFiles Rules(string rules) => new() { [RulesPath] = $$"""{ "chipDressing": 1, "rules": { {{rules}} } }""" };
    static DressedLevel Dress(Document doc, IDressingFiles files) => new Dresser(files).Dress(doc, RulesPath);

    static List<PolyMesh> Outputs(DressedLevel level, string rule) =>
        level.Meshes.Where(m => level.Origin(m) is { Role: not DressRole.Passthrough } o && o.Rule == rule).ToList();

    static Document WithBox(Vector3 min, Vector3 max, params string[] meshTags)
    {
        var doc = new Document();
        var m = doc.Add(Box(min, max));
        m.Name = "box";
        m.Tags = TagSet.Of(meshTags);
        return doc;
    }

    // ---- Railing ----

    const string Railing = """ "rail": { "kind": "railing", "meshes": { "tags": ["railing"] } } """;

    [Fact]
    public void A_tagged_box_becomes_a_railing_of_its_size()
    {
        var doc = WithBox(Vector3.Zero, new Vector3(3, 1, 0.1f), "railing");
        var level = Dress(doc, Rules(Railing));
        Assert.Empty(level.Diagnostics);
        var rail = Assert.Single(level.Meshes);
        Assert.Equal(DressRole.Proxy, level.Origin(rail).Role);
        Assert.True(rail.Faces.Count > 100);
        var (min, max) = rail.Bounds();
        Assert.Equal(0, min.X, 3);
        Assert.Equal(3, max.X, 3);
        Assert.Equal(1, max.Y, 3);
        Assert.Equal(0, min.Y, 3);
        Assert.Equal(new (int, string, string)[] { (doc.Meshes[0].Id, "rail", "railing") }, level.Proxies);

        var export = level.ForExport();
        Assert.Contains(export.Meshes, m => m.Collision == CollisionMode.Only && m.Faces.Count == 6); // collides as the box
    }

    [Fact]
    public void Per_mesh_overrides_change_one_railing_and_skips_switch_it_off()
    {
        var doc = WithBox(Vector3.Zero, new Vector3(3, 1, 0.1f), "railing");
        var files = Rules(Railing);
        var dresser = new Dresser(files);
        int full = dresser.Dress(doc, RulesPath).Meshes[0].Faces.Count;

        var box = doc.Meshes[0];
        box.Dressing = box.Dressing.WithParam("rail", "balusterEvery", JsonValue.Create(0.5));
        int sparse = dresser.Dress(doc, RulesPath, m => (int)MeshHash.Of(m)).Meshes[0].Faces.Count;
        Assert.True(sparse < full);

        box.Dressing = box.Dressing.WithSkip("rail", true);
        var skipped = dresser.Dress(doc, RulesPath, m => (int)MeshHash.Of(m));
        Assert.Same(box, Assert.Single(skipped.Meshes)); // back to the box
        Assert.Equal([("rail", true)], skipped.Applicable(box).Select(a => (a.Rule.Id, a.Skipped)));
    }

    [Fact]
    public void Bad_overrides_report_the_mesh_and_fall_back_to_the_rule()
    {
        var doc = WithBox(Vector3.Zero, new Vector3(3, 1, 0.1f), "railing");
        var box = doc.Meshes[0];
        box.Dressing = box.Dressing.WithParam("rail", "balustr", JsonValue.Create(0.5));
        var level = Dress(doc, Rules(Railing));
        var e = Assert.Single(level.Diagnostics);
        Assert.Equal("E104", e.Code);
        Assert.Equal(box.Id, e.MeshId);
        Assert.Contains("baluster", e.Fix);
        Assert.Equal(DressRole.Proxy, level.Origin(level.Meshes[0]).Role); // still built, with the rule's values
    }

    [Fact]
    public void A_proxy_rule_with_no_tagged_mesh_suggests_the_tag()
    {
        var level = Dress(WithBox(Vector3.Zero, Vector3.One, "railings"), Rules(Railing));
        var w = Assert.Single(level.Diagnostics);
        Assert.Equal("W402", w.Code);
        Assert.Contains("\"railings\"", w.Fix);
    }

    // ---- Stairs ----

    [Fact]
    public void A_ramp_becomes_stairs_climbing_toward_its_high_end()
    {
        var doc = new Document();
        var frame = PrimitiveFrame.FromBox(new Vector3(0, 0, 0), new Vector3(3, 1, 1), Vector3.UnitY, -Vector3.UnitX); // high end at x = 0
        var ramp = doc.Add(Primitive.Build(PrimitiveShape.Wedge, new PrimitiveOptions(), frame, "dev_a", TextureMapping.Default));
        ramp.Tags = TagSet.Of("ramp");
        var level = Dress(doc, Rules(""" "steps": { "kind": "stairs", "meshes": { "tags": ["ramp"] }, "stepHeight": 0.2 } """));
        Assert.Empty(level.Diagnostics);
        var stairs = Assert.Single(level.Meshes);
        Assert.Equal(DressRole.Proxy, level.Origin(stairs).Role);
        var treads = stairs.Positions.Select(p => MathF.Round(p.Y, 3)).Where(y => y > 0).Distinct().Count();
        Assert.Equal(5, treads); // 1 m / 0.2
        var top = stairs.Positions.Where(p => p.Y > 0.999f).ToList();
        Assert.True(top.All(p => p.X < 0.7f)); // the top step is at the high end of the ramp
    }

    [Fact]
    public void Two_shape_rules_on_one_mesh_conflict()
    {
        var doc = WithBox(Vector3.Zero, new Vector3(3, 1, 0.1f), "railing");
        foreach (var f in doc.Meshes[0].Faces) f.Tags = TagSet.Of("wall");
        var level = Dress(doc, Rules(Railing + """, "soft": { "kind": "bevel", "faces": { "tags": ["wall"] }, "width": 0.01 } """));
        Assert.Contains(level.Diagnostics, d => d.Code == "E330");
        Assert.Equal(DressRole.Passthrough, level.Origin(level.Meshes[0]).Role);
    }

    // ---- Repeat ----

    static Document Room()
    {
        var doc = new Document();
        var floor = doc.Add(Box(new Vector3(0, -0.2f, 0), new Vector3(4, 0, 4)));
        floor.Name = "floor";
        floor.Faces[3].Tags = TagSet.Of("floor");
        foreach (var (a, b) in new[] { (new Vector3(0, 0, 0), new Vector3(4, 2.5f, 0.2f)), (new Vector3(0, 0, 3.8f), new Vector3(4, 2.5f, 4)),
                     (new Vector3(0, 0, 0.2f), new Vector3(0.2f, 2.5f, 3.8f)), (new Vector3(3.8f, 0, 0.2f), new Vector3(4, 2.5f, 3.8f)) })
        {
            var w = doc.Add(Box(a, b));
            for (int f = 0; f < w.Faces.Count; f++)
                if (MathF.Abs(w.FaceNormal(f).Y) < 0.1f && Vector3.Dot(w.FaceNormal(f), new Vector3(2, 1.25f, 2) - w.FaceCenter(f)) > 0) w.Faces[f].Tags = TagSet.Of("wall");
        }
        return doc;
    }

    [Fact]
    public void Repeat_places_props_evenly_along_wall_feet()
    {
        // Props stand on the edge; offset half their depth out so they sit clear of the wall.
        var level = Dress(Room(), Rules(""" "posts": { "kind": "repeat", "faces": { "tags": ["wall"] }, "prop": { "box": [0.1, 1, 0.1] }, "every": 1, "offset": [0.05, 0] } """));
        Assert.Empty(level.Diagnostics);
        var outputs = Outputs(level, "posts");
        Assert.Equal(4, outputs.Count);
        Assert.Equal(4 * 4 * 6, outputs.Sum(m => m.Faces.Count)); // 3.6 m walls: 4 props each, 6 faces a box
        var all = outputs.SelectMany(m => m.Positions).ToList();
        Assert.Equal(0, all.Min(p => p.Y), 4);
        Assert.Equal(1, all.Max(p => p.Y), 4);
        Assert.True(all.All(p => p.X >= 0.2f - 1e-4f && p.Z >= 0.2f - 1e-4f)); // in the room, standing out from the walls
    }

    [Fact]
    public void Repeat_reads_glb_props_relative_to_the_rules_file_and_reports_missing_ones()
    {
        var propDoc = new Document();
        propDoc.Add(Box(new Vector3(-0.05f, 0, -0.05f), new Vector3(0.05f, 0.5f, 0.05f)));
        var files = Rules(""" "lights": { "kind": "repeat", "faces": { "tags": ["wall"] }, "prop": "props/light.glb", "every": 2, "offset": [0, 1.8] } """);
        files.SetBytes("levels/props/light.glb", GlbExporter.Build(propDoc, new MaterialLibrary()));
        var level = Dress(Room(), files);
        Assert.Empty(level.Diagnostics);
        var props = Outputs(level, "lights").SelectMany(m => m.Positions).ToList();
        Assert.Equal(1.8f, props.Min(p => p.Y), 3); // lifted along the wall
        Assert.Equal(4 * 2 * 8, props.Count);       // 2 per wall, 8 corners each

        var missing = Dress(Room(), Rules(""" "lights": { "kind": "repeat", "faces": { "tags": ["wall"] }, "prop": "props/nope.glb" } """));
        var w = Assert.Single(missing.Diagnostics);
        Assert.Equal("W610", w.Code);
        Assert.Contains("levels/props/nope.glb", w.Message);
    }

    // ---- Scatter ----

    const string Rubble = """ "rubble": { "kind": "scatter", "faces": { "tags": ["floor"] }, "props": [{ "box": [0.2, 0.1, 0.2] }, { "cylinder": [0.15, 0.3] }], "weights": [3, 1], "perM2": 1, "minGap": 0.3 } """;

    [Fact]
    public void Scatter_is_deterministic_and_stays_put_when_other_meshes_change()
    {
        var doc = Room();
        var dresser = new Dresser(Rules(Rubble));
        var first = dresser.Dress(doc, RulesPath);
        Assert.Empty(first.Diagnostics);
        var rubble = Assert.Single(Outputs(first, "rubble"));
        Assert.InRange(rubble.Faces.Count, 6 * 8, 50 * 20);
        Assert.Equal(first.ContentHash, Dress(Room(), Rules(Rubble)).ContentHash); // same level, same scatter

        var wall = doc.Meshes[1];
        for (int i = 0; i < wall.Positions.Count; i++) wall.Positions[i] += new Vector3(0, 0, -0.1f);
        var after = dresser.Dress(doc, RulesPath, m => (int)MeshHash.Of(m));
        Assert.Same(rubble, Assert.Single(Outputs(after, "rubble"))); // not even rebuilt
        Assert.All(rubble.Positions, p => Assert.InRange(p.Y, -0.01f, 0.5f));
    }

    [Fact]
    public void Scatter_seed_changes_the_arrangement()
    {
        var a = Dress(Room(), Rules(Rubble)).ContentHash;
        var b = Dress(Room(), Rules(Rubble.Replace("\"minGap\": 0.3", "\"minGap\": 0.3, \"seed\": 5"))).ContentHash;
        Assert.NotEqual(a, b);
    }

    // ---- Bake ----

    [Fact]
    public void Bake_turns_dressing_into_ordinary_meshes_and_stops_following_the_rules()
    {
        var doc = Room();
        doc.Add(Box(new Vector3(1, 0, 1), new Vector3(3, 1, 1.1f))).Tags = TagSet.Of("railing");
        var railBox = doc.Meshes[^1];
        var files = Rules(Railing + """, "base": { "kind": "sweep", "faces": { "tags": ["wall"] } } """);
        var level = Dress(doc, files);
        var south = doc.Meshes[1];
        int before = doc.Meshes.Count;

        var result = DressingBake.Bake(doc, level, [south.Id, railBox.Id]);
        Assert.Equal(2, result.Added.Count); // the south baseboard and the railing
        Assert.Equal(before + 2, doc.Meshes.Count);
        Assert.True(south.Dressing.Skips("base"));
        Assert.Equal(CollisionMode.Only, railBox.Collision); // the railing's box keeps colliding
        Assert.All(result.Added, id => Assert.False(doc.Find(id)!.Locked));

        var again = Dress(doc, files);
        Assert.Equal(3, Outputs(again, "base").Count);   // the baked wall no longer gets a generated baseboard
        Assert.Empty(Outputs(again, "rail"));
    }

    // ---- Overrides in the file and undo ----

    [Fact]
    public void Mesh_dressing_round_trips_and_counts_as_a_content_change()
    {
        var doc = WithBox(Vector3.Zero, Vector3.One, "railing");
        var m = doc.Meshes[0];
        ulong before = MeshHash.Of(m);
        m.Dressing = m.Dressing.WithParam("rail", "post", JsonValue.Create(0.1)).WithSkip("debris", true);
        Assert.NotEqual(before, MeshHash.Of(m));
        var back = DocumentSerializer.Deserialize(DocumentSerializer.Serialize(doc)).Meshes[0];
        Assert.Equal(m.Dressing, back.Dressing);
        Assert.Equal("{\"post\":0.1}", back.Dressing.OverridesFor("rail"));
        Assert.True(back.Dressing.Skips("debris"));
        Assert.True(m.Dressing.WithParam("rail", "post", null).WithSkip("debris", false).IsEmpty);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(19)]
    public void Incremental_matches_fresh_with_every_kind(int seed)
    {
        var doc = Room();
        var files = Rules(Railing + ", " + Rubble + """
            , "base": { "kind": "sweep", "faces": { "tags": ["wall"] } },
            "posts": { "kind": "repeat", "faces": { "tags": ["wall"] }, "prop": { "box": [0.1, 1, 0.1] }, "every": 1.5 }
            """);
        var dresser = new Dresser(files);
        var rng = new Random(seed);
        var revisions = new Dictionary<int, int>();
        int Rev(PolyMesh m) => revisions.TryGetValue(m.Id, out var r) ? r : 0;
        for (int step = 0; step < 40; step++)
        {
            var m = doc.Meshes[rng.Next(doc.Meshes.Count)];
            switch (rng.Next(5))
            {
                case 0: for (int i = 0; i < m.Positions.Count; i++) m.Positions[i] += new Vector3(rng.Next(-1, 2) * 0.25f, 0, rng.Next(-1, 2) * 0.25f); break;
                case 1: m.Tags = m.Tags.Contains("railing") ? TagSet.Empty : TagSet.Of("railing"); break;
                case 2: m.Dressing = m.Dressing.WithParam("posts", "every", JsonValue.Create(rng.Next(1, 4) * 0.5)); break;
                case 3: m.Dressing = m.Dressing.WithSkip("rubble", !m.Dressing.Skips("rubble")); break;
                case 4: doc.Add(Box(new Vector3(rng.Next(1, 3), 0, 1), new Vector3(3, 1, 1.2f))).Tags = TagSet.Of("railing"); break;
            }
            revisions[m.Id] = Rev(m) + 1;
            var incremental = dresser.Dress(doc, RulesPath, Rev);
            var fresh = new Dresser(files).Dress(doc, RulesPath, Rev);
            Assert.True(fresh.ContentHash == incremental.ContentHash, $"step {step}");
            Assert.Equal(fresh.Diagnostics.Select(x => x.ToString()).Order(), incremental.Diagnostics.Select(x => x.ToString()).Order());
        }
    }

    [Fact]
    public void Schema_covers_every_kind()
    {
        var schema = DressingSchema.Generate();
        foreach (var kind in RuleKind.All) Assert.Contains($"\"const\": \"{kind.Name}\"", schema);
        Assert.Contains("\"prop\"", schema);
    }
}
