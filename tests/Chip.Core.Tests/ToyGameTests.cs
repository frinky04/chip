using System.Numerics;
using Chip.Core.IO;
using Chip.Core.Scene;
using ToyGame;

namespace Chip.Core.Tests;

/// <summary>
/// The engine contract end to end: <c>samples/toy-game</c> defines its entities in code, generates the manifest Chip
/// reads, and loads Chip's export with nothing but glTF and the <c>chip_*</c> extras (docs/engines.md).
/// </summary>
[Collection("Global entity classes")]
public class ToyGameTests : IDisposable
{
    static readonly string Sample = FindSample();

    static string FindSample()
    {
        for (var dir = AppContext.BaseDirectory; dir != null; dir = Path.GetDirectoryName(dir))
            if (File.Exists(Path.Combine(dir, "Chip.slnx"))) return Path.Combine(dir, "samples", "toy-game");
        throw new DirectoryNotFoundException("no Chip.slnx above the test output");
    }

    public void Dispose() => EntityClasses.Reset();

    [Fact]
    public void The_checked_in_manifest_is_what_the_game_code_generates()
    {
        // After changing samples/toy-game/game/Entities.cs: dotnet run --project samples/toy-game/game -- manifest (from samples/toy-game).
        var onDisk = File.ReadAllText(Path.Combine(Sample, "chip", "entities.json")).ReplaceLineEndings("\n");
        Assert.Equal(Manifest.Generate().ReplaceLineEndings("\n"), onDisk);
    }

    [Fact]
    public void The_sample_manifest_passes_the_check()
    {
        var output = new StringWriter();
        Assert.True(EntityManifestCheck.Run(Sample, output) == 0, output.ToString());
    }

    [Fact]
    public void The_game_loads_what_chip_exports()
    {
        var levelPath = Path.Combine(Sample, "levels", "demo.chip");
        EntityClasses.Track(levelPath);
        Assert.Equal("Toy", EntityClasses.Engine);
        var doc = DocumentSerializer.Load(levelPath);
        var export = GlbExporter.Build(doc, new MaterialLibrary(), null);
        Assert.Empty(export.Warnings);

        var level = Level.Load(export.Data);
        Assert.Empty(level.Warnings);
        Assert.Equal((1, 2, 2, 2, 1), (level.All<PlayerStart>().Count(), level.All<Pickup>().Count(), level.All<Door>().Count(),
            level.All<Light>().Count(), level.All<Screen>().Count()));

        // The screen is a mesh entity: its geometry is its own (it collides as part of it), and the face painted with the
        // game's video surface is listed for the renderer to swap.
        var screen = level.All<Screen>().Single();
        Assert.Equal(("assets/videos/lobby.webm", true), (screen.Video, screen.Loop));
        Assert.Equal(new Vector3(4.5f, 1, 0.05f), screen.Position);
        Assert.NotEmpty(screen.Colliders);
        Assert.Contains(level.Surfaces, s => s.Surface == "video" && s.Owner == screen);
        // A game surface that doesn't render: a collider the game can tell apart.
        var monsterClip = Assert.Single(level.Colliders, c => c.Surface == "monster_clip");
        Assert.False(monsterClip.Rendered);

        var doors = level.All<Door>().ToDictionary(d => d.Name);
        Assert.Same(doors["right_door"], doors["left_door"].Partner);
        Assert.Equal((DoorMode.Slide, 110f), (doors["right_door"].Mode, doors["right_door"].OpenAngle));
        Assert.Equal(["key_red"], doors["left_door"].Keys);
        Assert.Equal("assets/sounds/door.wav", doors["left_door"].Sound); // not set in the level: the class default

        var pickups = level.All<Pickup>().OrderBy(p => p.Amount).ToList();
        Assert.Equal(("key_red", 1, false), (pickups[0].Item, pickups[0].Amount, pickups[0].Respawns));
        Assert.Equal(("coin", 5, true), (pickups[1].Item, pickups[1].Amount, pickups[1].Respawns));

        var lights = level.All<Light>().OrderBy(l => l.Range).ToList();
        Assert.Equal(new Vector4(1, 0.8f, 0.6f, 1), lights[0].Color);
        Assert.Same(lights[1], Assert.Single(lights[0].Linked));

        Assert.Contains(level.Colliders, c => c.Convex && !c.Rendered); // the crate
        Assert.DoesNotContain(level.Colliders, c => c.Node.Contains("-col"));
        Assert.Single(level.Triggers);
        var start = level.All<PlayerStart>().Single();
        Assert.Equal(0, level.GroundBelow(start.Position + Vector3.UnitY)!.Value, 3);
        Assert.Equal(180, MathF.Round(2 * MathF.Acos(Math.Clamp(MathF.Abs(start.Rotation.W), 0, 1)) * 180 / MathF.PI));
    }
}
