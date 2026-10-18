using System.Text.Json;
using System.Text.Json.Nodes;
using Chip.Editor.Viewport;

namespace Chip.Editor.Features.View;

/// <summary>
/// Camera bookmarks per level, in <c>%APPDATA%/Chip/camera-bookmarks.json</c> keyed by the level's full path.
/// Untitled documents keep bookmarks in memory only. Failures never interrupt editing.
/// </summary>
public static class BookmarkStore
{
    /// <summary>Overridable for tests.</summary>
    public static string FilePath { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Chip", "camera-bookmarks.json");

    public static CameraBookmarks Load(string? levelPath)
    {
        if (levelPath == null || !File.Exists(FilePath)) return new CameraBookmarks();
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(FilePath)) as JsonObject;
            return root?[Key(levelPath)] is JsonObject entry ? CameraBookmarks.FromJson(entry.ToJsonString()) : new CameraBookmarks();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return new CameraBookmarks();
        }
    }

    public static void Save(string? levelPath, CameraBookmarks bookmarks)
    {
        if (levelPath == null) return;
        try
        {
            JsonObject root = File.Exists(FilePath) && JsonNode.Parse(File.ReadAllText(FilePath)) is JsonObject existing ? existing : new JsonObject();
            root[Key(levelPath)] = JsonNode.Parse(bookmarks.ToJson());
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            // Bookmarks are a convenience; losing one must not cost the user their edit session.
        }
    }

    static string Key(string levelPath) => Path.GetFullPath(levelPath);
}
