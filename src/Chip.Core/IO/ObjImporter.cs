using System.Globalization;
using System.Numerics;
using Chip.Core.Scene;

namespace Chip.Core.IO;

/// <summary>
/// Reads Wavefront OBJ into editable meshes: one per <c>o</c> (or <c>g</c>) block, faces kept as n-gons and coplanar
/// neighbours with one material merged (<see cref="ImportBuilder"/>), <c>usemtl</c> names resolved against the library,
/// <c>vt</c> as explicit UVs with V flipped back to Chip's top-left origin. MTL files aren't needed: names are enough.
/// </summary>
public static class ObjImporter
{
    public static ImportResult Load(string path, MaterialLibrary materials, string fallbackMaterial, float scale = 1) =>
        Parse(File.ReadAllText(path), materials, fallbackMaterial, scale);

    public static ImportResult Parse(string text, MaterialLibrary materials, string fallbackMaterial, float scale = 1)
    {
        var inv = CultureInfo.InvariantCulture;
        var warnings = new List<string>();
        var v = new List<Vector3>();
        var vt = new List<Vector2>();
        var vn = new List<Vector3>();
        var objects = new List<(string Name, ImportBuilder Builder)>();
        ImportBuilder? current = null;
        string pendingName = "imported";
        string material = fallbackMaterial;
        int skipped = 0;

        ImportBuilder Current()
        {
            if (current == null) { current = new ImportBuilder(); objects.Add((pendingName, current)); }
            return current;
        }

        int Resolve(string token, int count) =>
            int.TryParse(token, NumberStyles.Integer, inv, out int i) ? (i < 0 ? count + i : i - 1) : -1;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            int hash = line.IndexOf('#');
            if (hash >= 0) line = line[..hash].TrimEnd();
            if (line.Length == 0) continue;
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            float F(int i) => i < parts.Length && float.TryParse(parts[i], NumberStyles.Float, inv, out float f) ? f : 0;
            switch (parts[0])
            {
                case "v": v.Add(new Vector3(F(1), F(2), F(3)) * scale); break;
                case "vt": vt.Add(new Vector2(F(1), 1 - F(2))); break;
                case "vn": vn.Add(new Vector3(F(1), F(2), F(3))); break;
                case "o" or "g":
                    var name = parts.Length > 1 ? string.Join(' ', parts.Skip(1)) : "imported";
                    if (current != null && current.PolygonCount == 0) objects[^1] = (name, current);
                    else { current = null; pendingName = name; }
                    break;
                case "usemtl":
                    var m = parts.Length > 1 ? string.Join(' ', parts.Skip(1)) : "";
                    material = m.Length == 0 ? fallbackMaterial : materials.Contains(m) ? materials.Get(m).Name : m;
                    break;
                case "f":
                    var b = Current();
                    int n = parts.Length - 1;
                    var verts = new int[n];
                    var uvs = new Vector2[n];
                    var normals = new Vector3[n];
                    bool hasUv = true, hasNormal = true, ok = n >= 3;
                    for (int i = 0; i < n && ok; i++)
                    {
                        var refs = parts[i + 1].Split('/');
                        int pi = Resolve(refs[0], v.Count);
                        if (pi < 0 || pi >= v.Count) { ok = false; break; }
                        verts[i] = b.AddVertex(v[pi]);
                        int ti = refs.Length > 1 && refs[1].Length > 0 ? Resolve(refs[1], vt.Count) : -1;
                        if (ti >= 0 && ti < vt.Count) uvs[i] = vt[ti]; else hasUv = false;
                        int ni = refs.Length > 2 && refs[2].Length > 0 ? Resolve(refs[2], vn.Count) : -1;
                        if (ni >= 0 && ni < vn.Count) normals[i] = vn[ni]; else hasNormal = false;
                    }
                    if (!ok) { skipped++; break; }
                    b.AddPolygon(verts, material, hasUv ? uvs : null, hasNormal ? normals : null);
                    break;
            }
        }
        if (skipped > 0) warnings.Add(skipped == 1 ? "Skipped 1 face with a bad vertex index" : $"Skipped {skipped} faces with bad vertex indices");

        // Chip's own exports name objects "<name>_<id>" so they stay unique; strip the id back off, but only for those files
        // (other tools' "wall_3" is a real name).
        bool fromChip = text.StartsWith("# Chip export", StringComparison.Ordinal);
        var result = new ImportResult(new(), new(), warnings);
        foreach (var (name, builder) in objects)
            if (builder.PolygonCount > 0) result.Meshes.Add(builder.Build(fromChip ? StripChipId(name) : name, materials));
        return result;
    }

    static string StripChipId(string name)
    {
        int cut = name.LastIndexOf('_');
        return cut > 0 && cut < name.Length - 1 && name.AsSpan(cut + 1).IndexOfAnyExceptInRange('0', '9') < 0 ? name[..cut] : name;
    }
}
