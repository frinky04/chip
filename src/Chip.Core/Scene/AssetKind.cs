using System.Text.Json;

namespace Chip.Core.Scene;

public enum AssetStore { Path, Stem }
public sealed record AssetEntry(string Value, string Label, string FullPath);

/// <summary>A recursively indexed project asset kind. Dispose releases its filesystem watcher.</summary>
public sealed record AssetKind : IDisposable
{
    public required string Name { get; init; }
    public required string Root { get; init; }
    public string ProjectRoot { get; init; } = "";
    public IReadOnlyList<string> Extensions { get; init; } = [];
    public bool Folders { get; init; }
    public AssetStore Store { get; init; }
    public string? LabelField { get; init; }
    public string Doc { get; init; } = "";
    readonly object _gate = new();
    FileSystemWatcher? _watcher;
    long _revision;
    long _cachedRevision = -1;
    IReadOnlyList<AssetEntry> _entries = [];

    public IReadOnlyList<AssetEntry> List()
    {
        lock (_gate)
        {
            // Watch the nearest existing ancestor, including creation of a missing asset root.
            if (_watcher == null)
            {
                var dir = Root;
                while (!Directory.Exists(dir)) dir = Path.GetDirectoryName(dir)!;
                _watcher = new FileSystemWatcher(dir) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size };
                _watcher.Changed += Changed; _watcher.Created += Changed; _watcher.Deleted += Changed; _watcher.Renamed += Changed;
                _watcher.Error += (_, _) => Interlocked.Increment(ref _revision);
                _watcher.EnableRaisingEvents = true;
            }
            long revision = Interlocked.Read(ref _revision);
            if (_cachedRevision == revision) return _entries;
            var entries = new List<AssetEntry>();
            if (Directory.Exists(Root))
                foreach (var path in Folders ? Directory.EnumerateDirectories(Root, "*", SearchOption.AllDirectories) : Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
                {
                    if (ValueFor(path) is not { } value) continue;
                    string label = value;
                    if (LabelField != null && Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase))
                        try { using var json = JsonDocument.Parse(File.ReadAllText(path)); if (json.RootElement.TryGetProperty(LabelField, out var l) && l.ValueKind == JsonValueKind.String) label = l.GetString()!; }
                        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
                    entries.Add(new(value, label, path));
                }
            _entries = entries.OrderBy(e => e.Value, StringComparer.OrdinalIgnoreCase).ToArray();
            _cachedRevision = revision;
            return _entries;
        }
    }
    void Changed(object sender, FileSystemEventArgs e) => Interlocked.Increment(ref _revision);
    public bool Exists(string value) => List().Any(e => e.Value.Equals(value, StringComparison.OrdinalIgnoreCase));
    public string? ValueFor(string fullPath)
    {
        var path = Path.GetFullPath(fullPath);
        var relative = Path.GetRelativePath(Root, path);
        if (relative == "." || Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar)) return null;
        if (Folders ? !Directory.Exists(path) : !File.Exists(path) || Extensions.Count > 0 && !Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)) return null;
        var value = Store == AssetStore.Path ? Path.GetRelativePath(ProjectRoot, path) : Folders ? relative : Path.ChangeExtension(relative, null);
        return value.Replace('\\', '/');
    }
    public void Dispose() { lock (_gate) { _watcher?.Dispose(); _watcher = null; } }
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
            Doc = Text("doc") ?? ""
        };
    });
}

public enum FlagType { Bool, Int }
public enum FlagScope { Level, Game }
public sealed record FlagSpec
{
    public required string Name { get; init; }
    public FlagType Type { get; init; }
    public JsonElement Default { get; init; }
    public FlagScope Scope { get; init; }
    public string Doc { get; init; } = "";
    internal static IReadOnlyDictionary<string, FlagSpec> Parse(JsonElement o) => o.EnumerateObject().ToDictionary(p => p.Name, p =>
    {
        var v = p.Value;
        var type = v.GetProperty("type").GetString();
        var scope = v.TryGetProperty("scope", out var s) ? s.GetString() : "level";
        if (type is not ("bool" or "int") || scope is not ("level" or "game")) throw new InvalidDataException($"Flag {p.Name}: invalid type or scope");
        var d = v.TryGetProperty("default", out var value) ? value.Clone() : type == "bool" ? EntityValues.Of(false) : EntityValues.Of(0L);
        if (type == "bool" ? d.ValueKind is not (JsonValueKind.True or JsonValueKind.False) : d.ValueKind != JsonValueKind.Number || !d.TryGetInt32(out _)) throw new InvalidDataException($"Flag {p.Name}: invalid default");
        return new FlagSpec { Name = p.Name, Type = type == "bool" ? FlagType.Bool : FlagType.Int, Scope = scope == "game" ? FlagScope.Game : FlagScope.Level, Default = d, Doc = v.TryGetProperty("doc", out var doc) ? doc.GetString()! : "" };
    });
}
