using System.Numerics;
using Chip.Core.Geometry;
using Chip.Core.IO;

namespace Chip.Core.Scene;

/// <summary>
/// A reference model loaded for drawing and picking: the file's meshes merged into one, in model space (Y up, origin where
/// the file has it), with its bounds and triangles.
/// </summary>
public sealed class ReferenceModel(PolyMesh mesh, Vector3 min, Vector3 max, Vector3[] triangles)
{
    public PolyMesh Mesh { get; } = mesh;
    public Vector3 Min { get; } = min;
    public Vector3 Max { get; } = max;
    public Vector3 Size => Max - Min;
    /// <summary>Three corners per triangle.</summary>
    public Vector3[] Triangles { get; } = triangles;
}

/// <summary>
/// Reference models (class <c>ref_model</c>): a <c>.glb</c>/<c>.gltf</c> drawn at an entity so a door, a prop or a person can
/// be fitted and judged for size inside the level. The model is drawn exactly as an engine would place the file at the
/// entity's node, so a hole cut around it fits the real asset. They're editor-only unless <see cref="MarkerKey"/> is on,
/// when the entity exports as an empty node to drop the real asset on.
/// <para>
/// The <see cref="ModelKey"/> value is a built-in name (no extension: <c>block_human</c> is
/// <c>assets/references/block_human.glb</c>), an absolute path, or a path relative to the level's folder. Files load once
/// and are cached; <see cref="Refresh"/> reloads the ones that changed on disk.
/// </para>
/// </summary>
public static class ReferenceModels
{
    public const string ClassName = "ref_model";
    public const string ModelKey = "model";
    public const string MarkerKey = "export_marker";
    public const string DefaultModel = "block_human";

    /// <summary>Where built-in models live (the editor ships <c>assets/references</c> beside the executable).</summary>
    public static string BuiltInFolder { get; set; } = Path.Combine(AppContext.BaseDirectory, "assets", "references");

    /// <summary>The open level's file; relative model paths resolve against its folder. Null for unsaved levels.</summary>
    public static string? LevelPath { get; set; }

    /// <summary>Changes whenever a model loads, reloads or drops, so caches of drawn or picked shapes can rebuild.</summary>
    public static int Revision { get; private set; }

    sealed record Entry(long Stamp, ReferenceModel? Model, string? Error);
    static readonly Dictionary<string, Entry> Cache = new(StringComparer.OrdinalIgnoreCase);
    static readonly object Gate = new();

    public static bool Is(Entity e) => e.ClassName == ClassName;

    /// <summary>The entity's model value (the default when the key is missing).</summary>
    public static string ModelOf(Entity e) => e.Properties.TryGetValue(ModelKey, out var m) ? m.Trim() : DefaultModel;

    /// <summary>Whether the entity exports as a marker node. Only reference models can opt out.</summary>
    public static bool Exports(Entity e) =>
        !Is(e) || (e.Properties.TryGetValue(MarkerKey, out var v) && v.Trim() is "1" or "true" or "yes" or "on");

    /// <summary>A built-in name has no folder and no extension.</summary>
    public static bool IsBuiltInName(string model) =>
        model.Length > 0 && model.IndexOfAny(['/', '\\', ':']) < 0 && Path.GetExtension(model).Length == 0;

    /// <summary>The built-in model names, sorted.</summary>
    public static IReadOnlyList<string> BuiltIns()
    {
        try
        {
            return Directory.Exists(BuiltInFolder)
                ? Directory.GetFiles(BuiltInFolder, "*.glb").Select(f => Path.GetFileNameWithoutExtension(f)!).Order(StringComparer.OrdinalIgnoreCase).ToList()
                : [];
        }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }

    /// <summary>A short name for labels and node names: the built-in name or the file's name without extension.</summary>
    public static string DisplayName(string model) => IsBuiltInName(model) ? model : Path.GetFileNameWithoutExtension(model);

