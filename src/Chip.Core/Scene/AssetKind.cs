using System.Text.Json;

namespace Chip.Core.Scene;

/// <summary>How a kind writes a value: project-relative with extension, or relative to the kind's root without one.</summary>
public enum AssetStore { Path, Stem }

/// <summary>One file (or folder) of a kind: the value a level stores, the name shown for it, and where it is.</summary>
public sealed record AssetEntry(string Value, string Label, string FullPath);

/// <summary>
/// A kind of file an asset property names (<c>chip.project.json</c> <c>"kinds"</c>): the files under <see cref="Root"/>
/// with its extensions, or its folders. <see cref="List"/> is cached and cheap to call every frame; a file watcher drops
/// the cache when anything under the folder changes. Dispose releases the watcher.
/// </summary>
public sealed record AssetKind : IDisposable
{
    public required string Name { get; init; }
    /// <summary>The folder searched, absolute.</summary>
    public required string Root { get; init; }
    /// <summary>The project's folder, which <see cref="AssetStore.Path"/> values are relative to.</summary>
    public string ProjectRoot { get; init; } = "";
    /// <summary>Extensions that count, with the dot; empty for any.</summary>
    public IReadOnlyList<string> Extensions { get; init; } = [];
    public bool Folders { get; init; }
    public AssetStore Store { get; init; }
    /// <summary>For <c>.json</c> files: the top-level string field shown as the entry's label.</summary>
    public string? LabelField { get; init; }
    public string Doc { get; init; } = "";

    readonly object _gate = new();
    FileSystemWatcher? _watcher;
    long _revision;
    long _cachedRevision = -1;
    IReadOnlyList<AssetEntry> _entries = [];

    /// <summary>Every entry of the kind, sorted by value.</summary>
    public IReadOnlyList<AssetEntry> List()
    {
        lock (_gate)
        {
            _watcher ??= Watch();
            long revision = Interlocked.Read(ref _revision);
            if (_cachedRevision == revision) return _entries;
            var entries = new List<AssetEntry>();
            if (Directory.Exists(Root))
            {
                var paths = Folders
                    ? Directory.EnumerateDirectories(Root, "*", SearchOption.AllDirectories)
                    : Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories);
                foreach (var path in paths)
                    if (ValueFor(path) is { } value) entries.Add(new(value, LabelOf(path) ?? value, path));
            }
            _entries = entries.OrderBy(e => e.Value, StringComparer.OrdinalIgnoreCase).ToArray();
            _cachedRevision = revision;
            return _entries;
        }
    }

    /// <summary>Watches the root, or its nearest existing parent, so a root created later is noticed too.</summary>
    FileSystemWatcher Watch()
    {
        var dir = Root;
        while (!Directory.Exists(dir)) dir = Path.GetDirectoryName(dir)!;
        var watcher = new FileSystemWatcher(dir)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
        };
        watcher.Changed += Changed;
        watcher.Created += Changed;
        watcher.Deleted += Changed;
        watcher.Renamed += Changed;
        watcher.Error += (_, _) => Interlocked.Increment(ref _revision);
        watcher.EnableRaisingEvents = true;
        return watcher;
    }

    void Changed(object sender, FileSystemEventArgs e) => Interlocked.Increment(ref _revision);

    string? LabelOf(string path)
    {
        if (LabelField == null || !Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            return json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty(LabelField, out var l)
                && l.ValueKind == JsonValueKind.String ? l.GetString() : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    public bool Exists(string value) => List().Any(e => e.Value.Equals(value, StringComparison.OrdinalIgnoreCase));

    /// <summary>The value a level stores for the file or folder at <paramref name="fullPath"/>; null when it isn't one of this kind.</summary>
    public string? ValueFor(string fullPath)
    {
        var path = Path.GetFullPath(fullPath);
        var relative = Path.GetRelativePath(Root, path);
        if (relative == "." || relative == ".." || Path.IsPathRooted(relative) || relative.StartsWith(".." + Path.DirectorySeparatorChar)) return null;
        if (Folders ? !Directory.Exists(path) : !File.Exists(path)) return null;
        if (!Folders && Extensions.Count > 0 && !Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)) return null;
        var value = Store == AssetStore.Path ? Path.GetRelativePath(ProjectRoot, path) : Folders ? relative : Path.ChangeExtension(relative, null);
        return value.Replace('\\', '/');
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _watcher?.Dispose();
            _watcher = null;
        }
    }

    /// <summary>Reads <c>"kinds"</c>. Throws <see cref="InvalidDataException"/> with what's wrong.</summary>
    internal static IReadOnlyDictionary<string, AssetKind> Parse(JsonElement o, string root) => o.EnumerateObject().ToDictionary(p => p.Name, p =>
    {
        var v = p.Value;
        string? Text(string key) => v.TryGetProperty(key, out var x) ? x.GetString() : null;
        var store = Text("store") ?? "path";
        if (store is not ("path" or "stem")) throw new InvalidDataException($"Kind {p.Name}: store must be path or stem");
        return new AssetKind
        {
            Name = p.Name,
            ProjectRoot = root,
            Root = Path.GetFullPath(Path.Combine(root, Text("root") ?? throw new InvalidDataException($"Kind {p.Name} needs a root"))),
            Extensions = v.TryGetProperty("ext", out var ext) ? ext.EnumerateArray().Select(x => x.GetString()!).ToArray() : [],
            Folders = v.TryGetProperty("folders", out var f) && f.GetBoolean(),
            Store = store == "stem" ? AssetStore.Stem : AssetStore.Path,
            LabelField = Text("label"),
            Doc = Text("doc") ?? "",
        };
    });
}

public enum FlagType { Bool, Int }
public enum FlagScope { Level, Game }

/// <summary>A flag (<c>chip.project.json</c> <c>"flags"</c>): named level or game state that connections set and test.</summary>
public sealed record FlagSpec
{
    public required string Name { get; init; }
    public FlagType Type { get; init; }
    public JsonElement Default { get; init; }
    /// <summary><see cref="FlagScope.Level"/> resets when a level loads; <see cref="FlagScope.Game"/> is kept across levels and saved.</summary>
    public FlagScope Scope { get; init; }
    public string Doc { get; init; } = "";

    /// <summary>Reads <c>"flags"</c>. Throws <see cref="InvalidDataException"/> with what's wrong.</summary>
    internal static IReadOnlyDictionary<string, FlagSpec> Parse(JsonElement o) => o.EnumerateObject().ToDictionary(p => p.Name, p =>
    {
        var v = p.Value;
        var type = v.GetProperty("type").GetString();
        var scope = v.TryGetProperty("scope", out var s) ? s.GetString() : "level";
        if (type is not ("bool" or "int") || scope is not ("level" or "game")) throw new InvalidDataException($"Flag {p.Name}: invalid type or scope");
        var d = v.TryGetProperty("default", out var value) ? value.Clone() : type == "bool" ? EntityValues.Of(false) : EntityValues.Of(0L);
        bool readable = type == "bool" ? d.ValueKind is JsonValueKind.True or JsonValueKind.False : d.ValueKind == JsonValueKind.Number && d.TryGetInt32(out _);
        if (!readable) throw new InvalidDataException($"Flag {p.Name}: invalid default");
        return new FlagSpec
        {
            Name = p.Name,
            Type = type == "bool" ? FlagType.Bool : FlagType.Int,
            Scope = scope == "game" ? FlagScope.Game : FlagScope.Level,
            Default = d,
            Doc = v.TryGetProperty("doc", out var doc) ? doc.GetString()! : "",
        };
    });
}
