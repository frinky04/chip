using System.Numerics;
using Chip.Core.Dressing;
using Chip.Core.Geometry;
using Chip.Core.Geometry.Primitives;
using Chip.Core.IO;
using Chip.Core.Scene;
using Chip.Core.Texturing;

namespace Chip.Editor.Features.Dressing;

/// <summary>
/// A small tagged level with a rules file, to try dressing (Tools > Open dressing sample) and for captures: an 8 × 6 m room
/// of separate wall boxes overlapping at the corners, a doorway, a pillar, a plinth, and a platform with a ramp and two
/// railing boxes. Rules: baseboards, bevelled pillar corners, a pipe round the plinth, railings, stairs, wall lights and
/// floor rubble: every kind.
/// </summary>
public static class DressingSample
{
    public const string Rules = """
        {
          "$schema": "./dressing-1.schema.json",
          "chipDressing": 1,
          "name": "Sample room",
          "vars": { "trim": "dev_measurewall01d", "trimHeight": 0.15 },
          "rules": {
            "baseboard": {
              "kind": "sweep",
              "note": "Along the foot of every wall face standing on the floor; mitres at corners, wraps the door jambs.",
              "faces": { "tags": ["wall"] },
              "profile": { "points": [[0, 0], [0, "$trimHeight"], [0.012, "$trimHeight"], [0.025, 0.12], [0.025, 0]] },
              "material": "$trim"
            },
            "pillar-bevel": {
              "kind": "bevel",
              "note": "Upright faces only, so just the four vertical corners round off.",
              "faces": { "tags": ["pillar"], "facing": "upright" },
              "width": 0.05,
              "segments": 3
            },
            "pipe": {
              "kind": "sweep",
              "faces": { "tags": ["plinth"], "facing": "upright" },
              "edges": "concave",
              "profile": { "round": 0.08 },
              "material": "dev_measurecrate02"
            },
            "railings": {
              "kind": "railing",
              "note": "Boxes tagged railing (object mode, Ctrl+T) become railings their size. Select one to override it.",
              "meshes": { "tags": ["railing"] },
              "material": "$trim"
            },
            "stairs": {
              "kind": "stairs",
              "note": "Ramps tagged ramp become stairs; collision stays the smooth ramp.",
              "meshes": { "tags": ["ramp"] },
              "stepHeight": 0.2
            },
            "wall-lights": {
              "kind": "repeat",
              "faces": { "tags": ["wall"], "notTags": ["platform"] },
              "prop": { "box": [0.3, 0.12, 0.08] },
              "every": 2.5,
              "offset": [0.04, 2.1],
              "material": "$trim"
            },
            "rubble": {
              "kind": "scatter",
              "faces": { "tags": ["floor"] },
              "props": [{ "box": [0.25, 0.15, 0.2] }, { "box": [0.12, 0.1, 0.12] }, { "cylinder": [0.18, 0.3] }],
              "weights": [2, 3, 1],
              "perM2": 0.25,
              "minGap": 0.5,
              "scale": [0.7, 1.4],
              "material": "$trim"
            }
          }
        }
        """;

    /// <summary>Writes room.chip, room.dressing.json and the schema into <paramref name="folder"/>; returns the level path.</summary>
    public static string Write(string folder)
    {
        Directory.CreateDirectory(folder);
        var level = Path.Combine(folder, "room.chip");
        File.WriteAllText(level, DocumentSerializer.Serialize(Build()));
        File.WriteAllText(DressingRules.DefaultPathFor(level), Rules);
        File.WriteAllText(Path.Combine(folder, DressingSchema.FileName), DressingSchema.Generate());
        return level;
    }

