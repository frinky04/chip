using Chip.Core.Scene;
using Chip.Editor.Framework;

namespace Chip.Editor.Features.Materials;

/// <summary>
/// Keeps the library on the level's project textures (<see cref="MaterialFolders.Project"/>) and picks up edits to them, so
/// saving in an image editor shows in the level without a rescan. File events settle first, because editors write in steps;
/// a file still being written when the rescan comes is unreadable, and gets another few tries.
/// </summary>
public sealed class ProjectTextures : IDisposable
{
    static readonly TimeSpan Settle = TimeSpan.FromSeconds(0.3);
    static readonly TimeSpan CheckFolderEvery = TimeSpan.FromSeconds(1);
    const int UnreadableRetries = 10;

    FileSystemWatcher? _watcher;
    GameProject? _project;
    string? _levelPath;
    bool _followed;
    long _lastEvent;
    long _lastFolderCheck;
    int _pending;       // a rescan is wanted
    int _imagesChanged; // and the user should hear about it
    int _retries;

    /// <summary>The per-frame host callback.</summary>
    public void Update(EditorContext ctx)
    {
        if (!_followed || !ReferenceEquals(_project, EntityClasses.Project) || !ReferenceEquals(_levelPath, ctx.Session.Path))
        {
            _followed = true;
            _project = EntityClasses.Project;
            _levelPath = ctx.Session.Path;
            Use(ctx, MaterialFolders.Project(_project, _levelPath));
        }
        Poll(ctx);
    }

    /// <summary>Makes <paramref name="folder"/> the library's project folder, if it isn't already.</summary>
    public void Use(EditorContext ctx, string? folder)
    {
        if (!ctx.Materials.SetProjectFolder(folder)) return;
        StopWatching();
        _lastFolderCheck = 0;
        _retries = 0;
        Volatile.Write(ref _imagesChanged, 0);
        Volatile.Write(ref _pending, 0);
        Reload(ctx);
    }

    /// <summary>Starts or stops watching as the folder comes and goes, and rescans once changes settle (never mid-gesture).</summary>
    public void Poll(EditorContext ctx)
    {
        long now = Environment.TickCount64;
        if (ctx.Materials.ProjectFolder is { } folder && now - _lastFolderCheck >= CheckFolderEvery.TotalMilliseconds)
        {
            _lastFolderCheck = now;
            bool exists = Directory.Exists(folder);
            if (exists != (_watcher != null))
            {
                StopWatching();
                if (exists) _watcher = Watch(folder);
                Mark(images: true);
            }
        }
        if (Volatile.Read(ref _pending) == 0 || now - Volatile.Read(ref _lastEvent) < Settle.TotalMilliseconds || ctx.Interacting) return;
        Volatile.Write(ref _pending, 0);
        bool images = Interlocked.Exchange(ref _imagesChanged, 0) != 0;
        var scan = Reload(ctx);
        if (scan.Unreadable > 0 && _retries < UnreadableRetries)
        {
            _retries++;
            Mark(images);
            return;
        }
        _retries = 0;
        if (images)
            ctx.Status(scan.Unreadable == 0 ? "Project textures updated"
                : scan.Unreadable == 1 ? "Project textures updated; 1 image can't be read" : $"Project textures updated; {scan.Unreadable} images can't be read",
                error: scan.Unreadable > 0);
    }

    static ScanResult Reload(EditorContext ctx)
    {
        var scan = ctx.Materials.Rescan();
        MaterialsFeature.PinProjectSizes(ctx);
        MaterialsFeature.Refreshed(ctx);
        return scan;
    }

    /// <summary>
    /// Whether a change to a file of this name can matter to the library. Editors' temporary files and engines' import
    /// files can't; a name with no extension may be a folder.
    /// </summary>
    public static bool Relevant(string? name)
    {
        var extension = Path.GetExtension(name);
        return string.IsNullOrEmpty(extension) || IsImage(name) || extension.Equals(".json", StringComparison.OrdinalIgnoreCase);
    }

    static bool IsImage(string? name) => MaterialLibrary.Extensions.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase);

    FileSystemWatcher Watch(string folder)
    {
        var watcher = new FileSystemWatcher(folder)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
        };
        watcher.Changed += Changed;
        watcher.Created += Changed;
        watcher.Deleted += Changed;
        watcher.Renamed += Renamed;
        watcher.Error += (_, _) => Mark(images: true);
        watcher.EnableRaisingEvents = true;
        return watcher;
    }

    void Changed(object sender, FileSystemEventArgs e)
    {
        if (Relevant(e.Name)) Mark(!string.Equals(Path.GetExtension(e.Name), ".json", StringComparison.OrdinalIgnoreCase));
    }

    // Editors often save by renaming a temporary file over the image, so the old name doesn't decide this one.
    void Renamed(object sender, RenamedEventArgs e)
    {
        if (Relevant(e.Name) || Relevant(e.OldName)) Mark(images: IsImage(e.Name) || IsImage(e.OldName));
    }

    void Mark(bool images)
    {
        if (images) Volatile.Write(ref _imagesChanged, 1);
        Volatile.Write(ref _lastEvent, Environment.TickCount64);
        Volatile.Write(ref _pending, 1);
    }

    void StopWatching()
    {
        _watcher?.Dispose();
        _watcher = null;
    }

    public void Dispose() => StopWatching();
}
