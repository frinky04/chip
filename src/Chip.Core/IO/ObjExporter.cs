using System.Globalization;
using System.Numerics;
using System.Text;
using Chip.Core.Geometry;
using Chip.Core.Scene;

namespace Chip.Core.IO;

/// <summary>
/// Wavefront OBJ + MTL. Faces stay n-gons, one <c>o</c> block per mesh with shared positions, per-corner UVs (V flipped:
/// OBJ's origin is bottom-left) and normals (smoothed across soft edges unless smoothing is off). Positions use the
/// export scale. OBJ has no collision or entities, so collision-only meshes and tool-material faces are left out and
/// entities are skipped. Textures follow <see cref="ExportSettings.EmbedTextures"/> semantics minus embedding: the MTL
/// references library images by relative path, or copies beside the file when it can't.
/// </summary>
public static class ObjExporter
{
    public sealed record ObjFiles(string Obj, string Mtl, IReadOnlyList<SideFile> SideFiles, int Meshes);

    public static ObjFiles Export(Document doc, MaterialLibrary materials, string objPath)
    {
        var files = Build(doc, materials, objPath);
        ExportImages.WriteAll(objPath, files.SideFiles);
        AtomicFile.Write(Path.ChangeExtension(objPath, ".mtl"), Encoding.UTF8.GetBytes(files.Mtl));
        AtomicFile.Write(objPath, Encoding.UTF8.GetBytes(files.Obj));
        return files;
    }

    /// <param name="objPath">Where the OBJ will be written (names the MTL and resolves texture paths); null references nothing.</param>
    public static ObjFiles Build(Document doc, MaterialLibrary materials, string? objPath)
    {
        var inv = CultureInfo.InvariantCulture;
        var opt = doc.Export;
        float s = doc.ExportScale;
        var obj = new StringBuilder();
        var mtl = new StringBuilder();
        var used = new List<string>();
        var usedSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sideFiles = new List<SideFile>();
        string mtlName = objPath != null ? Path.GetFileNameWithoutExtension(objPath) + ".mtl" : "export.mtl";
        obj.Append("# Chip export\n").Append("mtllib ").Append(mtlName).Append('\n');
        int vBase = 1, vtBase = 1, vnBase = 1, meshCount = 0;

        foreach (var mesh in doc.Meshes)
        {
            if (mesh.Hidden && !opt.IncludeHidden) continue;
            if (mesh.Collision is CollisionMode.Only or CollisionMode.ConvexOnly) continue;
            var faces = Enumerable.Range(0, mesh.Faces.Count).Where(f => Surfaces.Renders(mesh.Faces[f].Material)).ToList();
            if (faces.Count == 0) continue;
            meshCount++;
            var normals = opt.Smoothing ? Topology.CornerNormals(mesh) : null;
            obj.Append("o ").Append(Clean($"{mesh.Name}_{mesh.Id}")).Append('\n');
            foreach (var p in mesh.Positions)
                obj.Append(inv, $"v {F(p.X * s)} {F(p.Y * s)} {F(p.Z * s)}\n");

            var uvIndex = new Dictionary<Vector2, int>();
            var nIndex = new Dictionary<Vector3, int>();
            var faceLines = new StringBuilder();
            string? current = null;
            foreach (var f in faces.OrderBy(f => mesh.Faces[f].Material, StringComparer.OrdinalIgnoreCase))
            {
                var face = mesh.Faces[f];
                var info = materials.Get(face.Material);
                if (!string.Equals(current, face.Material, StringComparison.Ordinal))
                {
                    current = face.Material;
                    faceLines.Append("usemtl ").Append(Clean(face.Material)).Append('\n');
                    if (usedSet.Add(face.Material)) used.Add(face.Material);
                }
                var flat = mesh.FaceNormal(f);
                faceLines.Append('f');
                for (int i = 0; i < face.Verts.Length; i++)
                {
                    var uv = face.UV(i, mesh.Positions[face.Verts[i]], info.Width, info.Height);
                    uv = new Vector2(uv.X, 1 - uv.Y);
                    if (!uvIndex.TryGetValue(uv, out int ti)) uvIndex[uv] = ti = uvIndex.Count;
                    var n = normals?[f][i] ?? flat;
                    if (!nIndex.TryGetValue(n, out int ni)) nIndex[n] = ni = nIndex.Count;
                    faceLines.Append(inv, $" {vBase + face.Verts[i]}/{vtBase + ti}/{vnBase + ni}");
                }
                faceLines.Append('\n');
            }
            foreach (var uv in uvIndex.OrderBy(kv => kv.Value).Select(kv => kv.Key)) obj.Append(inv, $"vt {F(uv.X)} {F(uv.Y)}\n");
            foreach (var n in nIndex.OrderBy(kv => kv.Value).Select(kv => kv.Key)) obj.Append(inv, $"vn {F(n.X)} {F(n.Y)} {F(n.Z)}\n");
            obj.Append(faceLines);
            vBase += mesh.Positions.Count;
            vtBase += uvIndex.Count;
            vnBase += nIndex.Count;
        }

        mtl.Append("# Chip export\n");
        foreach (var name in used)
        {
            mtl.Append("\nnewmtl ").Append(Clean(name)).Append('\n');
            mtl.Append("Kd 1 1 1\nKa 0 0 0\nKs 0 0 0\nillum 1\n");
            if (ExportImages.Resolve(materials.Get(name), objPath, embed: false) is { RelativePath: { } rel } image)
            {
                mtl.Append("map_Kd ").Append(rel).Append('\n');
                if (materials.AlphaOf(name) != AlphaMode.Opaque) mtl.Append("map_d ").Append(rel).Append('\n'); // transparency from the same image's alpha
                if (image.Copy != null) sideFiles.Add(image.Copy);
            }
        }
        return new ObjFiles(obj.ToString(), mtl.ToString(), sideFiles, meshCount);
    }

    static string F(float v) => v.ToString("0.######", CultureInfo.InvariantCulture);

    /// <summary>OBJ names end at whitespace.</summary>
    static string Clean(string name) => string.Concat(name.Select(c => char.IsWhiteSpace(c) ? '_' : c));
}
