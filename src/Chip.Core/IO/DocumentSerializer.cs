using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Chip.Core.Geometry;
using Chip.Core.Scene;
using Chip.Core.Texturing;

namespace Chip.Core.IO;

/// <summary>
/// Versioned JSON for <c>.chip</c> files. Vectors are float arrays so files stay readable and diffable.
/// v2 adds per-corner UVs, soft edges, per-mesh lock/collision and entities; every v2 field is optional, so v1 loads as is.
/// Later additive v2 fields (also optional, omitted when default): per-face <c>dominant</c> (dominant normals).
/// </summary>
public static partial class DocumentSerializer
{
    public const int CurrentVersion = 2;

    static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    sealed record FileDto(int Version, float ExportScale, string? ExportPath, bool ExportCollision, int NextId,
        List<MeshDto> Meshes, List<EntityDto>? Entities,
        ExportDto? Export = null,
        List<GroupDto>? Groups = null, // stream H: outliner groups (optional, no version bump)
        float? TexelDensity = null,    // px per metre; files without it predate the setting and were built at 512
        string? Dressing = null);      // dressing rules file, relative to the level (optional, no version bump)

    sealed record MeshDto(int Id, string Name, bool Hidden, List<float[]> Positions, List<FaceDto> Faces,
        bool Locked = false, CollisionMode Collision = CollisionMode.Default, List<int[]>? SoftEdges = null,
        string[]? Tags = null,  // dressing tags (optional, no version bump)
        DressingDto? Dressing = null);

    /// <summary>A mesh's dressing overrides: <c>{ "set": { "railing": { "balusterEvery": 0.1 } }, "skip": ["debris"] }</c>.</summary>
    sealed record DressingDto(Dictionary<string, JsonElement>? Set = null, string[]? Skip = null);

    static DressingDto? DressingToDto(MeshDressing d) => d.IsEmpty ? null : new DressingDto(
        d.Set.Count > 0 ? d.Set.ToDictionary(kv => kv.Key, kv => JsonDocument.Parse(kv.Value).RootElement.Clone()) : null,
        d.Skip.Count > 0 ? d.Skip.ToArray() : null);

    static MeshDressing DressingFromDto(DressingDto? d, int meshId)
    {
        if (d == null) return MeshDressing.Empty;
        if (d.Set?.FirstOrDefault(kv => kv.Value.ValueKind != JsonValueKind.Object) is { Key: not null } bad)
            throw new InvalidDataException($"Mesh {meshId}: dressing overrides for \"{bad.Key}\" must be an object of parameters");
        return MeshDressing.Of(d.Set?.Select(kv => KeyValuePair.Create(kv.Key, kv.Value.GetRawText())) ?? [], d.Skip ?? []);
    }

    sealed record FaceDto(int[] Verts, string Material, float[] U, float[] V, float[] Shift, float[] Scale, float Rotation,
        List<float[]>? Uvs = null, bool? Dominant = null, string[]? Tags = null);

    static string[]? TagsDto(TagSet tags) => tags.IsEmpty ? null : tags.ToArray();

    static TagSet TagsFromDto(string[]? tags, string where)
    {
        if (tags == null) return default;
        try { return TagSet.Of(tags); }
        catch (ArgumentException e) { throw new InvalidDataException($"{where}: {e.Message}"); }
    }

    sealed record EntityDto(int Id, string ClassName, string Name, float[] Position, float[] Rotation, bool Hidden,
        Dictionary<string, string>? Properties,
        bool Locked = false); // stream H

    // stream H: a group's direct members are object ids; Parent 0 = top level.
    sealed record GroupDto(int Id, string Name, int Parent, int[] Members, bool Hidden = false, bool Locked = false);

    public static string Serialize(Document doc) => JsonSerializer.Serialize(new FileDto(
        CurrentVersion, doc.ExportScale, doc.ExportPath, doc.ExportCollision, doc.NextId,
        doc.Meshes.Select(m => new MeshDto(m.Id, m.Name, m.Hidden,
            m.Positions.Select(Arr).ToList(),
            m.Faces.Select(f => new FaceDto(f.Verts, f.Material, Arr(f.Map.UAxis), Arr(f.Map.VAxis),
                [f.Map.ShiftU, f.Map.ShiftV], [f.Map.ScaleU, f.Map.ScaleV], f.Map.Rotation,
                f.UVs?.Select(uv => new[] { uv.X, uv.Y }).ToList(), f.Dominant ? true : null, TagsDto(f.Tags))).ToList(),
            m.Locked, m.Collision,
            m.SoftEdges.Count > 0 ? m.SoftEdges.OrderBy(e => e.A).ThenBy(e => e.B).Select(e => new[] { e.A, e.B }).ToList() : null,
            TagsDto(m.Tags), DressingToDto(m.Dressing))).ToList(),
        doc.Entities.Count > 0
            ? doc.Entities.Select(e => new EntityDto(e.Id, e.ClassName, e.Name, Arr(e.Position), Arr(e.Rotation), e.Hidden,
                e.Properties.Count > 0 ? e.Properties : null,
                e.Locked)).ToList()
            : null,
        ExportToDto(doc.Export),
        doc.Groups.Count > 0 ? doc.Groups.Select(g => new GroupDto(g.Id, g.Name, g.Parent, g.Members.ToArray(), g.Hidden, g.Locked)).ToList() : null,
        doc.TexelDensity, doc.DressingPath), Options);

