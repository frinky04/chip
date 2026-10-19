using System.Numerics;
using System.Text.Json;
using Chip.Core.Dressing;
using Chip.Core.Geometry;
using Chip.Core.IO;
using Chip.Core.Scene;
using static Chip.Core.Tests.ClipTests;

namespace Chip.Core.Tests;

public class DressingTests
{
    const string RulesPath = "levels/room.dressing.json";

    /// <summary>
    /// A 4 × 4 m room: a floor slab and four separate 20 cm wall boxes standing on it, overlapping at the corners like a
    /// real blockout (south and north run the full width; west and east fit between them). Faces looking into the room
    /// are tagged "wall".
    /// </summary>
    static Document Room(bool door = false)
    {
        var doc = new Document();
        doc.Add(Named(Box(new Vector3(0, -0.2f, 0), new Vector3(4, 0, 4)), "floor"));
        if (door)
        {
            doc.Add(Named(Box(new Vector3(0, 0, 0), new Vector3(1.5f, 2.5f, 0.2f)), "south-a"));
            doc.Add(Named(Box(new Vector3(2.5f, 0, 0), new Vector3(4, 2.5f, 0.2f)), "south-b"));
        }
        else doc.Add(Named(Box(new Vector3(0, 0, 0), new Vector3(4, 2.5f, 0.2f)), "south"));
        doc.Add(Named(Box(new Vector3(0, 0, 3.8f), new Vector3(4, 2.5f, 4)), "north"));
        doc.Add(Named(Box(new Vector3(0, 0, 0.2f), new Vector3(0.2f, 2.5f, 3.8f)), "west"));
        doc.Add(Named(Box(new Vector3(3.8f, 0, 0.2f), new Vector3(4, 2.5f, 3.8f)), "east"));
        var centre = new Vector3(2, 1.25f, 2);
        foreach (var m in doc.Meshes.Skip(1))
            for (int f = 0; f < m.Faces.Count; f++)
            {
                var n = m.FaceNormal(f);
                if (MathF.Abs(n.Y) < 0.1f && Vector3.Dot(n, centre - m.FaceCenter(f)) > 0) m.Faces[f].Tags = TagSet.Of("wall");
            }
        return doc;
    }

    static PolyMesh Named(PolyMesh m, string name)
    {
        m.Name = name;
        return m;
    }

    static MemoryFiles Rules(string rules, string? path = null) =>
        new() { [path ?? RulesPath] = $$"""{ "chipDressing": 1, "rules": { {{rules}} } }""" };

    const string Baseboard = """ "baseboard": { "kind": "sweep", "faces": { "tags": ["wall"] } } """;

    static DressedLevel Dress(Document doc, MemoryFiles files) => new Dresser(files).Dress(doc, RulesPath);

    static List<PolyMesh> Added(DressedLevel level, string rule) =>
        level.Meshes.Where(m => level.Origin(m) is { Role: DressRole.Added } o && o.Rule == rule).ToList();

    // ---- Sweep ----

    [Fact]
    public void Baseboard_runs_round_a_room_of_separate_walls_and_mitres_the_corners()
    {
        var doc = Room();
        var level = Dress(doc, Rules(Baseboard));
        Assert.Empty(level.Diagnostics);
        var trims = Added(level, "baseboard");
        Assert.Equal(4, trims.Count);
        Assert.All(trims, t => Assert.Equal(2, t.Faces.Count)); // top and front; no caps on a closed loop
        Assert.All(trims, t => Assert.True(t.Locked && t.Id < 0 && t.Collision == CollisionMode.None));

        var points = trims.SelectMany(t => t.Positions).ToList();
        var min = points.Aggregate(Vector3.Min);
        var max = points.Aggregate(Vector3.Max);
        Assert.Equal(new Vector3(0.2f, 0, 0.2f), min, new Vector3Comparer(1e-4f));
        Assert.Equal(new Vector3(3.8f, 0.12f, 3.8f), max, new Vector3Comparer(1e-4f));
        // The outer top corner of the south-west mitre is shared by both pieces.
        var corner = new Vector3(0.215f, 0.12f, 0.215f);
        Assert.Equal(2, trims.Count(t => t.Positions.Any(p => Vector3.Distance(p, corner) < 1e-4f)));
        Assert.Equal(doc.Meshes.Count + 4, level.Meshes.Count); // the blockout passes through untouched
    }

