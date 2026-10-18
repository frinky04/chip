using Chip.Editor;
using Silk.NET.Maths;
using Silk.NET.Windowing;

bool shot = args.Length >= 3 && args[0] == "--shot";
var options = WindowOptions.Default with
{
    Size = shot ? new Vector2D<int>((int)(1600 * (Chip.Editor.UI.Dpi.Override ?? 1)), (int)(940 * (Chip.Editor.UI.Dpi.Override ?? 1))) : new Vector2D<int>(1600, 940),
    Title = "Chip",
    VSync = true,
    API = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.ForwardCompatible, new APIVersion(3, 3)),
    IsVisible = !shot,
    WindowState = shot ? WindowState.Normal : WindowState.Maximized,
};

using var window = Window.Create(options);
if (shot)
{
    window.Initialize();
    using var snapApp = new EditorApp(window);
    snapApp.RunSnapshot(args[1] == "-" ? null : args[1], args[2], args.Length > 3 ? args[3] : "empty");
    window.Close();
    return 0;
}
EditorApp? app = null;
window.Load += () => app = new EditorApp(window);
window.Render += dt => app?.Frame((float)dt);
window.Run();
app?.Dispose();
return 0;
