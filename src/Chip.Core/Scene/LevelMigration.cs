using System.Text.Json;
using Chip.Core.IO;

namespace Chip.Core.Scene;

/// <summary>
/// Carries levels over a rename in the game: a class, property, output, input or argument stored under one of its old
/// names (<c>"was"</c>) moves to the current name. A value already under the current name wins. Running it again changes
/// nothing.
/// </summary>
public static class LevelMigration
{
    /// <summary>Applies the catalog's renames to the level. Returns how many names moved.</summary>
    public static int Apply(Document doc, EntityCatalog catalog)
    {
        int count = 0;
        if (catalog.World is { } world)
            foreach (var p in world.Props) Rename(doc.World, p.Name, p.Was);
        // Classes first, so the properties and ports below are looked up on the current class.
        foreach (var e in doc.Entities)
            if (catalog.Find(e.ClassName) == null && catalog.Classes.FirstOrDefault(c => c.Was.Contains(e.ClassName)) is { } renamed)
            {
                e.ClassName = renamed.Name;
                count++;
            }
        foreach (var e in doc.Entities)
        {
            if (catalog.Find(e.ClassName) is not { } cls) continue;
            foreach (var p in cls.Props) Rename(e.Properties, p.Name, p.Was);
            foreach (var c in e.Outputs)
            {
                if (cls.Output(c.On) == null && cls.Outputs.FirstOrDefault(o => o.Was.Contains(c.On)) is { } output)
                {
                    c.On = output.Name;
                    count++;
                }
                // The target's inputs, by its current class or one it was renamed from (it may come later in the level).
                var inputs = LogicOps.TargetsOf(doc, e, c.Target)
                    .Select(t => catalog.Find(t.ClassName) ?? catalog.Classes.FirstOrDefault(k => k.Was.Contains(t.ClassName)))
                    .OfType<EntityClass>().SelectMany(k => k.Inputs).ToList();
                if (inputs.All(i => i.Name != c.Do) && inputs.FirstOrDefault(i => i.Was.Contains(c.Do)) is { } input)
                {
                    c.Do = input.Name;
                    count++;
                }
                foreach (var p in inputs.Where(i => i.Name == c.Do).SelectMany(i => i.Params)) Rename(c.Args, p.Name, p.Was);
            }
        }
        return count;

        void Rename(Dictionary<string, JsonElement> values, string name, IReadOnlyList<string> aliases)
        {
            foreach (var old in aliases)
                if (old != name && values.Remove(old, out var value))
                {
                    values.TryAdd(name, value);
                    count++;
                }
        }
    }

    /// <summary>
    /// <c>--migrate</c>: applies the renames to the level at <paramref name="path"/>, or to every level under a game root,
    /// saving each one that changed and printing it. Exit codes: 0 done, 2 unreadable, 3 no project or manifest.
    /// </summary>
    public static int Run(string path, TextWriter output)
    {
        try
        {
            using var project = GameProject.Find(path);
            if (project == null || !File.Exists(project.Manifest)) return 3;
            var catalog = EntityCatalog.Parse(File.ReadAllText(project.Manifest), project.Manifest);
            foreach (var file in LevelCheck.Files(path))
            {
                var doc = DocumentSerializer.Load(file);
                int n = Apply(doc, catalog);
                if (n == 0) continue;
                DocumentSerializer.Save(doc, file);
                output.WriteLine($"{file}: {n} renames");
            }
            return 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            output.WriteLine(e.Message);
            return 2;
        }
    }
}
