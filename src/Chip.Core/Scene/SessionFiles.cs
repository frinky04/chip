namespace Chip.Core.Scene;

/// <summary>A text file one undo step rewrote (dressing rules): its text before and after (null: the file didn't exist).</summary>
public sealed record FileChange(string Path, string? Before, string? After);

/// <summary>Where <see cref="Session.WriteFile"/> reads and writes files. The editor uses disk; tests use memory.</summary>
public interface ISessionFiles
{
    /// <summary>The file's text, or null if it doesn't exist.</summary>
    string? Read(string path);

    /// <summary>Writes the text, creating folders as needed; null deletes the file.</summary>
    void Write(string path, string? text);
}

/// <summary>Files on disk.</summary>
public sealed class DiskSessionFiles : ISessionFiles
{
    public static readonly DiskSessionFiles Instance = new();

    public string? Read(string path) => File.Exists(path) ? File.ReadAllText(path) : null;

    public void Write(string path, string? text)
    {
        if (text == null)
        {
            if (File.Exists(path)) File.Delete(path);
            return;
        }
        if (Path.GetDirectoryName(path) is { Length: > 0 } dir) Directory.CreateDirectory(dir);
        File.WriteAllText(path, text);
    }
}
