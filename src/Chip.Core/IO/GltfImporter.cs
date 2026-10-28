using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Chip.Core.Geometry;
using Chip.Core.Scene;

namespace Chip.Core.IO;

/// <summary>
/// Reads <c>.glb</c> and <c>.gltf</c> (embedded, data-URI or external buffers) into editable meshes, one per mesh node
/// with its world transform baked in. Triangles, strips and fans are rebuilt into n-gons by <see cref="ImportBuilder"/>;
/// TEXCOORD_0 becomes explicit UVs; material names resolve against the library (unknown names are kept, so they show as
/// missing until the texture is added). Files written by Chip round-trip: <c>extras.chip_id</c> regroups a mesh with its
/// clip/trigger nodes, <c>extras.chip_mesh</c> restores its name, Godot collision suffixes restore its
/// <see cref="CollisionMode"/>, and empty nodes with <c>extras.chip_class</c> come back as entities.
/// </summary>
public static class GltfImporter
{
    const uint Magic = 0x46546C67, JsonChunk = 0x4E4F534A, BinChunk = 0x004E4942;

    /// <param name="scale">Multiplies positions (the editor passes 1 / export scale so a Chip export comes back in metres).</param>
    public static ImportResult Load(string path, MaterialLibrary materials, string fallbackMaterial, float scale = 1) =>
        Parse(File.ReadAllBytes(path), Path.GetDirectoryName(Path.GetFullPath(path)), materials, fallbackMaterial, scale);