    [Fact]
    public void A_doorway_stops_the_trim_and_it_wraps_the_jambs()
    {
        var level = Dress(Room(door: true), Rules(Baseboard));
        var stats = Assert.Single(level.Stats);
        Assert.Equal(7, stats.Targets); // west, north, east, two south pieces, two jambs
        var south = Added(level, "baseboard").Where(t => t.Positions.All(p => p.Z < 0.3f)).ToList();
        Assert.Equal(2, south.Count);
        Assert.All(south, t => Assert.Equal(2 + 2 + 1, t.Faces.Count)); // run + jamb (2 faces each) + a cap at the doorway
    }

    [Fact]
    public void Editing_one_wall_reruns_only_jobs_that_touch_it()
    {
        var doc = Room();
        var dresser = new Dresser(Rules(Baseboard));
        dresser.Dress(doc, RulesPath);
        Assert.True(dresser.LastRebuilds > 0);
        dresser.Dress(doc, RulesPath);
        Assert.Equal(0, dresser.LastRebuilds);

        var north = doc.Meshes.First(m => m.Name == "north");
        int outside = Enumerable.Range(0, north.Faces.Count).First(f => north.FaceNormal(f).Z > 0.9f);
        north.Faces[outside].Material = "dev_b"; // an untagged face: segments recheck, no trim rebuilds
        var level = dresser.Dress(doc, RulesPath);
        Assert.Equal(4, dresser.LastRebuilds); // north, its neighbours west and east, and the floor under it
        Assert.Equal(4, Added(level, "baseboard").Count);

        for (int i = 0; i < north.Positions.Count; i++) north.Positions[i] -= new Vector3(0, 0, 0.5f); // room shrinks
        var moved = dresser.Dress(doc, RulesPath);
        Assert.Equal(new Dresser(Rules(Baseboard)).Dress(doc, RulesPath).ContentHash, moved.ContentHash); // incremental == fresh
        // The new north-west corner mitres where the west trim now ends; west and east stick out past the north wall,
        // and their exposed feet beyond it get trim too.
        var corner = new Vector3(0.215f, 0.12f, 3.285f);
        Assert.Equal(2, Added(moved, "baseboard").Count(t => t.Positions.Any(p => Vector3.Distance(p, corner) < 1e-4f)));
    }

    [Fact]
    public void Generated_ids_and_content_are_stable()
    {
        var doc = Room();
        var dresser = new Dresser(Rules(Baseboard));
        var a = dresser.Dress(doc, RulesPath);
        var b = dresser.Dress(doc, RulesPath);
        Assert.Equal(Added(a, "baseboard").Select(m => m.Id), Added(b, "baseboard").Select(m => m.Id));
        Assert.Equal(a.Revision(Added(a, "baseboard")[0]), b.Revision(Added(b, "baseboard")[0]));
        Assert.Equal(a.ContentHash, new Dresser(Rules(Baseboard)).Dress(Room(), RulesPath).ContentHash);
    }

    [Fact]
    public void Skip_tag_and_hidden_meshes_opt_out()
    {
        var doc = Room();
        var west = doc.Meshes.First(m => m.Name == "west");
        foreach (var f in west.Faces) if (f.Tags.Contains("wall")) f.Tags = f.Tags.Add("skip:baseboard");
        doc.Meshes.First(m => m.Name == "east").Hidden = true;
        var level = Dress(doc, Rules(Baseboard));
        Assert.Equal(2, Added(level, "baseboard").Count); // south and north
    }

