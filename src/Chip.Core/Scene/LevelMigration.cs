using System.Text.Json;
using Chip.Core.IO;
namespace Chip.Core.Scene;

public static class LevelMigration
{
    public static int Apply(Document doc, EntityCatalog catalog)
    {
        int count = 0;
        foreach (var e in doc.Entities)
            if (catalog.Find(e.ClassName) == null && catalog.Classes.FirstOrDefault(c => c.Was.Contains(e.ClassName)) is { } renamed) { e.ClassName = renamed.Name; count++; }
        foreach (var e in doc.Entities)
        {
            if (catalog.Find(e.ClassName) is not { } cls) continue;
            foreach (var p in cls.Props) Rename(e.Properties, p.Name, p.Was);
            foreach (var c in e.Outputs)
            {
                if (cls.Output(c.On) == null && cls.Outputs.FirstOrDefault(o => o.Was.Contains(c.On)) is { } output) { c.On = output.Name; count++; }
                var inputs = LogicOps.TargetsOf(doc, e, c.Target).Select(t => catalog.Find(t.ClassName) ?? catalog.Classes.FirstOrDefault(k => k.Was.Contains(t.ClassName))).Where(k => k != null).SelectMany(k => k!.Inputs).ToList();
                if (inputs.All(i => i.Name != c.Do) && inputs.FirstOrDefault(i => i.Was.Contains(c.Do)) is { } input) { c.Do = input.Name; count++; }
                foreach (var p in inputs.Where(i => i.Name == c.Do).SelectMany(i => i.Params)) Rename(c.Args, p.Name, p.Was);
            }
        }
        return count;
        void Rename(Dictionary<string, JsonElement> values, string name, IReadOnlyList<string> aliases)
        {
            foreach (var old in aliases)
                if (old != name && values.Remove(old, out var value)) { values.TryAdd(name, value); count++; }
        }
    }
    public static int Run(string path, TextWriter output)
    {
        try
        {
            using var p = GameProject.Find(path);
            if (p == null || !File.Exists(p.Manifest)) return 3;
            var catalog = EntityCatalog.Parse(File.ReadAllText(p.Manifest), p.Manifest);
            foreach (var file in LevelCheck.Files(path)) { var doc = DocumentSerializer.Load(file); int n = Apply(doc, catalog); if (n == 0) continue; DocumentSerializer.Save(doc, file); output.WriteLine($"{file}: {n} renames"); }
            return 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or JsonException) { output.WriteLine(e.Message); return 2; }
    }
}
