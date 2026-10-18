using Chip.Core.Geometry;
using Chip.Core.IO;

namespace Chip.Core.Scene;

/// <summary>The saved level: meshes plus export settings. Geometry is in metres; <see cref="ExportScale"/> multiplies on export (e.g. 100 for centimetre engines).</summary>
public sealed class Document
{
    public List<PolyMesh> Meshes = new();
    public List<Entity> Entities = new();
    /// <summary>Outliner groups (stream H). Optional in files; ids share <see cref="NextId"/> with meshes and entities.</summary>
    public List<Group> Groups = new();
    public int NextId = 1;
    public float ExportScale = 1f;
    public string? ExportPath;
    public bool ExportCollision = true;
    /// <summary>Further export options (smoothing, hidden, textures, watch mode, material remap). Stream J.</summary>
    public ExportSettings Export = new();

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
        Groups = Groups.Select(g => g.Clone()).ToList(),
        NextId = NextId,
        ExportScale = ExportScale,
        ExportPath = ExportPath,
        ExportCollision = ExportCollision,
        Export = Export.Clone(),
    };
}