    [Fact]
    public void Round_profiles_run_round_concave_corners_inside_one_mesh()
    {
        // A plinth: a 4 m box with a 2 m block raised on its top, so the block's sides meet the top in four inside corners.
        var m = Box(Vector3.Zero, new Vector3(4, 1, 4));
        MeshOps.Inset(m, [3]).Apply(m, 1, outset: false);
        var (move, _) = MeshOps.ExtrudeFaces(m, [3]);
        foreach (var v in move) m.Positions[v] += Vector3.UnitY;
        var doc = new Document();
        doc.Add(m);

        var level = Dress(doc, Rules(""" "pipe": { "kind": "sweep", "faces": { "facing": "upright" }, "edges": "concave", "profile": { "round": 0.1 } } """));
        Assert.Empty(level.Diagnostics);
        Assert.Equal(4, level.Stats[0].Targets);
        var pipe = Assert.Single(Added(level, "pipe"));
        Assert.Equal(4 * 8, pipe.Faces.Count); // eight sides per run, no caps on the closed loop
        Assert.NotEmpty(pipe.SoftEdges);        // round profiles shade smooth
        var min = pipe.Positions.Aggregate(Vector3.Min);
        Assert.Equal(1, min.Y, 4);               // sits on the top
        Assert.Equal(0.9f, min.X, 4);            // and against the block (1 m in, pipe 10 cm across)
    }

    // ---- Bevel ----

    const string BevelAll = """ "soft": { "kind": "bevel", "faces": { "tags": ["wall"] }, "width": 0.1 } """;

    static Document TaggedBox()
    {
        var doc = new Document();
        var m = doc.Add(Box(Vector3.Zero, new Vector3(2)));
        foreach (var f in m.Faces) f.Tags = TagSet.Of("wall");
        return doc;
    }

    [Fact]
    public void Bevel_replaces_the_mesh_with_a_bevelled_copy_and_leaves_the_blockout_alone()
    {
        var doc = TaggedBox();
        var level = Dress(doc, Rules(BevelAll));
        var shown = Assert.Single(level.Meshes);
        Assert.Equal(DressRole.Modified, level.Origin(shown).Role);
        Assert.Equal(6 + 12 + 8, shown.Faces.Count);
        Assert.Equal(6, doc.Meshes[0].Faces.Count);
        Assert.True(IsConsistentlyWound(shown));

        var export = level.ForExport();
        Assert.Equal(2, export.Meshes.Count);
        Assert.Equal(CollisionMode.None, export.Meshes[0].Collision);
        Assert.Equal(26, export.Meshes[0].Faces.Count);
        Assert.Equal(CollisionMode.Only, export.Meshes[1].Collision); // collision stays the blockout box
        Assert.Equal(6, export.Meshes[1].Faces.Count);
        Assert.All(export.Meshes, m => Assert.True(m.Id > 0));
    }

    [Fact]
    public void Only_edges_between_matching_faces_at_the_angle_bevel()
    {
        var doc = TaggedBox();
        var m = doc.Meshes[0];
        m.Faces[2].Tags = TagSet.Empty; // top and bottom untagged: only the four upright corners qualify
        m.Faces[3].Tags = TagSet.Empty;
        var level = Dress(doc, Rules(BevelAll));
        Assert.Equal(6 + 4, level.Meshes[0].Faces.Count);
    }

    [Fact]
    public void A_bevel_too_wide_for_the_mesh_warns_and_leaves_it()
    {
        var doc = TaggedBox();
        var level = Dress(doc, Rules(""" "soft": { "kind": "bevel", "faces": { "tags": ["wall"] }, "width": 1.5 } """));
        Assert.Same(doc.Meshes[0], Assert.Single(level.Meshes));
        var w = Assert.Single(level.Diagnostics);
        Assert.Equal("W310", w.Code);
        Assert.Equal(doc.Meshes[0].Id, w.MeshId);
        Assert.Contains("1", w.Fix);
    }

    [Fact]
    public void Two_shape_rules_on_one_mesh_is_an_error_and_neither_applies()
    {
        var doc = TaggedBox();
        var level = Dress(doc, Rules(BevelAll + """, "other": { "kind": "bevel", "faces": { "tags": ["wall"] }, "width": 0.05 } """));
        Assert.Same(doc.Meshes[0], Assert.Single(level.Meshes));
        Assert.Contains(level.Diagnostics, d => d.Code == "E330" && d.Message.Contains("soft") && d.Message.Contains("other"));
    }

    // ---- Rules files ----

