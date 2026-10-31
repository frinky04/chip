using System.Text.Json;
using Chip.Core.Geometry;
using Chip.Core.IO;

namespace Chip.Core.Scene;

/// <summary>A prefab file found in the project: where it is, its id and its name.</summary>
public sealed record PrefabInfo(string FullPath, string RelativePath, string Guid, string Name);

/// <summary>
/// Prefab files: a <c>*.prefab.chip</c> is an ordinary level whose meshes are a prop's parts, around the file's world
/// origin, with a <see cref="PrefabHeader"/>. Levels hold linked copies of it through an <see cref="InstanceDefinition"/>
/// whose <see cref="InstanceDefinition.Source"/> names the file by its path relative to the project root (the folder of
/// <c>chip.project.json</c> or <c>project.godot</c>, else the level's own folder) and by its id, so a moved or renamed
/// file is found again. Editing a copy writes the file (<see cref="Session"/>); opening a level, or the file changing,
/// reads it back (<see cref="Refresh"/>).
/// </summary>
public static class Prefabs
{
    public const string Extension = ".prefab.chip";

    /// <summary>Folders never searched: engine caches, version control, build output.</summary>
    static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase) { ".godot", ".git", ".import", ".chip", "bin", "obj", "node_modules", "artifacts" };

    /// <summary>The folder prefab paths are relative to, for a level saved at <paramref name="levelPath"/>; null when unsaved.</summary>
    public static string? RootFor(string? levelPath)
    {
        if (string.IsNullOrWhiteSpace(levelPath)) return null;
        return GameProject.FindRoot(levelPath) ?? Path.GetDirectoryName(Path.GetFullPath(levelPath));
    }

    /// <summary>A reference's file on disk.</summary>
    public static string FullPath(string root, PrefabRef r) => Path.GetFullPath(Path.Combine(root, r.Path.Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>How a reference names <paramref name="fullPath"/>: relative to the root, with forward slashes.</summary>
    public static string Relative(string root, string fullPath) => Path.GetRelativePath(root, fullPath).Replace('\\', '/');

    public static bool IsPrefabPath(string path) => path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase);

    /// <summary>"crate" for <c>props/crate.prefab.chip</c>.</summary>
    public static string NameFromPath(string path)
    {
        var file = Path.GetFileName(path);
        return IsPrefabPath(file) ? file[..^Extension.Length] : Path.GetFileNameWithoutExtension(file);
    }

    /// <summary>Where new prefabs go: the project's <c>prefabs.default</c>, else a <c>prefabs</c> folder in the root.</summary>
    public static string DefaultFolder(string root) =>
        Path.GetFullPath(Path.Combine(root, GameProject.Find(root)?.PrefabDefault ?? "prefabs"));

    /// <summary>
    /// Every prefab file under the root (or the project's <c>prefabs.roots</c>), sorted by path. Files that can't be
    /// read are left out.
    /// </summary>
    public static List<PrefabInfo> Scan(string root)
    {
        var folders = GameProject.Find(root)?.PrefabRoots is { Count: > 0 } roots
            ? roots.Select(r => Path.GetFullPath(Path.Combine(root, r))).Where(Directory.Exists).ToList()
            : [root];
        var result = new List<PrefabInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in folders)
            foreach (var file in Files(folder))
                if (seen.Add(file) && ReadHeader(file) is { } h)
                    result.Add(new PrefabInfo(file, Relative(root, file), h.Guid, h.Name));
        result.Sort((a, b) => string.Compare(a.RelativePath, b.RelativePath, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    static IEnumerable<string> Files(string folder)
    {
        var stack = new Stack<string>([folder]);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            string[] files, dirs;
            try
            {
                files = Directory.GetFiles(dir, "*" + Extension);
                dirs = Directory.GetDirectories(dir);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            foreach (var f in files) yield return f;
            foreach (var d in dirs)
                if (!Skipped.Contains(Path.GetFileName(d))) stack.Push(d);
        }
    }

    /// <summary>The header of a prefab file without reading its meshes, or null if it isn't a readable prefab.</summary>
    public static PrefabHeader? ReadHeader(string file)
    {
        try
        {
            using var stream = File.OpenRead(file);
            using var json = JsonDocument.Parse(stream);
            if (!json.RootElement.TryGetProperty("prefab", out var p) || p.ValueKind != JsonValueKind.Object) return null;
            var guid = p.TryGetProperty("guid", out var g) ? g.GetString() : null;
            if (string.IsNullOrEmpty(guid)) return null;
            return new PrefabHeader(guid, p.TryGetProperty("name", out var n) && n.GetString() is { Length: > 0 } name ? name : NameFromPath(file));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { return null; }
    }

    /// <summary>The parts a prefab document stands for: its meshes, each keyed by its id (stable within the file).</summary>
    public static List<DefinitionPart> Parts(Document prefab) => prefab.Meshes.Select(m =>
    {
        var part = m.Clone();
        part.Hidden = part.Locked = false;
        part.Origin = null;
        part.Part = 0;
        return new DefinitionPart(m.Id, part);
    }).ToList();

    /// <summary>Reads a prefab file and checks it can be placed. Throws (IO, <see cref="InvalidDataException"/>, JSON) when it can't.</summary>
    public static Document Read(string fullPath)
    {
        var prefab = DocumentSerializer.Load(fullPath);
        if (prefab.Prefab == null) throw new InvalidDataException($"{Path.GetFileName(fullPath)} isn't a prefab (it has no \"prefab\" header)");
        if (prefab.Meshes.Count == 0) throw new InvalidDataException($"{Path.GetFileName(fullPath)} has no meshes to place");
        return prefab;
    }

    /// <summary>
    /// A new definition (id from <paramref name="doc"/>) for the prefab file at <paramref name="fullPath"/>, from its
    /// contents (<paramref name="prefab"/>, read with <see cref="Read"/>; read now when null).
    /// </summary>
    public static InstanceDefinition Load(Document doc, string root, string fullPath, Document? prefab = null)
    {
        prefab ??= Read(fullPath);
        var header = prefab.Prefab!;
        return new InstanceDefinition(doc.NextId++, header.Name, Parts(prefab)) { Source = new PrefabRef(Relative(root, fullPath), header.Guid) };
    }

    /// <summary>
    /// The text of the prefab file for <paramref name="def"/>: its parts as the file's meshes, keeping whatever else the
    /// file had (<paramref name="existing"/>, its current text, or null for a new file).
    /// </summary>
    public static string Write(InstanceDefinition def, string? existing)
    {
        Document doc;
        try { doc = existing != null ? DocumentSerializer.Deserialize(existing) : new Document(); }
        catch (Exception e) when (e is InvalidDataException or JsonException) { doc = new Document(); }
        doc.Meshes = def.Parts.Select(p =>
        {
            var m = p.Mesh.Clone();
            m.Id = p.Key;
            m.Part = 0;
            return m;
        }).ToList();
        doc.NextId = Math.Max(doc.NextId, doc.Meshes.Count == 0 ? 1 : doc.Meshes.Max(m => m.Id) + 1);
        doc.Groups.RemoveAll(g => true); // parts are flat; groups would point at old ids
        doc.Prefab = new PrefabHeader(def.Source?.Guid ?? NewGuid(), doc.Prefab?.Name ?? def.Name);
        return DocumentSerializer.Serialize(doc);
    }

    public static string NewGuid() => Guid.NewGuid().ToString("N");

    /// <summary>
    /// Makes <paramref name="def"/> a prefab saved at <paramref name="fullPath"/>: a new id, the file's name as its
    /// name. Returns the new definition and the file's text; the caller replaces the definition and writes the file.
    /// </summary>
    public static (InstanceDefinition Def, string Text) Promote(InstanceDefinition def, string root, string fullPath)
    {
        var promoted = def with { Name = NameFromPath(fullPath), Source = new PrefabRef(Relative(root, fullPath), NewGuid()) };
        return (promoted, Write(promoted, null));
    }

    /// <summary>What <see cref="Refresh"/> found for one prefab.</summary>
    public enum Status { Same, Updated, Moved, Missing, Unreadable }

    /// <summary>
    /// Brings the level's prefab copies up to date with their files: a file that changed replaces its definition and
    /// rebuilds the copies; a file that moved is found by its id and the reference follows it; a missing one leaves the
    /// copies as they last were. Edits <paramref name="doc"/> directly: run it on a freshly loaded level, or inside a
    /// session step. Returns what happened to each prefab it looked at (by definition id), with a message for problems.
    /// </summary>
    public static List<(int Definition, Status Status, string? Message)> Refresh(Document doc, string root, Func<string, bool>? only = null,
        List<PrefabInfo>? index = null)
    {
        var result = new List<(int, Status, string?)>();
        foreach (var def in doc.Definitions.ToList())
        {
            if (def.Source is not { } src) continue;
            var full = FullPath(root, src);
            if (only != null && !only(full)) continue;
            var current = def;
            var status = Status.Same;
            if (!File.Exists(full) || ReadHeader(full)?.Guid != src.Guid)
            {
                index ??= Scan(root);
                if (index.Find(p => p.Guid == src.Guid) is not { } found)
                {
                    result.Add((def.Id, Status.Missing, $"Prefab {src.Path} is missing: its copies keep their last shape"));
                    continue;
                }
                full = found.FullPath;
                current = current with { Source = src with { Path = found.RelativePath } };
                status = Status.Moved;
            }
            Document prefab;
            try { prefab = DocumentSerializer.Load(full); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
            {
                result.Add((def.Id, Status.Unreadable, $"Can't read prefab {src.Path}: {e.Message}"));
                continue;
            }
            var parts = Parts(prefab);
            if (!InstanceSync.Same(parts, def.Parts))
            {
                current = current with { Parts = parts };
                if (status == Status.Same) status = Status.Updated;
            }
            if (!ReferenceEquals(current, def))
            {
                doc.Definitions = doc.Definitions.Select(d => d.Id == def.Id ? current : d).ToList();
                if (!ReferenceEquals(current.Parts, def.Parts))
                    foreach (var copy in InstanceOps.Copies(doc, def.Id))
                        if (copy.Origin != null) InstanceOps.Rebuild(doc, copy, current).ToList();
            }
            result.Add((def.Id, status, null));
        }
        return result;
    }

    /// <summary>The definition in <paramref name="doc"/> for the prefab with this id, if the level already uses it.</summary>
    public static InstanceDefinition? Find(Document doc, string guid) => doc.Definitions.FirstOrDefault(d => d.Source?.Guid == guid);
}