    /// <summary>Density of levels saved before <see cref="Document.TexelDensity"/> existed (the old fixed 512 px/m).</summary>
    public const float LegacyTexelDensity = 512f;

    public static Document Deserialize(string json)
    {
        var dto = JsonSerializer.Deserialize<FileDto>(json, Options) ?? throw new InvalidDataException("Empty .chip file");
        if (dto.Version > CurrentVersion) throw new InvalidDataException($".chip version {dto.Version} is newer than this editor ({CurrentVersion})");
        var doc = new Document
        {
            ExportScale = dto.ExportScale <= 0 ? 1 : dto.ExportScale, ExportPath = dto.ExportPath, ExportCollision = dto.ExportCollision, NextId = dto.NextId,
            TexelDensity = dto.TexelDensity is float t && t > 0 ? t : LegacyTexelDensity,
            DressingPath = dto.Dressing,
        };
        doc.Export = ExportFromDto(dto.Export);
        foreach (var m in dto.Meshes)
        {
            var mesh = new PolyMesh
            {
                Id = m.Id, Name = m.Name, Hidden = m.Hidden, Locked = m.Locked, Collision = m.Collision,
                Positions = m.Positions.Select(Vec).ToList(), Tags = TagsFromDto(m.Tags, $"Mesh {m.Id}"),
                Dressing = DressingFromDto(m.Dressing, m.Id),
            };
            foreach (var f in m.Faces)
            {
                if (f.Verts.Length < 3 || f.Verts.Any(v => v < 0 || v >= mesh.Positions.Count))
                    throw new InvalidDataException($"Mesh {m.Id} has an invalid face");
                if (f.Uvs != null && f.Uvs.Count != f.Verts.Length)
                    throw new InvalidDataException($"Mesh {m.Id} has a face whose UV count doesn't match its corners");
                mesh.Faces.Add(new Face(f.Verts, f.Material, new TextureMapping
                {
                    UAxis = Vec(f.U), VAxis = Vec(f.V), ShiftU = f.Shift[0], ShiftV = f.Shift[1],
                    ScaleU = f.Scale[0], ScaleV = f.Scale[1], Rotation = f.Rotation,
                })
                {
                    UVs = f.Uvs?.Select(uv => new Vector2(uv[0], uv[1])).ToArray(), Dominant = f.Dominant == true,
                    Tags = TagsFromDto(f.Tags, $"Mesh {m.Id}"),
                });
            }
            if (m.SoftEdges != null)
                foreach (var e in m.SoftEdges)
                    if (e.Length == 2 && e[0] >= 0 && e[1] >= 0 && e[0] < mesh.Positions.Count && e[1] < mesh.Positions.Count)
                        mesh.SoftEdges.Add(new Edge(e[0], e[1]));
            doc.Meshes.Add(mesh);
            doc.NextId = Math.Max(doc.NextId, mesh.Id + 1);
        }
        if (dto.Entities != null)
            foreach (var e in dto.Entities)
            {
                doc.Entities.Add(new Entity
                {
                    Id = e.Id, ClassName = e.ClassName, Name = e.Name, Position = Vec(e.Position), Rotation = Vec(e.Rotation),
                    Hidden = e.Hidden, Properties = e.Properties ?? new(),
                    Locked = e.Locked,
                });
                doc.NextId = Math.Max(doc.NextId, e.Id + 1);
            }
        if (dto.Groups != null) // stream H
            foreach (var g in dto.Groups)
            {
                doc.Groups.Add(new Group { Id = g.Id, Name = g.Name ?? "Group", Parent = g.Parent, Members = (g.Members ?? []).ToList(), Hidden = g.Hidden, Locked = g.Locked });
                doc.NextId = Math.Max(doc.NextId, g.Id + 1);
            }
        return doc;
    }

    public static void Save(Document doc, string path) => AtomicFile.Write(path, System.Text.Encoding.UTF8.GetBytes(Serialize(doc)));

    public static Document Load(string path) => Deserialize(File.ReadAllText(path));

    static float[] Arr(Vector3 v) => [v.X, v.Y, v.Z];
    static Vector3 Vec(float[] a) => new(a[0], a[1], a[2]);
}

public static class AtomicFile
{
    /// <summary>Writes beside the target then swaps it in, so a crash never leaves a half-written file.</summary>
    public static void Write(string path, byte[] bytes)
    {
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, path, overwrite: true);
    }
}
