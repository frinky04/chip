using System.Numerics;
using System.Text.Json;
using Chip.Core.Geometry;
using Chip.Core.Scene;
using Chip.Core.Texturing;

namespace Chip.Core.IO;

/// <summary>Versioned JSON for <c>.chip</c> files. Vectors are float arrays so files stay readable and diffable.</summary>
public static class DocumentSerializer
{
    public const int CurrentVersion = 1;

    static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    sealed record FileDto(int Version, float ExportScale, string? ExportPath, bool ExportCollision, int NextId, List<MeshDto> Meshes);
    sealed record MeshDto(int Id, string Name, bool Hidden, List<float[]> Positions, List<FaceDto> Faces);
    sealed record FaceDto(int[] Verts, string Material, float[] U, float[] V, float[] Shift, float[] Scale, float Rotation);

    public static string Serialize(Document doc) => JsonSerializer.Serialize(new FileDto(
        CurrentVersion, doc.ExportScale, doc.ExportPath, doc.ExportCollision, doc.NextId,
        doc.Meshes.Select(m => new MeshDto(m.Id, m.Name, m.Hidden,
            m.Positions.Select(Arr).ToList(),
            m.Faces.Select(f => new FaceDto(f.Verts, f.Material, Arr(f.Map.UAxis), Arr(f.Map.VAxis),
                [f.Map.ShiftU, f.Map.ShiftV], [f.Map.ScaleU, f.Map.ScaleV], f.Map.Rotation)).ToList())).ToList()), Options);

    public static Document Deserialize(string json)
    {
        var dto = JsonSerializer.Deserialize<FileDto>(json, Options) ?? throw new InvalidDataException("Empty .chip file");
        if (dto.Version > CurrentVersion) throw new InvalidDataException($".chip version {dto.Version} is newer than this editor ({CurrentVersion})");
        var doc = new Document { ExportScale = dto.ExportScale <= 0 ? 1 : dto.ExportScale, ExportPath = dto.ExportPath, ExportCollision = dto.ExportCollision, NextId = dto.NextId };
        foreach (var m in dto.Meshes)
        {
            var mesh = new PolyMesh { Id = m.Id, Name = m.Name, Hidden = m.Hidden, Positions = m.Positions.Select(Vec).ToList() };
            foreach (var f in m.Faces)
            {
                if (f.Verts.Length < 3 || f.Verts.Any(v => v < 0 || v >= mesh.Positions.Count))
                    throw new InvalidDataException($"Mesh {m.Id} has an invalid face");
                mesh.Faces.Add(new Face(f.Verts, f.Material, new TextureMapping
                {
                    UAxis = Vec(f.U), VAxis = Vec(f.V), ShiftU = f.Shift[0], ShiftV = f.Shift[1],
                    ScaleU = f.Scale[0], ScaleV = f.Scale[1], Rotation = f.Rotation,
                }));
            }
            doc.Meshes.Add(mesh);
            doc.NextId = Math.Max(doc.NextId, mesh.Id + 1);
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
