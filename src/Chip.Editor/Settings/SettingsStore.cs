using System.Text.Json;
using Chip.Core.IO;

namespace Chip.Editor.Settings;

/// <summary>
/// Loads and saves <see cref="AppSettings"/>. A missing or unreadable file yields defaults (the bad file is kept as
/// <c>settings.json.bad</c> so nothing is silently lost); saves are atomic. A store with no folder lives in memory only,
/// which is what tests and headless captures use.
/// </summary>
public sealed class SettingsStore
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary><c>%APPDATA%/Chip</c>, or <c>CHIP_SETTINGS_DIR</c> when set.</summary>
    public static string DefaultFolder =>
        Environment.GetEnvironmentVariable("CHIP_SETTINGS_DIR") is { Length: > 0 } dir ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Chip");

    /// <summary>Folder holding settings.json and autosave/, or null for an in-memory store.</summary>
    public string? Folder { get; }
    public string? FilePath => Folder == null ? null : Path.Combine(Folder, "settings.json");
    public AppSettings Current { get; private set; }
    /// <summary>Why the last load fell back to defaults, if it did.</summary>
    public string? LoadError { get; private set; }

    public SettingsStore(string? folder)
    {
        Folder = folder;
        Current = Load();
    }

    public static SettingsStore InMemory() => new(null);

    AppSettings Load()
    {
        if (FilePath is not { } path || !File.Exists(path)) return new AppSettings();
        try
        {
            return (JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json) ?? new AppSettings()).Normalize();
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            LoadError = $"Settings couldn't be read ({e.Message}), so defaults are in use. The old file is saved as settings.json.bad.";
            try { File.Copy(path, path + ".bad", overwrite: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            return new AppSettings();
        }
    }

    /// <summary>Writes the current settings. Returns an error message, or null on success (always null in memory).</summary>
    public string? Save()
    {
        if (FilePath is not { } path) return null;
        try
        {
            Directory.CreateDirectory(Folder!);
            AtomicFile.Write(path, JsonSerializer.SerializeToUtf8Bytes(Current.Normalize(), Json));
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"Couldn't save settings to {path}: {e.Message}";
        }
    }
}
