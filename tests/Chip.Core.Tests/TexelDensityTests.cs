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
    public void Changing_the_level_density_rescales_following_faces_and_undoes()
    {
        var lib = Lib();
        var doc = new Document { TexelDensity = 512 };
        lib.DefaultTexelsPerMeter = 512;
        var wall = doc.Add(MeshOps.CreateBox(Vector3.Zero, Vector3.One, "wall", lib.MappingFor("wall")));
        var brick = doc.Add(MeshOps.CreateBox(new Vector3(2, 0, 0), new Vector3(3, 1, 1), "brick", lib.MappingFor("brick")));
        wall.Faces[0].UVs = [new(0, 0), new(1, 0), new(1, 1), new(0, 1)];
        float brickScale = brick.Faces[1].Map.ScaleU;
        var session = new Session();
        session.Reset(doc, null);
        var before = DocumentSerializer.Serialize(doc);

        int changed = 0;
        session.Do("Texel Density", () => changed = TexelDensityOps.Change(session.Doc, lib, 64));

        Assert.Equal(6, changed); // the wall's faces; the brick has its own density
        var w = session.Doc.Find(wall.Id)!;
        Assert.Equal(1f / 64, w.Faces[1].Map.ScaleU, 6);                       // projected: 8x larger texels
        Assert.Equal(new Vector2(0.125f, 0.125f), w.Faces[0].UVs![2]);          // explicit: 1/8 of a repeat per metre
        Assert.Equal(brickScale, session.Doc.Find(brick.Id)!.Faces[1].Map.ScaleU);
        Assert.Equal(64f, session.Doc.TexelDensity);
        Assert.Equal(64f, lib.DefaultTexelsPerMeter);

        session.Undo();
        Assert.Equal(before, DocumentSerializer.Serialize(session.Doc));
        Assert.Equal(512f, session.Doc.TexelDensity);
    }

    [Fact]
    public void Setting_the_same_density_changes_nothing()
    {
        var lib = Lib();
        var doc = new Document();
        doc.Add(MeshOps.CreateBox(Vector3.Zero, Vector3.One, "wall", lib.MappingFor("wall")));
        Assert.Equal(0, TexelDensityOps.Change(doc, lib, doc.TexelDensity));
        Assert.Throws<ArgumentOutOfRangeException>(() => TexelDensityOps.Change(doc, lib, 0));
    }
}