    public static ImportResult Parse(byte[] data, string? baseDir, MaterialLibrary materials, string fallbackMaterial, float scale = 1)
    {
        var warnings = new List<string>();
        JsonNode root;
        byte[]? glbBin = null;
        if (data.Length >= 12 && BinaryPrimitives.ReadUInt32LittleEndian(data) == Magic)
        {
            int offset = 12;
            JsonNode? json = null;
            while (offset + 8 <= data.Length)
            {
                int len = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));
                uint type = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 4));
                if (offset + 8 + len > data.Length) throw new InvalidDataException("The GLB is truncated");
                if (type == JsonChunk) json = JsonNode.Parse(data.AsSpan(offset + 8, len));
                else if (type == BinChunk && glbBin == null) glbBin = data.AsSpan(offset + 8, len).ToArray();
                offset += 8 + len;
            }
            root = json ?? throw new InvalidDataException("The GLB has no JSON chunk");
        }
        else root = JsonNode.Parse(Encoding.UTF8.GetString(data).TrimStart('﻿')) ?? throw new InvalidDataException("Empty glTF file");

        var reader = new Reader(root, glbBin, baseDir);
        var gltfMaterials = root["materials"]?.AsArray();
        var meshes = root["meshes"]?.AsArray();
        var nodes = root["nodes"]?.AsArray() ?? [];
        var groups = new Dictionary<string, (ImportBuilder Builder, string Name, CollisionMode Collision)>();
        var order = new List<string>();
        var entities = new List<Entity>();

        string MaterialName(int? index, string? tool)
        {
            if (index is not { } i) return tool ?? fallbackMaterial;
            var name = gltfMaterials?[i]?["name"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(name)) return fallbackMaterial;
            return materials.Contains(name) ? materials.Get(name).Name : name;
        }

        var ownedKeys = new Dictionary<Entity, List<string>>();

        void Visit(int nodeIndex, Matrix4x4 parent, int depth, Entity? owner)
        {
            if (depth > 64 || nodes[nodeIndex] is not JsonObject node) return;
            var world = LocalMatrix(node) * parent;
            var extras = node["extras"] as JsonObject;
            // Baked world collision is derived from the meshes it came from; re-importing those is enough.
            if (extras?["chip_baked"]?.GetValue<bool>() == true) return;
            string nodeName = node["name"]?.GetValue<string>() ?? "";
            if (node["mesh"]?.GetValue<int>() is int meshIndex && meshes?[meshIndex] is JsonObject mesh)
            {
                // A surface node's faces go back to the surface's material (render-only surface nodes keep their own).
                string? tool = extras?["chip_surface"]?.GetValue<string>() ?? extras?["chip_tool"]?.GetValue<string>();
                string key = extras?["chip_id"] is { } id ? $"chip:{id.ToJsonString()}" : $"node:{nodeIndex}";
                if (owner != null)
                {
                    if (!ownedKeys.TryGetValue(owner, out var keys)) ownedKeys[owner] = keys = new List<string>();
                    if (!keys.Contains(key)) keys.Add(key);
                }
                var (baseName, collision) = StripCollisionSuffix(nodeName);
                if (!groups.TryGetValue(key, out var group))
                {
                    group = (new ImportBuilder(), "", CollisionMode.Default);
                    order.Add(key);
                }
                if (tool == null)
                {
                    string name = extras?["chip_mesh"]?.GetValue<string>() is { Length: > 0 } cm ? cm
                        : baseName.Length > 0 ? baseName : mesh["name"]?.GetValue<string>() ?? "imported";
                    if (extras?["chip_collision"]?.GetValue<string>() is { } cc && Enum.TryParse<CollisionMode>(cc, out var exact)) collision = exact;
                    group = (group.Builder, name, collision ?? CollisionMode.Default);
                }
                else if (group.Name.Length == 0) group = (group.Builder, baseName.Length > 0 ? baseName : "imported", group.Collision);
                groups[key] = group;
                AddMesh(group.Builder, mesh, world, tool);
            }
            else if (extras?["chip_class"]?.GetValue<string>() is { Length: > 0 } cls)
            {
                Matrix4x4.Decompose(world, out _, out var rot, out var pos);
                var e = new Entity
                {
                    // The id it had in the exporting level, so references between imported entities can follow
                    // (SceneOps.AddEntities gives it a fresh one).
                    Id = extras["chip_id"] is JsonValue idValue && idValue.TryGetValue<int>(out var chipId) ? chipId : 0,
                    ClassName = cls, Name = extras["chip_name"]?.GetValue<string>() ?? "",
                    Position = pos * scale, Rotation = EntityEuler(rot),
                };
                if (extras["properties"] is JsonObject props)
                    foreach (var (k, v) in props)
                        e.Properties[k] = v == null ? EntityValues.Null : JsonSerializer.SerializeToElement(v);
                entities.Add(e);
                owner = e; // mesh children are this entity's meshes
            }
            if (node["children"] is JsonArray children)
                foreach (var c in children) if (c?.GetValue<int>() is int ci && ci >= 0 && ci < nodes.Count) Visit(ci, world, depth + 1, owner);
        }

        void AddMesh(ImportBuilder builder, JsonObject mesh, Matrix4x4 world, string? tool)
        {
            bool flip = world.GetDeterminant() < 0;
            Matrix4x4.Invert(world, out var inv);
            var normalMatrix = Matrix4x4.Transpose(inv);
            foreach (var p in mesh["primitives"]?.AsArray() ?? [])
            {
                if (p is not JsonObject prim) continue;
                int mode = prim["mode"]?.GetValue<int>() ?? 4;
                if (mode is not (4 or 5 or 6)) { warnings.Add($"Skipped a primitive drawn as points or lines (mode {mode})"); continue; }
                var attrs = prim["attributes"] as JsonObject;
                if (attrs?["POSITION"]?.GetValue<int>() is not int posAcc) continue;
                try
                {
                    var pos = reader.Floats(posAcc, 3);
                    var uv = attrs["TEXCOORD_0"]?.GetValue<int>() is int uvAcc ? reader.Floats(uvAcc, 2) : null;
                    var nrm = attrs["NORMAL"]?.GetValue<int>() is int nAcc ? reader.Floats(nAcc, 3) : null;
                    int count = pos.Length / 3;
                    var idx = prim["indices"]?.GetValue<int>() is int iAcc ? reader.Indices(iAcc) : Enumerable.Range(0, count).ToArray();
                    string material = MaterialName(prim["material"]?.GetValue<int>(), tool);
                    var welded = new int[count];
                    for (int i = 0; i < count; i++)
                        welded[i] = builder.AddVertex(Vector3.Transform(new Vector3(pos[i * 3], pos[i * 3 + 1], pos[i * 3 + 2]), world) * scale);
                    foreach (var (a, b, c) in Triangles(idx, mode))
                    {
                        if (a >= count || b >= count || c >= count) continue;
                        int[] t = flip ? [c, b, a] : [a, b, c];
                        builder.AddPolygon(t.Select(i => welded[i]).ToArray(), material,
                            uv == null ? null : t.Select(i => new Vector2(uv[i * 2], uv[i * 2 + 1])).ToArray(),
                            nrm == null ? null : t.Select(i => SafeNormal(Vector3.TransformNormal(new Vector3(nrm[i * 3], nrm[i * 3 + 1], nrm[i * 3 + 2]), normalMatrix))).ToArray());
                    }
                }
                catch (Exception e) when (e is NotSupportedException or InvalidDataException or IndexOutOfRangeException or ArgumentException)
                {
                    warnings.Add($"Skipped a primitive: {e.Message}");
                }
            }
        }

        // Roots of the default scene, else every node nobody parents.
        var rootsNode = root["scenes"]?[root["scene"]?.GetValue<int>() ?? 0]?["nodes"]?.AsArray();
        IEnumerable<int> roots;
        if (rootsNode != null) roots = rootsNode.Select(n => n!.GetValue<int>());
        else
        {
            var children = nodes.SelectMany(n => n?["children"]?.AsArray().Select(c => c!.GetValue<int>()) ?? []).ToHashSet();
            roots = Enumerable.Range(0, nodes.Count).Where(i => !children.Contains(i));
        }
        foreach (var r in roots) if (r >= 0 && r < nodes.Count) Visit(r, Matrix4x4.Identity, 0, null);

        var result = new ImportResult(new(), entities, warnings);
        var built = new Dictionary<string, PolyMesh>();
        foreach (var key in order)
        {
            var g = groups[key];
            if (g.Builder.PolygonCount == 0) continue;
            var m = g.Builder.Build(g.Name, materials);
            m.Collision = g.Collision;
            result.Meshes.Add(m);
            built[key] = m;
        }
        foreach (var (e, keys) in ownedKeys)
            if (keys.Where(built.ContainsKey).Select(k => built[k]).ToList() is { Count: > 0 } owned) result.EntityMeshes[e] = owned;
        for (int i = 0; i < (gltfMaterials?.Count ?? 0); i++)
        {
            string name = MaterialName(i, null);
            if (name == fallbackMaterial || result.Images.ContainsKey(name) || gltfMaterials![i] is not JsonObject mat) continue;
            try { if (reader.BaseColorImage(mat) is { } image) result.Images[name] = image; }
            catch (Exception e) when (e is InvalidDataException or NotSupportedException or FormatException or IOException or InvalidOperationException)
            {
                warnings.Add($"Couldn't read the texture of material {name}: {e.Message}");
            }
        }
        return result;
    }

    /// <summary>Splits Godot's import suffix off a node name: <c>-col</c>, <c>-convcol</c>, <c>-colonly</c>, <c>-convcolonly</c>.</summary>
    public static (string Name, CollisionMode? Mode) StripCollisionSuffix(string name)
    {
        foreach (var (suffix, mode) in new[] { ("-convcolonly", CollisionMode.ConvexOnly), ("-colonly", CollisionMode.Only), ("-convcol", CollisionMode.Default), ("-col", CollisionMode.Default) })
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return (name[..^suffix.Length], mode);
        return (name, null);
    }

    /// <summary>Inverse of <see cref="GlbExporter.EntityRotation"/>: Euler degrees (yaw Y, pitch X, roll Z).</summary>
    public static Vector3 EntityEuler(Quaternion q)
    {
        var m = Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(q));
        // Column-vector R = Ry Rx Rz; System.Numerics stores its transpose, so R[i][j] = M(j+1)(i+1).
        float pitch = MathF.Asin(Math.Clamp(-m.M32, -1f, 1f));
        float yaw, roll;
        if (MathF.Abs(m.M32) < 0.9999f)
        {
            yaw = MathF.Atan2(m.M31, m.M33);
            roll = MathF.Atan2(m.M12, m.M22);
        }
        else
        {
            yaw = MathF.Atan2(-m.M13, m.M11); // gimbal lock: fold roll into yaw
            roll = 0;
        }
        const float k = 180f / MathF.PI;
        return new Vector3(pitch * k, yaw * k, roll * k);
    }

    static Vector3 SafeNormal(Vector3 n) => n.LengthSquared() > 1e-12f ? Vector3.Normalize(n) : Vector3.UnitY;

    static IEnumerable<(int, int, int)> Triangles(int[] idx, int mode)
    {
        switch (mode)
        {
            case 4:
                for (int i = 0; i + 2 < idx.Length; i += 3) yield return (idx[i], idx[i + 1], idx[i + 2]);
                break;
            case 5: // strip: every other triangle is flipped to keep the winding
                for (int i = 0; i + 2 < idx.Length; i++)
                    yield return i % 2 == 0 ? (idx[i], idx[i + 1], idx[i + 2]) : (idx[i + 1], idx[i], idx[i + 2]);
                break;
            case 6:
                for (int i = 1; i + 1 < idx.Length; i++) yield return (idx[0], idx[i], idx[i + 1]);
                break;
        }
    }

    static Matrix4x4 LocalMatrix(JsonObject node)
    {
        if (node["matrix"] is JsonArray m && m.Count == 16)
        {
            var f = m.Select(x => x!.GetValue<float>()).ToArray();
            // glTF is column-major for column vectors, which reads straight into System.Numerics' row-vector layout.
            return new Matrix4x4(f[0], f[1], f[2], f[3], f[4], f[5], f[6], f[7], f[8], f[9], f[10], f[11], f[12], f[13], f[14], f[15]);
        }
        var t = node["translation"] is JsonArray ta ? new Vector3(ta[0]!.GetValue<float>(), ta[1]!.GetValue<float>(), ta[2]!.GetValue<float>()) : Vector3.Zero;
        var r = node["rotation"] is JsonArray ra ? new Quaternion(ra[0]!.GetValue<float>(), ra[1]!.GetValue<float>(), ra[2]!.GetValue<float>(), ra[3]!.GetValue<float>()) : Quaternion.Identity;
        var s = node["scale"] is JsonArray sa ? new Vector3(sa[0]!.GetValue<float>(), sa[1]!.GetValue<float>(), sa[2]!.GetValue<float>()) : Vector3.One;
        return Matrix4x4.CreateScale(s) * Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(r)) * Matrix4x4.CreateTranslation(t);
    }

    /// <summary>Accessor reads over the file's buffers (strided views, normalized integers).</summary>
    sealed class Reader(JsonNode root, byte[]? glbBin, string? baseDir)
    {
        readonly Dictionary<int, byte[]> _buffers = new();

        byte[] Buffer(int index)
        {
            if (_buffers.TryGetValue(index, out var b)) return b;
            var buf = root["buffers"]?[index] ?? throw new InvalidDataException($"Buffer {index} is missing");
            var uri = buf["uri"]?.GetValue<string>();
            if (uri == null) b = glbBin ?? throw new InvalidDataException("The file has no binary chunk");
            else if (uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                int comma = uri.IndexOf(',');
                if (comma < 0 || !uri[..comma].EndsWith(";base64", StringComparison.OrdinalIgnoreCase)) throw new NotSupportedException("Only base64 data URIs are supported");
                b = Convert.FromBase64String(uri[(comma + 1)..]);
            }
            else
            {
                if (baseDir == null) throw new InvalidDataException($"Can't resolve external buffer {uri}");
                var file = Path.Combine(baseDir, Uri.UnescapeDataString(uri));
                if (!File.Exists(file)) throw new InvalidDataException($"Buffer file {uri} not found next to the glTF");
                b = File.ReadAllBytes(file);
            }
            return _buffers[index] = b;
        }

        /// <summary>The material's base colour image (PNG or JPEG), or null when it has none.</summary>
        public ImportedImage? BaseColorImage(JsonObject material)
        {
            if (material["pbrMetallicRoughness"]?["baseColorTexture"]?["index"]?.GetValue<int>() is not int texture
                || root["textures"]?[texture]?["source"]?.GetValue<int>() is not int source
                || root["images"]?[source] is not JsonObject image) return null;
            string? mime = image["mimeType"]?.GetValue<string>();
            string? uri = image["uri"]?.GetValue<string>();
            byte[] data;
            if (image["bufferView"]?.GetValue<int>() is int viewIndex)
            {
                var view = root["bufferViews"]![viewIndex]!;
                var buffer = Buffer(view["buffer"]!.GetValue<int>());
                int offset = view["byteOffset"]?.GetValue<int>() ?? 0, length = view["byteLength"]!.GetValue<int>();
                if (offset + length > buffer.Length) throw new InvalidDataException("An image runs past its buffer");
                data = buffer.AsSpan(offset, length).ToArray();
            }
            else if (uri != null && uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                int comma = uri.IndexOf(',');
                if (comma < 0 || !uri[..comma].EndsWith(";base64", StringComparison.OrdinalIgnoreCase)) return null;
                mime ??= uri[5..uri.IndexOf(';')];
                data = Convert.FromBase64String(uri[(comma + 1)..]);
            }
            else if (uri != null && baseDir != null && File.Exists(Path.Combine(baseDir, Uri.UnescapeDataString(uri))))
                data = File.ReadAllBytes(Path.Combine(baseDir, Uri.UnescapeDataString(uri)));
            else return null;
            string ext = mime?.ToLowerInvariant() switch
            {
                "image/png" => ".png",
                "image/jpeg" or "image/jpg" => ".jpg",
                _ => uri != null && !uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ? Path.GetExtension(uri).ToLowerInvariant() : "",
            };
            return ext is ".png" or ".jpg" or ".jpeg" ? new ImportedImage(data, ext == ".jpeg" ? ".jpg" : ext) : null;
        }

        (byte[] Data, int Offset, int Stride, int Count, int ComponentType, bool Normalized) Layout(int accessor, int components)
        {
            var acc = root["accessors"]?[accessor] ?? throw new InvalidDataException($"Accessor {accessor} is missing");
            if (acc["sparse"] != null) throw new NotSupportedException("sparse accessors aren't supported");
            int count = acc["count"]!.GetValue<int>();
            int type = acc["componentType"]!.GetValue<int>();
            int size = type switch { 5120 or 5121 => 1, 5122 or 5123 => 2, 5125 or 5126 => 4, _ => throw new InvalidDataException($"Unknown component type {type}") };
            if (acc["bufferView"]?.GetValue<int>() is not int viewIndex) return (new byte[count * components * size], 0, components * size, count, type, false);
            var view = root["bufferViews"]![viewIndex]!;
            var data = Buffer(view["buffer"]!.GetValue<int>());
            int offset = (view["byteOffset"]?.GetValue<int>() ?? 0) + (acc["byteOffset"]?.GetValue<int>() ?? 0);
            int stride = view["byteStride"]?.GetValue<int>() ?? components * size;
            if (count > 0 && offset + stride * (count - 1) + components * size > data.Length) throw new InvalidDataException("An accessor runs past its buffer");
            return (data, offset, stride, count, type, acc["normalized"]?.GetValue<bool>() ?? false);
        }

        public float[] Floats(int accessor, int components)
        {
            var (data, offset, stride, count, type, normalized) = Layout(accessor, components);
            var result = new float[count * components];
            for (int i = 0; i < count; i++)
                for (int c = 0; c < components; c++)
                {
                    var s = data.AsSpan(offset + i * stride);
                    result[i * components + c] = type switch
                    {
                        5126 => BinaryPrimitives.ReadSingleLittleEndian(s[(c * 4)..]),
                        5121 => s[c] / (normalized ? 255f : 1f),
                        5120 => normalized ? MathF.Max((sbyte)s[c] / 127f, -1) : (sbyte)s[c],
                        5123 => BinaryPrimitives.ReadUInt16LittleEndian(s[(c * 2)..]) / (normalized ? 65535f : 1f),
                        5122 => normalized ? MathF.Max(BinaryPrimitives.ReadInt16LittleEndian(s[(c * 2)..]) / 32767f, -1) : BinaryPrimitives.ReadInt16LittleEndian(s[(c * 2)..]),
                        _ => BinaryPrimitives.ReadUInt32LittleEndian(s[(c * 4)..]),
                    };
                }
            return result;
        }

        public int[] Indices(int accessor)
        {
            var (data, offset, stride, count, type, _) = Layout(accessor, 1);
            var result = new int[count];
            for (int i = 0; i < count; i++)
            {
                var s = data.AsSpan(offset + i * stride);
                result[i] = type switch
                {
                    5121 => s[0],
                    5123 => BinaryPrimitives.ReadUInt16LittleEndian(s),
                    5125 => (int)BinaryPrimitives.ReadUInt32LittleEndian(s),
                    _ => throw new InvalidDataException($"Index component type {type} isn't allowed"),
                };
            }
            return result;
        }
    }
}
