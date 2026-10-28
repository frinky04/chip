using System.Text.Json;
using Chip.Core.IO;

namespace Chip.Core.Scene;

/// <summary><c>--check-levels</c>: every level of a game checked against its classes, kinds and flags (<see cref="LogicOps.Check"/>).</summary>
public static class LevelCheck
{
    /// <summary>The level at <paramref name="path"/>, or every <c>.chip</c> under it, skipping build output and dot folders.</summary>
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

    /// <summary>
    /// Prints each level's problems (or, with <paramref name="json"/>, <c>{ "levels": [ { path, problems } ] }</c>). Levels
    /// load with renames applied, as the editor opens them. Exit codes: 0 clean, 1 warnings only, 2 errors, 3 no project
    /// or manifest.
    /// </summary>
    public static int Run(string path, TextWriter output, bool json = false)
    {
        var levels = new List<object>();
        int code = 0;
        try
        {
            using var project = GameProject.Find(path, out var problem);
            if (project == null || !File.Exists(project.Manifest))
            {
                output.WriteLine(json ? """{"levels":[]}""" : "No project or entity manifest found");
                return 3;
            }
            if (problem != null) { output.WriteLine(problem); return 2; }
            var catalog = EntityCatalog.Parse(File.ReadAllText(project.Manifest), project.Manifest);
            foreach (var file in Files(path))
            {
                var problems = CheckFile(file, catalog, project);
                code = Math.Max(code, problems.Any(p => p.Severity == Severity.Error) ? 2 : problems.Count > 0 ? 1 : 0);
                levels.Add(new
                {
                    path = file,
                    problems = problems.Select(p => new { severity = Name(p.Severity), entity = p.Entity, message = p.Message }),
                });
                if (!json)
                    foreach (var p in problems) output.WriteLine($"{file}: {Name(p.Severity)}: {p.Message}");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            if (!json) output.WriteLine(e.Message);
            code = 2;
        }
        if (json) output.WriteLine(JsonSerializer.Serialize(new { levels }, new JsonSerializerOptions { WriteIndented = true }));
        return code;
    }

    static IReadOnlyList<LevelProblem> CheckFile(string file, EntityCatalog catalog, GameProject project)
    {
        try
        {
            var doc = DocumentSerializer.Load(file);
            LevelMigration.Apply(doc, catalog);
            return LogicOps.Check(doc, catalog, project);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or JsonException)
        {
            return [new(Severity.Error, null, e.Message)];
        }
    }

    static string Name(Severity s) => s.ToString().ToLowerInvariant();
}
