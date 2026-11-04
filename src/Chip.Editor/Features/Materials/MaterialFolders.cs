using Chip.Core.Scene;
using Chip.Editor.Settings;

namespace Chip.Editor.Features.Materials;

/// <summary>
/// The material library's folders, in the order names resolve (the first folder holding a name wins). The editor and
/// headless commands both build from this list, so one document exports the same materials from either.
/// </summary>
public static class MaterialFolders
{
    public static string Textures => Path.Combine(AppContext.BaseDirectory, "assets", "textures");

    /// <param name="settings">The user's folders (Preferences > Paths).</param>
    /// <param name="environment">Stands in for <see cref="MaterialLibrary.EnvVar"/> (tests); null reads it.</param>
    public static IReadOnlyList<string> All(IEnumerable<string> settings, string? environment = null) =>
    [
        Textures,
        Path.Combine(AppContext.BaseDirectory, "assets", "hotspots"),
        Path.Combine(AppContext.BaseDirectory, "assets", "tools"),
        .. settings,
        .. MaterialLibrary.EnvironmentFolders(environment),
    ];

    /// <summary>Adds every folder and scans them. Folders the library already has keep their place.</summary>
    public static void AddTo(MaterialLibrary lib, IEnumerable<string> settings, string? environment = null)
    {
        foreach (var folder in All(settings, environment)) lib.AddFolder(folder);
        lib.Rescan();
    }

    /// <summary>The user's folders that aren't there. A bundled folder may be absent from a build, which is no one's mistake.</summary>
    public static IReadOnlyList<string> Missing(MaterialLibrary lib) =>
        lib.Folders.Where(f => !Directory.Exists(f) && !f.StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>
    /// The library as the editor has it once running, for a command with no window: every folder from the saved settings,
    /// plus the current project's surfaces (call <see cref="EntityClasses.Track"/> first). Problems go to <paramref name="problems"/>.
    /// </summary>
    public static MaterialLibrary LoadHeadless(SettingsStore settings, TextWriter problems, string? environment = null)
    {
        if (settings.LoadError != null) problems.WriteLine(settings.LoadError);
        var lib = MaterialLibrary.LoadFolder(Textures);
        AddTo(lib, settings.Current.TextureFolders, environment);
        new SurfaceMaterials().Sync(lib);
        if (Missing(lib) is { Count: > 0 } missing)
            problems.WriteLine($"Texture folder not found: {string.Join(", ", missing)}. Fix Preferences > Paths or {MaterialLibrary.EnvVar}");
        return lib;
    }
}