    [Fact]
    public void Imports_vars_and_level_overrides_merge_by_rule_id()
    {
        var files = new MemoryFiles
        {
            ["styles/industrial.dressing.json"] = """
                {
                  "chipDressing": 1,
                  "vars": { "trimHeight": 0.12, "trim": "metal" },
                  "rules": {
                    "baseboard": { "kind": "sweep", "faces": { "tags": ["wall"] }, "profile": { "rect": [0.02, "$trimHeight"] }, "material": "$trim" },
                    "crown": { "kind": "sweep", "faces": { "tags": ["wall"] }, "onto": { "facing": "down" } }
                  }
                }
                """,
            [RulesPath] = """
                {
                  "chipDressing": 1,
                  // comments are fine
                  "imports": ["../styles/industrial.dressing.json"],
                  "vars": { "trimHeight": 0.2 },
                  "rules": {
                    "crown": { "enabled": false },
                    "baseboard": { "material": "wood" },
                  }
                }
                """,
        };
        var rules = RuleSet.Load(files, RulesPath);
        Assert.Empty(rules.Diagnostics);
        Assert.Equal(1, rules.ImportCount);
        Assert.Equal(["baseboard", "crown"], rules.Rules.Select(r => r.Id));
        var baseboard = rules.Rules[0];
        Assert.Equal("wood", baseboard.Text("material"));
        Assert.Equal(0.2f, baseboard.Get<Profile>("profile").Points.Max(p => p.Y), 5); // the level's var wins
        Assert.False(rules.Rules[1].Enabled);

        var trims = Added(new Dresser(files).Dress(Room(), RulesPath), "baseboard");
        Assert.All(trims, t => Assert.All(t.Faces, f => Assert.Equal("wood", f.Material)));
    }

    static Diagnostic Single(string rules, string code)
    {
        var set = RuleSet.Load(Rules(rules), RulesPath);
        return Assert.Single(set.Diagnostics, d => d.Code == code);
    }

    [Fact]
    public void Mistakes_say_where_what_and_how_to_fix()
    {
        var typo = Single(""" "soft": { "kind": "bevel", "faces": { "tags": ["wall"] }, "widht": 0.1 } """, "E104");
        Assert.Contains("did you mean \"width\"", typo.Fix);
        Assert.Equal("rules.soft.widht", typo.Path);
        Assert.Equal(1, typo.Pos!.Value.Line);

        var quoted = Single(""" "soft": { "kind": "bevel", "faces": { "tags": ["wall"] }, "width": "0.8" } """, "E201");
        Assert.Contains("write 0.8 without quotes", quoted.Fix);

        var cm = Single(""" "soft": { "kind": "bevel", "faces": { "tags": ["wall"] }, "width": "12cm" } """, "E201");
        Assert.Contains("0.12", cm.Fix);

        Assert.Contains("did you mean \"sweep\"", Single(""" "trim": { "kind": "swep", "faces": {} } """, "E105").Fix);
        Assert.Contains("no variable \"height\"", Single(""" "trim": { "kind": "sweep", "faces": {}, "profile": { "rect": [0.1, "$height"] } } """, "E301").Message);
        Assert.Contains("Wall", Single(""" "trim": { "kind": "sweep", "faces": { "tags": ["Wall"] } } """, "E203").Message);
        Assert.Contains("\"kind\"", Single(""" "trim": { "faces": {} } """, "E302").Fix);
        Assert.Contains("faces", Single(""" "trim": { "kind": "sweep" } """, "E204").Message);
        Assert.Equal("E202", Single(""" "soft": { "kind": "bevel", "faces": {}, "segments": 99 } """, "E202").Code);
        Assert.Contains("\"trim-a\"", Single(""" "Trim A": { "kind": "sweep", "faces": {} } """, "E203").Fix);
    }

    [Fact]
    public void Bad_json_reports_its_line_and_a_broken_rule_doesnt_stop_the_others()
    {
        var bad = RuleSet.Load(new MemoryFiles { [RulesPath] = "{\n  \"chipDressing\": 1,\n  \"rules\": { \"a\": { \"kind\": } }\n}" }, RulesPath);
        var e = Assert.Single(bad.Diagnostics);
        Assert.Equal("E100", e.Code);
        Assert.Equal(3, e.Pos!.Value.Line);

        var files = Rules(Baseboard + """, "broken": { "kind": "sweep", "faces": { "tags": ["wall"] }, "edges": "sides" } """);
        var level = Dress(Room(), files);
        Assert.Equal(["broken"], level.Rules.Broken);
        Assert.Equal(4, Added(level, "baseboard").Count);
        Assert.Contains(level.Diagnostics, d => d.Code == "E201" && d.Path == "rules.broken.edges");
    }

