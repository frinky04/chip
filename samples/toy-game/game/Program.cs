using System.Numerics;
using ToyGame;

// ToyGame manifest [out.json]   write the entity manifest Chip reads (default: chip/entities.json)
// ToyGame play <level.glb>      load an exported level and report what the game sees
switch (args.FirstOrDefault())
{
    case "manifest":
    {
        var path = args.ElementAtOrDefault(1) ?? Path.Combine("chip", "entities.json");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, Manifest.Generate());
        Console.WriteLine($"Wrote {Manifest.EntityTypes().Count()} entity classes to {path}");
        return 0;
    }
    case "play" when args.Length >= 2:
    {
        var level = Level.Load(args[1]);
        foreach (var g in level.Entities.GroupBy(e => e.GetType().Name)) Console.WriteLine($"{g.Count(),3} {g.Key}");
        Console.WriteLine($"{level.Colliders.Count} colliders ({level.Colliders.Sum(c => c.Triangles.Length / 3)} triangles), {level.Triggers.Count} triggers");
        foreach (var w in level.Warnings) Console.Error.WriteLine($"warning: {w}");
        foreach (var door in level.All<Door>().Where(d => d.Partner != null))
            Console.WriteLine($"door {Label(door)} opens with {Label(door.Partner!)}");
        foreach (var screen in level.All<Screen>())
            Console.WriteLine($"screen {Label(screen)} plays {screen.Video} on {level.Surfaces.Count(s => s.Owner == screen && s.Surface == "video")} mesh(es)");
        foreach (var clip in level.Colliders.Where(c => c.Surface == "monster_clip"))
            Console.WriteLine($"monster clip {clip.Node} blocks monsters only");
        if (level.All<PlayerStart>().FirstOrDefault() is not { } start)
        {
            Console.Error.WriteLine("no player_start: place one in Chip");
            return 1;
        }
        if (level.GroundBelow(start.Position + new Vector3(0, 0.5f, 0)) is not { } ground)
        {
            Console.Error.WriteLine($"player_start at {start.Position} has no floor under it");
            return 1;
        }
        Console.WriteLine($"player spawns at {start.Position} and stands on y = {ground:0.###}");
        return 0;
    }
    default:
        Console.Error.WriteLine("usage: ToyGame manifest [out.json] | ToyGame play <level.glb>");
        return 2;
}

static string Label(GameEntity e) => e.Name.Length > 0 ? e.Name : $"#{e.Id}";
