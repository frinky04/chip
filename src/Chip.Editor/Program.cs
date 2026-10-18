using Chip.Editor;
using Silk.NET.Maths;
using Silk.NET.Windowing;

bool bench = args.Length > 0 && args[0] == "--bench";
bool shot = args.Length >= 3 && args[0] == "--shot";
var options = WindowOptions.Default with
{
    Size = bench ? new Vector2D<int>(1600, 900) : shot ? new Vector2D<int>((int)(1600 * (Chip.Editor.UI.Dpi.Override ?? 1)), (int)(940 * (Chip.Editor.UI.Dpi.Override ?? 1))) : new Vector2D<int>(1600, 940),
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
