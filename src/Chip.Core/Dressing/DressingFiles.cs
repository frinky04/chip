namespace Chip.Core.Dressing;

/// <summary>
/// Where dressing reads rules files from. Paths are as written in the files (relative to the importing file, forward or
/// back slashes). <see cref="Stamp"/> changes whenever a file does, so the dresser reloads edited rules (live reload).
/// </summary>
public interface IDressingFiles
{
    /// <summary>The file's text, or null if it doesn't exist or can't be read.</summary>
    string? ReadText(string path);

    /// <summary>The file's bytes (props: <c>.glb</c> / <c>.gltf</c>), or null if it doesn't exist or can't be read.</summary>
    byte[]? ReadBytes(string path);

    /// <summary>A number that changes when the file changes (0 when it doesn't exist).</summary>
    long Stamp(string path);
}

/// <summary>Rules files on disk. Stamps are last-write times.</summary>
public sealed class DiskFiles : IDressingFiles
{
    public static readonly DiskFiles Instance = new();

    public string? ReadText(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : null; }
        catch (IOException) { return null; }      // mid-save by another program: the next stamp change retries
        catch (UnauthorizedAccessException) { return null; }
    }

    public byte[]? ReadBytes(string path)
    {
        try { return File.Exists(path) ? File.ReadAllBytes(path) : null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public long Stamp(string path)
    {
        try { return File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks ^ new FileInfo(path).Length : 0; }
        catch (IOException) { return 0; }
    }
}

/// <summary>
/// Rules files in memory, for tests and headless tools. Setting a file bumps its stamp. Also a session's file store, so
/// undoable rules edits in tests land where the dresser reads.
/// </summary>
public sealed class MemoryFiles : IDressingFiles, Scene.ISessionFiles
{
    string? Scene.ISessionFiles.Read(string path) => ReadText(path);
    void Scene.ISessionFiles.Write(string path, string? text) => this[path] = text;

    readonly Dictionary<string, (string Text, long Stamp)> _files = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, byte[]> _bytes = new(StringComparer.OrdinalIgnoreCase);
    long _next;

    public string? this[string path]
    {
        get => ReadText(path);
        set
        {
            if (value == null) _files.Remove(Key(path));
            else _files[Key(path)] = (value, ++_next);
        }
    }

    /// <summary>Stores a binary file (a prop); its stamp changes like a text file's.</summary>
    public void SetBytes(string path, byte[] data)
    {
        _bytes[Key(path)] = data;
        _files[Key(path)] = ("", ++_next);
    }

    public string? ReadText(string path) => _files.TryGetValue(Key(path), out var f) ? f.Text : null;
    public byte[]? ReadBytes(string path) =>
        _bytes.TryGetValue(Key(path), out var b) ? b : ReadText(path) is { } t ? System.Text.Encoding.UTF8.GetBytes(t) : null;
    public long Stamp(string path) => _files.TryGetValue(Key(path), out var f) ? f.Stamp : 0;

    static string Key(string path) => RulePaths.Normalize(path);
}

/// <summary>Path handling shared by the loader and the file adapters: forward slashes, <c>.</c> and <c>..</c> collapsed.</summary>
public static class RulePaths
{
    public static string Normalize(string path)
    {
        var parts = new List<string>();
        var segments = path.Replace('\\', '/').Split('/');
        for (int i = 0; i < segments.Length; i++)
        {
            var s = segments[i];
            if (s == "." || (s.Length == 0 && i > 0)) continue;
            if (s == ".." && parts.Count > 0 && parts[^1] != ".." && parts[^1].Length > 0 && !parts[^1].EndsWith(':')) parts.RemoveAt(parts.Count - 1);
            else parts.Add(s);
        }
        return string.Join('/', parts);
    }

    /// <summary><paramref name="relative"/> as written in <paramref name="fromFile"/>, resolved against that file's folder.</summary>
    public static string Resolve(string fromFile, string relative)
    {
        if (Path.IsPathRooted(relative)) return Normalize(relative);
        int slash = Normalize(fromFile).LastIndexOf('/');
        return Normalize(slash < 0 ? relative : Normalize(fromFile)[..(slash + 1)] + relative);
    }
}
