using System.Numerics;
using Chip.Core.Geometry;
using Chip.Core.IO;
using Chip.Core.Scene;
using Chip.Core.Texturing;

namespace Chip.Core.Tests;

/// <summary>The level's texel density ("1 m is 64 px"), per-texture overrides, persistence and rescaling.</summary>
public class TexelDensityTests
{
    static MaterialLibrary Lib()
    {
        var lib = new MaterialLibrary();
        lib.Add(new MaterialInfo("wall", null, 512, 512));
        lib.Add(new MaterialInfo("brick", null, 256, 256));
        lib.SetTexelDensity("brick", 128); // its own density
        return lib;
    }

    [Fact]
    public void New_levels_use_256_px_per_metre_and_old_files_load_at_512()
    {
        Assert.Equal(256f, new Document().TexelDensity);

        var doc = new Document { TexelDensity = 100 };
        Assert.Equal(100f, DocumentSerializer.Deserialize(DocumentSerializer.Serialize(doc)).TexelDensity);

        // A file written before the setting existed: no texelDensity field.
        var json = System.Text.Json.Nodes.JsonNode.Parse(DocumentSerializer.Serialize(new Document()))!.AsObject();
        Assert.True(json.Remove("texelDensity"));
        string legacy = json.ToJsonString();
        Assert.Equal(DocumentSerializer.LegacyTexelDensity, DocumentSerializer.Deserialize(legacy).TexelDensity);
    }

    [Fact]
    public void Materials_follow_the_level_unless_they_have_their_own_density()
    {
        var lib = Lib();
        Assert.Equal(512f, lib.Get("wall").TexelsPerMeter); // library default until a level says otherwise
        lib.DefaultTexelsPerMeter = 64;
        Assert.Equal(64f, lib.Get("wall").TexelsPerMeter);
        Assert.Equal(8f, lib.Get("wall").TileWidth);       // 512 px at 64 px/m
        Assert.Equal(128f, lib.Get("brick").TexelsPerMeter);
        Assert.Equal(64f, lib.Get("unknown").TexelsPerMeter);
        Assert.Equal(1f / 64, lib.MappingFor("wall").ScaleU, 6);
    }

    [Fact]
    public void Changing_the_level_density_leaves_existing_faces_alone_and_new_ones_use_it()
    {
        var lib = Lib();
        var doc = new Document { TexelDensity = 512 };
        lib.DefaultTexelsPerMeter = 512;
        var wall = doc.Add(MeshOps.CreateBox(Vector3.Zero, Vector3.One, "wall", lib.MappingFor("wall")));
        wall.Faces[0].UVs = [new(0, 0), new(1, 0), new(1, 1), new(0, 1)];
        var session = new Session();
        session.Reset(doc, null);
        var meshes = System.Text.Json.Nodes.JsonNode.Parse(DocumentSerializer.Serialize(doc))!["meshes"]!.ToJsonString();
        var before = DocumentSerializer.Serialize(doc);

        session.Do("Texel Density", () => TexelDensityOps.Change(session.Doc, lib, 64));

        Assert.Equal(meshes, System.Text.Json.Nodes.JsonNode.Parse(DocumentSerializer.Serialize(session.Doc))!["meshes"]!.ToJsonString());
        Assert.Equal(64f, session.Doc.TexelDensity);
        Assert.Equal(64f, lib.DefaultTexelsPerMeter);
        Assert.Equal(1f / 64, lib.MappingFor("wall").ScaleU, 6);

        session.Undo();
        Assert.Equal(before, DocumentSerializer.Serialize(session.Doc));
        Assert.Equal(512f, session.Doc.TexelDensity);
    }

    [Fact]
    public void Rescaling_follows_projected_faces_only_and_undoes()
    {
        var lib = Lib();
        var doc = new Document { TexelDensity = 64 };
        lib.DefaultTexelsPerMeter = 512;
        var wall = doc.Add(MeshOps.CreateBox(Vector3.Zero, Vector3.One, "wall", lib.MappingFor("wall")));
        var brick = doc.Add(MeshOps.CreateBox(new Vector3(2, 0, 0), new Vector3(3, 1, 1), "brick", lib.MappingFor("brick")));
        wall.Faces[0].UVs = [new(0, 0), new(1, 0), new(1, 1), new(0, 1)];
        wall.Faces[2].Map.ScaleU *= 2; // a hand-tuned scale
        float brickScale = brick.Faces[1].Map.ScaleU;
        var session = new Session();
        session.Reset(doc, null);
        var before = DocumentSerializer.Serialize(doc);

        int changed = 0;
        session.Do("Rescale Faces", () => changed = TexelDensityOps.Rescale(session.Doc, lib, 512, 64));

        Assert.Equal(5, changed); // the wall's projected faces; not its explicit one, nor the brick with its own density
        var w = session.Doc.Find(wall.Id)!;
        Assert.Equal(1f / 64, w.Faces[1].Map.ScaleU, 6);                       // 8x larger texels
        Assert.Equal(2f / 64, w.Faces[2].Map.ScaleU, 6);                       // still twice that
        Assert.Equal(new Vector2(1, 1), w.Faces[0].UVs![2]);                   // explicit: untouched
        Assert.Equal(brickScale, session.Doc.Find(brick.Id)!.Faces[1].Map.ScaleU);

        session.Undo();
        Assert.Equal(before, DocumentSerializer.Serialize(session.Doc));
    }

    [Fact]
    public void Bad_densities_are_refused_and_rescaling_to_the_same_one_changes_nothing()
    {
        var lib = Lib();
        var doc = new Document();
        doc.Add(MeshOps.CreateBox(Vector3.Zero, Vector3.One, "wall", lib.MappingFor("wall")));
        Assert.Equal(0, TexelDensityOps.Rescale(doc, lib, 256, 256));
        Assert.Throws<ArgumentOutOfRangeException>(() => TexelDensityOps.Change(doc, lib, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => TexelDensityOps.Rescale(doc, lib, 0, 256));
    }
}
