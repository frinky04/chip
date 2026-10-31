using Chip.Core.Geometry;
using Chip.Core.IO;

namespace Chip.Core.Scene;

/// <summary>The saved level: meshes plus export settings. Geometry is in metres; <see cref="ExportScale"/> multiplies on export (e.g. 100 for centimetre engines).</summary>
public sealed class Document
{
    public List<PolyMesh> Meshes = new();
    public List<Entity> Entities = new();
    /// <summary>Values set on the manifest's world class; absent properties use the game's defaults.</summary>
    public Dictionary<string, System.Text.Json.JsonElement> World = new();
    /// <summary>Outliner groups (stream H). Optional in files; ids share <see cref="NextId"/> with meshes and entities.</summary>
    public List<Group> Groups = new();
    /// <summary>
    /// What linked copies share (<see cref="InstanceDefinition"/>). Never changed in place: an edit assigns a new list, so
    /// undo snapshots keep the old one. Ids share <see cref="NextId"/>.
    /// </summary>
    public IReadOnlyList<InstanceDefinition> Definitions = [];

    /// <summary>Set when this document is a prefab file (<c>*.prefab.chip</c>): its meshes are the prefab's parts.</summary>
    public PrefabHeader? Prefab;

    public InstanceDefinition? FindDefinition(int id)
    {
        foreach (var d in Definitions) if (d.Id == id) return d;
        return null;
    }
    public int NextId = 1;
    /// <summary>
    /// Texel density in pixels per metre ("1 m is 256 px", as in Scythe) for every material without its own override.
    /// New levels use <see cref="NewLevelTexelDensity"/>; files saved before the setting existed load at 512, the scale they
    /// were built at. Change it with <see cref="TexelDensityOps.Change"/> so existing faces follow.
    /// </summary>
    public float TexelDensity = NewLevelTexelDensity;
    public const float NewLevelTexelDensity = 256f;
    public float ExportScale = 1f;
    public string? ExportPath;
    public bool ExportCollision = true;
    /// <summary>Further export options (smoothing, hidden, textures, watch mode, material remap). Stream J.</summary>
    public ExportSettings Export = new();
    /// <summary>
    /// The dressing rules file, relative to the level file (docs/dressing.md). Null: <c>&lt;level&gt;.dressing.json</c> beside the
    /// level, if it exists.
    /// </summary>
    public string? DressingPath;

    public PolyMesh Add(PolyMesh m)
    {
        m.Id = NextId++;
        Meshes.Add(m);
        return m;
    }

    /// <summary>Adds an entity with a fresh id (ids are unique across meshes and entities).</summary>
    public Entity Add(Entity e)
    {
        e.Id = NextId++;
        Entities.Add(e);
        return e;
    }

    /// <summary>Adds a group with a fresh id.</summary>
    public Group Add(Group g)
    {
        g.Id = NextId++;
        Groups.Add(g);
        return g;
    }

    public Group? FindGroup(int id) => Groups.Find(g => g.Id == id);

    public PolyMesh? Find(int id) => Meshes.Find(m => m.Id == id);

    public Entity? FindEntity(int id) => Entities.Find(e => e.Id == id);

    public Document Clone() => Clone(null);

    /// <summary>Only scoped meshes are copied; other mesh references are temporary undo baselines.</summary>
    internal Document Clone(IReadOnlySet<int>? meshIds) => new()
    {
        Meshes = Meshes.Select(m => meshIds == null || meshIds.Contains(m.Id) ? m.Clone() : m).ToList(),
        Entities = Entities.Select(e => e.Clone()).ToList(),
        World = World.ToDictionary(kv => kv.Key, kv => kv.Value.Clone()),
        Groups = Groups.Select(g => g.Clone()).ToList(),
        Definitions = Definitions,
        Prefab = Prefab,
        NextId = NextId,
        TexelDensity = TexelDensity,
        ExportScale = ExportScale,
        ExportPath = ExportPath,
        ExportCollision = ExportCollision,
        Export = Export.Clone(),
        DressingPath = DressingPath,
    };
}
