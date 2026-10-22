using Chip.Editor;
using Silk.NET.Maths;
using Silk.NET.Windowing;

// Headless dressing tools (no window): docs/dressing.md.
if (args.Length >= 2 && args[0] == "--rules-check")
{
    try
    {
        var level = Chip.Core.IO.DocumentSerializer.Load(args[1]);
        return Chip.Core.Dressing.RulesCheck.Run(level, Path.GetFullPath(args[1]), Chip.Core.Dressing.DiskFiles.Instance, Console.Out, json: args.Contains("--json"));
    }
    catch (Exception e) when (e is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine($"can't read {args[1]}: {e.Message}");
        return 4;
    }
}
// Headless export: Chip --export level.chip [out.glb] (default: the level's export path). Entity classes come from the
// Godot project the output or the level is in, as in the editor. Dressing rules aren't applied.
if (args.Length >= 2 && args[0] == "--export")
{
    try
    {
        var levelPath = Path.GetFullPath(args[1]);
        var level = Chip.Core.IO.DocumentSerializer.Load(levelPath);
        string? output = args.Length >= 3 ? Path.GetFullPath(args[2])
            : level.ExportPath is { Length: > 0 } p ? Path.GetFullPath(Path.Combine(Path.GetDirectoryName(levelPath)!, p)) : null;
        if (output == null)
        {
            Console.Error.WriteLine($"{args[1]} has no export path; pass one: --export {args[1]} out.glb");
            return 2;
        }
        Chip.Core.Scene.EntityClasses.Track(output, levelPath);
        if (Chip.Core.Scene.EntityClasses.Problem is { } problem) Console.Error.WriteLine(problem);
        var materials = Chip.Core.Scene.MaterialLibrary.LoadFolder(Path.Combine(AppContext.BaseDirectory, "assets", "textures"));
        var r = Chip.Core.IO.GlbExporter.Export(level, materials, output);
        foreach (var w in r.Warnings) Console.Error.WriteLine(w);
        Console.WriteLine($"Exported {r.Meshes} meshes and {r.Entities} entities to {output}");
        return 0;
    }
    catch (Exception e) when (e is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine($"can't export {args[1]}: {e.Message}");
        return 4;
    }
}
if (args.Length >= 1 && args[0] == "--rules-schema")
{
    var schema = Chip.Core.Dressing.DressingSchema.Generate();
    if (args.Length >= 2) File.WriteAllText(args[1], schema);
    else Console.Out.Write(schema);
    return 0;
}

bool bench = args.Length > 0 && args[0] == "--bench";
bool shot = args.Length >= 3 && args[0] == "--shot";
var options = WindowOptions.Default with
{
    // Bench and shots scale the backbuffer with CHIP_UI_SCALE, so every scale lays out the same panels (the dock, say).
    Size = bench ? new Vector2D<int>((int)(1600 * (Chip.Editor.UI.Dpi.Override ?? 1)), (int)(900 * (Chip.Editor.UI.Dpi.Override ?? 1)))
        : shot ? new Vector2D<int>((int)(1600 * (Chip.Editor.UI.Dpi.Override ?? 1)), (int)(940 * (Chip.Editor.UI.Dpi.Override ?? 1))) : new Vector2D<int>(1600, 940),
    Title = "Chip",
    VSync = !bench,
    API = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.ForwardCompatible, new APIVersion(3, 3)),
    IsVisible = !shot && !bench,
    WindowState = shot || bench ? WindowState.Normal : WindowState.Maximized,
};

using var window = Window.Create(options);
if (bench)
{
    window.Initialize();
    using var benchApp = new EditorApp(window, persistent: false);
    int code = benchApp.RunBenchmark(args.ElementAtOrDefault(1) ?? "stress", args.ElementAtOrDefault(2));
    window.Close();
    return code;
}
if (shot)
{
    window.Initialize();
    using var snapApp = new EditorApp(window, persistent: false); // captures never read or write user settings
    var rest = args.Skip(3).ToList();
    int runAt = rest.IndexOf("--run");
    var commands = runAt >= 0 && runAt + 1 < rest.Count ? rest[runAt + 1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : [];
    if (runAt >= 0) rest.RemoveRange(runAt, Math.Min(2, rest.Count - runAt));
    int code = snapApp.RunSnapshot(args[1] == "-" ? null : args[1], args[2], rest.FirstOrDefault() ?? "empty", commands);
    window.Close();
    return code;
}
EditorApp? app = null;
window.Load += () => app = new EditorApp(window);
window.Render += dt => app?.Frame((float)dt);
window.Run();
app?.Dispose();
return 0;