    /// <summary>The model's file, or null with a reason when it can't be located.</summary>
    public static (string? Path, string? Error) Resolve(string model)
    {
        if (model.Length == 0) return (null, "Pick a model file");
        if (IsBuiltInName(model)) return (Path.GetFullPath(Path.Combine(BuiltInFolder, model + ".glb")), null);
        if (Path.IsPathRooted(model)) return (Path.GetFullPath(model), null);
        if (LevelPath is not { } level) return (null, $"{model} is relative to the level's folder. Save the level, or pick the file again");
        return (Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(level)) ?? "", model)), null);
    }

    /// <summary>
    /// The value to store for a picked file: relative to the level's folder when the level is saved and the file is on the
    /// same drive, else absolute.
    /// </summary>
    public static string StoredPath(string file)
    {
        file = Path.GetFullPath(file);
        if (LevelPath is not { } level) return file;
        var dir = Path.GetDirectoryName(Path.GetFullPath(level)) ?? "";
        if (!string.Equals(Path.GetPathRoot(dir), Path.GetPathRoot(file), StringComparison.OrdinalIgnoreCase)) return file;
        return Path.GetRelativePath(dir, file).Replace('\\', '/');
    }

    public static (ReferenceModel? Model, string? Error) Get(Entity e) => Is(e) ? Get(ModelOf(e)) : (null, null);

    /// <summary>The loaded model, or null with a reason. Loads on first use; later calls hit the cache.</summary>
    public static (ReferenceModel? Model, string? Error) Get(string model)
    {
        var (path, error) = Resolve(model);
        if (path == null) return (null, error);
        lock (Gate)
            if (Cache.TryGetValue(path, out var hit)) return (hit.Model, hit.Error);
        var entry = Load(path);
        lock (Gate)
        {
            Cache[path] = entry;
            Revision++;
        }
        return (entry.Model, entry.Error);
    }

    /// <summary>Drops models whose file changed (or appeared, or went away) since it was read, so the next use reloads it.</summary>
    public static bool Refresh()
    {
        List<string> stale;
        lock (Gate) stale = Cache.Where(kv => Stamp(kv.Key) != kv.Value.Stamp).Select(kv => kv.Key).ToList();
        if (stale.Count == 0) return false;
        lock (Gate)
        {
            foreach (var p in stale) Cache.Remove(p);
            Revision++;
        }
        return true;
    }

    static long Stamp(string path)
    {
        try { return File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks ^ new FileInfo(path).Length : 0; }
        catch (IOException) { return 0; }
        catch (UnauthorizedAccessException) { return 0; }
    }

    static Entry Load(string path)
    {
        long stamp = Stamp(path);
        string name = Path.GetFileName(path);
        if (stamp == 0) return new(0, null, $"Can't find {name}. Check the path, or pick the file again");
        try
        {
            var result = GltfImporter.Load(path, new MaterialLibrary(), "reference");
            var meshes = result.Meshes.Where(m => m.Faces.Count > 0).ToList();
            if (meshes.Count == 0) return new(stamp, null, $"{name} has no meshes");
            var mesh = meshes[0].Clone();
            MeshOps.MergeMeshes(mesh, meshes.Skip(1));
            return new(stamp, Build(mesh), null);
        }
        catch (Exception e) when (e is InvalidDataException or System.Text.Json.JsonException or IOException or UnauthorizedAccessException
            or FormatException or InvalidOperationException or ArgumentException or IndexOutOfRangeException)
        {
            return new(stamp, null, $"{name} isn't a readable glTF file: {e.Message}");
        }
    }

    /// <summary>Bounds and triangles for a mesh in model space (exposed for tests).</summary>
    public static ReferenceModel Build(PolyMesh mesh)
    {
        var tris = new List<Vector3>();
        var loop = new List<int>();
        for (int f = 0; f < mesh.Faces.Count; f++)
        {
            loop.Clear();
            Triangulator.Triangulate(mesh, f, loop);
            var verts = mesh.Faces[f].Verts;
            foreach (var i in loop) tris.Add(mesh.Positions[verts[i]]);
        }
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var p in mesh.Positions) { min = Vector3.Min(min, p); max = Vector3.Max(max, p); }
        if (mesh.Positions.Count == 0) min = max = Vector3.Zero;
        return new ReferenceModel(mesh, min, max, tris.ToArray());
    }

    /// <summary>Forgets every loaded model (tests).</summary>
    public static void ClearCache()
    {
        lock (Gate)
        {
            Cache.Clear();
            Revision++;
        }
    }
}
