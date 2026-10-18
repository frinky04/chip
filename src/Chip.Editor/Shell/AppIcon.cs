using Silk.NET.Core;
using Silk.NET.Windowing;
using StbImageSharp;

namespace Chip.Editor;

/// <summary>
/// Window/taskbar icon. The .exe embeds <c>chip.ico</c> for Explorer, but GLFW windows only pick up an icon resource
/// named GLFW_ICON, so the running window gets its icon from the bundled PNGs instead.
/// </summary>
static class AppIcon
{
    public static void Apply(IWindow window)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "assets", "icon");
        var images = new List<RawImage>();
        foreach (var name in new[] { "chip-32.png", "chip-256.png" })
        {
            var path = Path.Combine(dir, name);
            if (!File.Exists(path)) continue;
            try
            {
                var img = ImageResult.FromMemory(File.ReadAllBytes(path), ColorComponents.RedGreenBlueAlpha);
                images.Add(new RawImage(img.Width, img.Height, img.Data));
            }
            catch (Exception e) when (e is IOException or InvalidOperationException) { }
        }
        if (images.Count > 0) window.SetWindowIcon(images.ToArray());
    }
}