    [Fact]
    public void A_rule_that_matches_nothing_says_why_and_suggests_the_tag()
    {
        var level = Dress(Room(), Rules(""" "trim": { "kind": "sweep", "faces": { "tags": ["wal"] } } """));
        var w = Assert.Single(level.Diagnostics);
        Assert.Equal("W402", w.Code);
        Assert.Contains("did you mean the tag \"wall\"", w.Fix);

        var onto = Dress(Room(), Rules(""" "crown": { "kind": "sweep", "faces": { "tags": ["wall"] }, "onto": { "facing": "down" } } """));
        Assert.Contains("onto", Assert.Single(onto.Diagnostics).Message);
    }

    [Fact]
    public void Edited_rules_files_reload()
    {
        var files = Rules(Baseboard);
        var dresser = new Dresser(files);
        var doc = Room();
        Assert.Equal(4, Added(dresser.Dress(doc, RulesPath), "baseboard").Count);
        files[RulesPath] = """{ "chipDressing": 1, "rules": { "baseboard": { "kind": "sweep", "faces": { "tags": ["wall"] }, "enabled": false } } }""";
        Assert.Equal(4, Added(dresser.Dress(doc, RulesPath, checkFiles: false), "baseboard").Count); // not checked yet: old rules
        Assert.Empty(Added(dresser.Dress(doc, RulesPath), "baseboard"));
    }

    [Fact]
    public void Rules_check_reports_and_exits_by_severity()
    {
        var doc = Room();
        doc.DressingPath = "room.dressing.json";
        var output = new StringWriter();
        Assert.Equal(0, RulesCheck.Run(doc, "levels/room.chip", Rules(Baseboard), output));
        Assert.Contains("baseboard", output.ToString());
        Assert.Contains("0 errors, 0 warnings", output.ToString());

        Assert.Equal(1, RulesCheck.Run(doc, "levels/room.chip", Rules(""" "trim": { "kind": "sweep", "faces": { "tags": ["nope"] } } """), new StringWriter()));
        var json = new StringWriter();
        Assert.Equal(2, RulesCheck.Run(doc, "levels/room.chip", Rules(""" "trim": { "kind": "sweep" } """), json, json: true));
        using var parsed = JsonDocument.Parse(json.ToString());
        Assert.Equal(1, parsed.RootElement.GetProperty("errors").GetInt32());

        doc.DressingPath = null;
        Assert.Equal(3, RulesCheck.Run(doc, "levels/other.chip", Rules(Baseboard), new StringWriter()));
        Assert.Equal(0, RulesCheck.Run(doc, "levels/room.chip", Rules(Baseboard), new StringWriter())); // the default name
    }

    [Fact]
    public void The_schema_and_starter_file_are_valid_and_agree_with_the_loader()
    {
        using var schema = JsonDocument.Parse(DressingSchema.Generate());
        var rules = schema.RootElement.GetProperty("properties").GetProperty("rules");
        Assert.Contains("sweep", rules.ToString());
        Assert.Contains("bevel", rules.ToString());

        var starter = RuleSet.Load(new MemoryFiles { [RulesPath] = DressingSchema.Starter("room") }, RulesPath);
        Assert.Empty(starter.Diagnostics);
        Assert.Equal(4, Added(new Dresser(new MemoryFiles { [RulesPath] = DressingSchema.Starter("room") }).Dress(Room(), RulesPath), "baseboard").Count);
    }

    [Fact]
    public void The_level_remembers_its_rules_file()
    {
        var doc = Room();
        doc.DressingPath = "../styles/room.dressing.json";
        Assert.Equal("../styles/room.dressing.json", DocumentSerializer.Deserialize(DocumentSerializer.Serialize(doc)).DressingPath);
        Assert.Equal("styles/room.dressing.json", DressingRules.PathFor(doc, "levels/room.chip", new MemoryFiles()));
    }

    sealed class Vector3Comparer(float eps) : IEqualityComparer<Vector3>
    {
        public bool Equals(Vector3 a, Vector3 b) => Vector3.Distance(a, b) <= eps;
        public int GetHashCode(Vector3 v) => 0;
    }
}
