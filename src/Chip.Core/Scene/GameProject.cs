using System.Text.Json;

namespace Chip.Core.Scene;

/// <summary>
/// The game a level belongs to, and the engine conventions its export follows. Found by walking up from a level or its
/// export path to the first folder holding <c>chip.project.json</c> or <c>project.godot</c> (the project file wins when a
/// folder has both; its missing fields then take Godot's values). See <c>docs/engines.md</c>.
/// </summary>
public sealed record GameProject
{
    public const string FileName = "chip.project.json";
    public const int Version = 1;

    /// <summary>The folder holding the project file (or <c>project.godot</c>).</summary>
    public required string Root { get; init; }
    /// <summary>The project file, or null for a Godot project without one.</summary>
    public string? File { get; init; }
    /// <summary>The engine's name in Chip's messages ("Set in Godot").</summary>
    public string Engine { get; init; } = "the engine";
    /// <summary>The entity manifest, absolute.</summary>
    public required string Manifest { get; init; }
    /// <summary>Collision nodes get Godot's import suffixes (<c>-col</c>, <c>-colonly</c>, <c>-convcolonly</c>) as well as extras.</summary>
    public bool CollisionSuffixes { get; init; }
    /// <summary>What engine asset paths start with (<c>res://</c>), shown as a hint and checked; empty for no rule.</summary>
    public string AssetPrefix { get; init; } = "";
    public bool IsGodot { get; init; }

    /// <summary>The manifest when the project file doesn't name one (where the Godot addon writes it), relative to the project folder.</summary>
    public static readonly string DefaultManifest = Path.Combine(".chip", "entities.json");

    /// <summary>The project file or manifest path relative to <see cref="Root"/>, for messages.</summary>
    public string Relative(string path) => Path.GetRelativePath(Root, path);

    public static GameProject Godot(string root) => new()
    {
        Root = root, Engine = "Godot", Manifest = Path.Combine(root, DefaultManifest), CollisionSuffixes = true,
        AssetPrefix = "res://", IsGodot = true,
    };

    /// <summary>
    /// The project at or above <paramref name="path"/> (a file or folder), or null. A project file that can't be read
    /// still returns its folder, with <paramref name="problem"/> saying why and every other field at its default.
    /// </summary>
    public static GameProject? Find(string? path, out string? problem)
    {
        problem = null;
        if (FindRoot(path) is not { } root) return null;
        var file = Path.Combine(root, FileName);
        bool godot = System.IO.File.Exists(Path.Combine(root, "project.godot"));
        if (!System.IO.File.Exists(file)) return Godot(root);
        try { return Parse(System.IO.File.ReadAllText(file), file, godot); }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            problem = $"Can't read {Path.Combine(Path.GetFileName(root), FileName)}: {e.Message}";
            var fallback = godot ? Godot(root) : Defaults(root);
            return fallback with { File = file };
        }
    }

    public static GameProject? Find(string? path) => Find(path, out _);

    /// <summary>The folder holding <c>chip.project.json</c> or <c>project.godot</c> at or above <paramref name="path"/>, or null.</summary>
    public static string? FindRoot(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var dir = Path.GetFullPath(path);
            if (!Directory.Exists(dir)) dir = Path.GetDirectoryName(dir);
            for (; dir != null; dir = Path.GetDirectoryName(dir))
                if (System.IO.File.Exists(Path.Combine(dir, FileName)) || System.IO.File.Exists(Path.Combine(dir, "project.godot"))) return dir;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        return null;
    }

    static GameProject Defaults(string root) => new() { Root = root, Manifest = Path.Combine(root, DefaultManifest) };

    /// <summary>Reads a project file. Throws <see cref="InvalidDataException"/> with what's wrong.</summary>
    public static GameProject Parse(string json, string file, bool godot = false)
    {
        var root = Path.GetDirectoryName(Path.GetFullPath(file))!;
        var p = (godot ? Godot(root) : Defaults(root)) with { File = Path.GetFullPath(file) };
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }); }
        catch (JsonException e) { throw new InvalidDataException($"isn't valid JSON: {e.Message}"); }
        using (doc)
        {
            var o = doc.RootElement;
            if (o.ValueKind != JsonValueKind.Object) throw new InvalidDataException("isn't a JSON object");
            if (o.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.Number && v.GetInt32() > Version)
                throw new InvalidDataException($"is version {v.GetInt32()}, newer than this Chip reads ({Version}). Update Chip");
            foreach (var prop in o.EnumerateObject())
                switch (prop.Name)
                {
                    case "version": break;
                    case "engine": p = p with { Engine = Text(prop) }; break;
                    case "entities": p = p with { Manifest = Path.GetFullPath(Path.Combine(root, Text(prop))) }; break;
                    case "assetPrefix": p = p with { AssetPrefix = prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString()! : throw Bad(prop, "text") }; break;
                    case "collisionSuffixes":
                        p = p with { CollisionSuffixes = prop.Value.ValueKind switch
                        {
                            JsonValueKind.True => true, JsonValueKind.False => false, _ => throw Bad(prop, "true or false"),
                        } };
                        break;
                    default: throw new InvalidDataException($"has an unknown field \"{prop.Name}\" (known: version, engine, entities, collisionSuffixes, assetPrefix)");
                }
            return p;
        }

        static string Text(JsonProperty prop) =>
            prop.Value.ValueKind == JsonValueKind.String && prop.Value.GetString() is { Length: > 0 } s ? s : throw Bad(prop, "non-empty text");

        static InvalidDataException Bad(JsonProperty prop, string want) => new($"\"{prop.Name}\" should be {want}");
    }
}
