using System.Text.Json;
using Chip.Core.IO;

namespace Chip.Core.Scene;

public static class LevelCheck
{
    internal static IEnumerable<string> Files(string path)
    {
        if (File.Exists(path)) { yield return Path.GetFullPath(path); yield break; }
        foreach (var file in Directory.EnumerateFiles(path, "*.chip")) yield return Path.GetFullPath(file);
        foreach (var dir in Directory.EnumerateDirectories(path))
        {
            var name = Path.GetFileName(dir);
            if (name.StartsWith('.') || name is "bin" or "obj" or "artifacts" or "dist") continue;
            foreach (var file in Files(dir)) yield return file;
        }
    }
    public static int Run(string path, TextWriter output, bool json = false)
    {
        var levels = new List<object>(); int code = 0;
        try
        {
            using var p = GameProject.Find(path, out var problem);
            if (p == null || !File.Exists(p.Manifest)) { if (json) output.WriteLine("{\"levels\":[]}"); else output.WriteLine("No project or entity manifest found"); return 3; }
            if (problem != null) { output.WriteLine(problem); return 2; }
            var catalog = EntityCatalog.Parse(File.ReadAllText(p.Manifest), p.Manifest);
            foreach (var file in Files(path))
            {
                IReadOnlyList<LevelProblem> problems;
                try { var doc = DocumentSerializer.Load(file); LevelMigration.Apply(doc, catalog); problems = LogicOps.Check(doc, catalog, p); }
                catch (Exception e) when (e is IOException or InvalidDataException or JsonException) { problems = [new(Severity.Error, null, e.Message)]; }
                code = Math.Max(code, problems.Any(p => p.Severity == Severity.Error) ? 2 : problems.Count > 0 ? 1 : 0);
                levels.Add(new { path = file, problems = problems.Select(p => new { severity = p.Severity.ToString().ToLowerInvariant(), entity = p.Entity, message = p.Message }) });
                if (!json) foreach (var issue in problems) output.WriteLine($"{file}: {issue.Severity.ToString().ToLowerInvariant()}: {issue.Message}");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or JsonException) { if (!json) output.WriteLine(e.Message); code = 2; }
        if (json) output.WriteLine(JsonSerializer.Serialize(new { levels }, new JsonSerializerOptions { WriteIndented = true }));
        return code;
    }
}
