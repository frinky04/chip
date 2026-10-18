using Chip.Core.Geometry;
using Chip.Core.IO;

namespace Chip.Core.Scene;

/// <summary>The saved level: meshes plus export settings. Geometry is in metres; <see cref="ExportScale"/> multiplies on export (e.g. 100 for centimetre engines).</summary>
public sealed class Document
{
    public List<PolyMesh> Meshes = new();
    public List<Entity> Entities = new();
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

    public PolyMesh? Find(int id) => Meshes.Find(m => m.Id == id);

    public Entity? FindEntity(int id) => Entities.Find(e => e.Id == id);

    public Document Clone() => new()
    {
        Meshes = Meshes.Select(m => m.Clone()).ToList(),
        Entities = Entities.Select(e => e.Clone()).ToList(),
        NextId = NextId,
        ExportScale = ExportScale,
        ExportPath = ExportPath,
        ExportCollision = ExportCollision,
        Export = Export.Clone(),
    };
}
