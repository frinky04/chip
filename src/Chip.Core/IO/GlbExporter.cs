using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using Chip.Core.Geometry;
using Chip.Core.Scene;

namespace Chip.Core.IO;

/// <summary>
/// Writes a glTF 2.0 binary. One node per mesh at the world origin (stable across re-exports), one primitive per material,
/// flat normals, UVs from each face's projection, and material images embedded as PNG.
/// With <see cref="Document.ExportCollision"/>, node names get Godot's <c>-col</c> suffix (static trimesh collision).
/// </summary>
public static class GlbExporter
{
    public static void Export(Document doc, MaterialLibrary materials, string path) =>
        AtomicFile.Write(path, Build(doc, materials));

    public static byte[] Build(Document doc, MaterialLibrary materials)
    {
        var bin = new MemoryStream();
        var bufferViews = new JsonArray();
        var accessors = new JsonArray();
        var materialIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var gltfMaterials = new JsonArray();
        var images = new JsonArray();
        var textures = new JsonArray();
        var meshes = new JsonArray();
        var nodes = new JsonArray();
        float s = doc.ExportScale;

        int View(ReadOnlySpan<byte> data, int? target)
        {
            while (bin.Length % 4 != 0) bin.WriteByte(0);
            var view = new JsonObject { ["buffer"] = 0, ["byteOffset"] = bin.Length, ["byteLength"] = data.Length };
            if (target is { } t) view["target"] = t;
            bin.Write(data);
            bufferViews.Add(view);
            return bufferViews.Count - 1;
        }

        int Accessor<T>(List<T> data, int componentType, string type, int? target, JsonObject? extra = null) where T : struct
        {
            var acc = new JsonObject
            {
                ["bufferView"] = View(MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(data)), target),
                ["componentType"] = componentType, ["count"] = data.Count, ["type"] = type,
            };
            if (extra != null) foreach (var kv in extra) acc[kv.Key] = kv.Value?.DeepClone();
            accessors.Add(acc);
            return accessors.Count - 1;
        }

        int Material(string name)
        {
            if (materialIndex.TryGetValue(name, out var idx)) return idx;
            var info = materials.Get(name);
            var pbr = new JsonObject { ["metallicFactor"] = 0f, ["roughnessFactor"] = 1f };
            if (info.Path != null && File.Exists(info.Path))
            {
                images.Add(new JsonObject { ["bufferView"] = View(File.ReadAllBytes(info.Path), null), ["mimeType"] = "image/png", ["name"] = info.Name });
                textures.Add(new JsonObject { ["source"] = images.Count - 1, ["sampler"] = 0 });
                pbr["baseColorTexture"] = new JsonObject { ["index"] = textures.Count - 1 };
            }
            else pbr["baseColorFactor"] = new JsonArray(1f, 0f, 1f, 1f);
            gltfMaterials.Add(new JsonObject { ["name"] = name, ["pbrMetallicRoughness"] = pbr });
            return materialIndex[name] = gltfMaterials.Count - 1;
        }

        var tri = new List<int>();
        foreach (var mesh in doc.Meshes)
        {
            var primitives = new JsonArray();
            foreach (var group in Enumerable.Range(0, mesh.Faces.Count).GroupBy(f => mesh.Faces[f].Material))
            {
                var info = materials.Get(group.Key);
                var pos = new List<Vector3>();
                var nrm = new List<Vector3>();
                var uv = new List<Vector2>();
                var idx = new List<uint>();
                foreach (var f in group)
                {
                    var face = mesh.Faces[f];
                    var n = mesh.FaceNormal(f);
                    uint baseIndex = (uint)pos.Count;
                    foreach (var v in face.Verts)
                    {
                        var p = mesh.Positions[v];
                        pos.Add(p * s);
                        nrm.Add(n);
                        uv.Add(face.Map.Project(p, info.Width, info.Height));
                    }
                    tri.Clear();
                    Triangulator.Triangulate(mesh, f, tri);
                    foreach (var t in tri) idx.Add(baseIndex + (uint)t);
                }
                if (idx.Count == 0) continue;
                var min = pos.Aggregate(new Vector3(float.MaxValue), Vector3.Min);
                var max = pos.Aggregate(new Vector3(float.MinValue), Vector3.Max);
                primitives.Add(new JsonObject
                {
                    ["attributes"] = new JsonObject
                    {
                        ["POSITION"] = Accessor(pos, 5126, "VEC3", 34962, new JsonObject { ["min"] = new JsonArray(min.X, min.Y, min.Z), ["max"] = new JsonArray(max.X, max.Y, max.Z) }),
                        ["NORMAL"] = Accessor(nrm, 5126, "VEC3", 34962),
                        ["TEXCOORD_0"] = Accessor(uv, 5126, "VEC2", 34962),
                    },
                    ["indices"] = Accessor(idx, 5125, "SCALAR", 34963),
                    ["material"] = Material(group.Key),
                });
            }
            if (primitives.Count == 0) continue;
            string name = $"{mesh.Name}_{mesh.Id}";
            meshes.Add(new JsonObject { ["name"] = name, ["primitives"] = primitives });
            nodes.Add(new JsonObject { ["name"] = doc.ExportCollision ? name + "-col" : name, ["mesh"] = meshes.Count - 1 });
        }

        var root = new JsonObject
        {
            ["asset"] = new JsonObject { ["version"] = "2.0", ["generator"] = "Chip" },
            ["scene"] = 0,
            ["scenes"] = new JsonArray(new JsonObject { ["nodes"] = new JsonArray(Enumerable.Range(0, nodes.Count).Select(i => (JsonNode)i).ToArray()) }),
            ["nodes"] = nodes,
            ["meshes"] = meshes,
            ["materials"] = gltfMaterials,
            ["accessors"] = accessors,
            ["bufferViews"] = bufferViews,
            ["buffers"] = new JsonArray(new JsonObject { ["byteLength"] = Align4(bin.Length) }),
        };
        if (images.Count > 0)
        {
            root["images"] = images;
            root["textures"] = textures;
            root["samplers"] = new JsonArray(new JsonObject { ["magFilter"] = 9729, ["minFilter"] = 9987, ["wrapS"] = 10497, ["wrapT"] = 10497 });
        }
        if (nodes.Count == 0) { root.Remove("meshes"); root.Remove("materials"); root.Remove("accessors"); root.Remove("bufferViews"); root.Remove("buffers"); }

        var json = Encoding.UTF8.GetBytes(root.ToJsonString());
        int jsonLen = (int)Align4(json.Length);
        int binLen = (int)Align4(bin.Length);
        bool hasBin = bin.Length > 0;
        var outStream = new MemoryStream();
        var w = new BinaryWriter(outStream);
        w.Write(0x46546C67u);
        w.Write(2u);
        w.Write((uint)(12 + 8 + jsonLen + (hasBin ? 8 + binLen : 0)));
        w.Write((uint)jsonLen);
        w.Write(0x4E4F534Au);
        w.Write(json);
        for (int i = json.Length; i < jsonLen; i++) w.Write((byte)' ');
        if (hasBin)
        {
            w.Write((uint)binLen);
            w.Write(0x004E4942u);
            w.Write(bin.GetBuffer(), 0, (int)bin.Length);
            for (long i = bin.Length; i < binLen; i++) w.Write((byte)0);
        }
        w.Flush();
        return outStream.ToArray();
    }

    static long Align4(long n) => (n + 3) & ~3L;
}