    public static Document Build()
    {
        var doc = new Document();
        PolyMesh Add(string name, Vector3 min, Vector3 max, string material)
        {
            var m = MeshOps.CreateBox(min, max, material, TextureMapping.Default);
            m.Name = name;
            return doc.Add(m);
        }
        var floor = Add("floor", new Vector3(0, -0.25f, 0), new Vector3(8, 0, 6), "dev_measurecrate02");
        const float h = 3, t = 0.25f;
        var walls = new[]
        {
            Add("south-a", new Vector3(0, 0, 0), new Vector3(3.25f, h, t), "dev_measurewall01a"),
            Add("south-b", new Vector3(4.75f, 0, 0), new Vector3(8, h, t), "dev_measurewall01a"),
            Add("north", new Vector3(0, 0, 6 - t), new Vector3(8, h, 6), "dev_measurewall01a"),
            Add("west", new Vector3(0, 0, t), new Vector3(t, h, 6 - t), "dev_measurewall01a"),
            Add("east", new Vector3(8 - t, 0, t), new Vector3(8, h, 6 - t), "dev_measurewall01a"),
        };
        var pillar = Add("pillar", new Vector3(5.5f, 0, 3.5f), new Vector3(6, h, 4), "dev_measurewall01a");
        var plinth = Add("plinth", new Vector3(1.25f, 0, 2.5f), new Vector3(3.25f, 0.5f, 4.5f), "dev_measurecrate02");
        // The plinth's raised block: inset the top and lift it, so its sides meet the top in four inside corners.
        int top = Enumerable.Range(0, plinth.Faces.Count).First(f => plinth.FaceNormal(f).Y > 0.9f);
        MeshOps.Inset(plinth, [top]).Apply(plinth, 0.5f, outset: false);
        var (lift, _) = MeshOps.ExtrudeFaces(plinth, [top]);
        foreach (var v in lift) plinth.Positions[v] += new Vector3(0, 0.75f, 0);
        MeshOps.AlignToGrid(plinth, Enumerable.Range(0, plinth.Faces.Count));

        // A platform in the south-east corner, a ramp up to it (stairs) and two railing boxes along its open edges.
        var platform = Add("platform", new Vector3(5.5f, 0, t), new Vector3(8 - t, 1, 2.25f), "dev_measurecrate02");
        var rampFrame = PrimitiveFrame.FromBox(new Vector3(4, 0, t), new Vector3(5.5f, 1, 1.25f), Vector3.UnitY, Vector3.UnitX);
        var ramp = doc.Add(Primitive.Build(PrimitiveShape.Wedge, new PrimitiveOptions(), rampFrame, "dev_measurecrate02", TextureMapping.Default));
        ramp.Name = "ramp";
        ramp.Tags = TagSet.Of("ramp");
        Add("rail-north", new Vector3(5.5f, 1, 2.15f), new Vector3(8 - t, 2, 2.25f), "dev_measurewall01a").Tags = TagSet.Of("railing");
        Add("rail-west", new Vector3(5.5f, 1, 1.25f), new Vector3(5.6f, 2, 2.15f), "dev_measurewall01a").Tags = TagSet.Of("railing");

        var centre = new Vector3(4, 1.5f, 3);
        for (int f = 0; f < platform.Faces.Count; f++)
            if (MathF.Abs(platform.FaceNormal(f).Y) < 0.1f) platform.Faces[f].Tags = TagSet.Of("platform", "wall");
        foreach (var w in walls)
            for (int f = 0; f < w.Faces.Count; f++)
                if (MathF.Abs(w.FaceNormal(f).Y) < 0.1f && Vector3.Dot(w.FaceNormal(f), centre - w.FaceCenter(f)) > 0)
                    w.Faces[f].Tags = TagSet.Of("wall");
        foreach (var f in pillar.Faces) f.Tags = TagSet.Of("pillar");
        for (int f = 0; f < pillar.Faces.Count; f++)
            if (MathF.Abs(pillar.FaceNormal(f).Y) < 0.1f) pillar.Faces[f].Tags = TagSet.Of("pillar", "wall");
        foreach (var f in plinth.Faces) f.Tags = TagSet.Of("plinth");
        for (int f = 0; f < floor.Faces.Count; f++)
            if (floor.FaceNormal(f).Y > 0.9f) floor.Faces[f].Tags = TagSet.Of("floor");
        return doc;
    }
}
