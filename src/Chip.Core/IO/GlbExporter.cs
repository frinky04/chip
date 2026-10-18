using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using Chip.Core.Geometry;
using Chip.Core.Scene;

namespace Chip.Core.IO;

/// <summary>What an export produced: the main file's bytes, files to write beside it, and counts for the status bar.</summary>
public sealed record ExportResult(byte[] Data, IReadOnlyList<SideFile> SideFiles, int Meshes, int Entities)
{
    /// <summary>Baked world collision, when <see cref="ExportSettings.BakeCollision"/> ran.</summary>
    public CollisionBakeStats? Collision { get; init; }
    /// <summary>Render triangles before and after tessellation, when <see cref="ExportSettings.Tessellate"/> ran.</summary>
    public (int Before, int After)? Tessellated { get; init; }
}

/// <summary>
/// Writes a glTF 2.0 binary. One node per mesh at the world origin (stable across re-exports), one primitive per material,
/// per-corner normals (flat, or smoothed across soft edges unless <see cref="ExportSettings.Smoothing"/> is off), explicit
/// UVs where present else the face projection, and material images embedded as PNG (or referenced, see
/// <see cref="ExportSettings.EmbedTextures"/>). Collision follows Godot's import suffixes: <c>-col</c> (render + trimesh
/// collider, from <see cref="Document.ExportCollision"/> or <see cref="CollisionMode.Default"/>), <c>-colonly</c> and
/// <c>-convcolonly</c> (<see cref="CollisionMode.ConvexOnly"/>). Collision-only nodes carry no material.
/// Tool materials (<see cref="ToolMaterial"/>): nodraw and skip faces never render; clip faces, and nodraw faces of a
/// colliding mesh, go to a <c>name_clip-colonly</c> node (<c>extras.chip_tool = "clip"</c>); trigger faces go to
/// <c>name_trigger-colonly</c> with <c>extras.chip_tool = "trigger"</c>.
/// Mesh nodes carry <c>extras.chip_mesh</c>/<c>chip_id</c> (and <c>chip_remap</c>, material name to path, for the remaps
/// they use); remapped materials carry <c>extras.godot_path</c>. Entities
/// follow as empty nodes (translation, rotation) with <c>extras = { chip_class, chip_id, chip_name?, properties }</c>.
/// </summary>
public static class GlbExporter
{
    /// <summary>Builds and writes the file (plus any referenced texture copies beside it).</summary>
    public static ExportResult Export(Document doc, MaterialLibrary materials, string path)
    {
        var result = Build(doc, materials, path);
        ExportImages.WriteAll(path, result.SideFiles);
        AtomicFile.Write(path, result.Data);
        return result;
    }

    /// <summary>The GLB bytes, with textures always embedded (no output path to reference from).</summary>
    public static byte[] Build(Document doc, MaterialLibrary materials) => Build(doc, materials, null).Data;

    public static ExportResult Build(Document doc, MaterialLibrary materials, string? outputPath)
    {
        var opt = doc.Export;
        var bin = new MemoryStream();
        var bufferViews = new JsonArray();
        var accessors = new JsonArray();
        var materialIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var gltfMaterials = new JsonArray();
        var images = new JsonArray();
        var textures = new JsonArray();
        var meshes = new JsonArray();
        var nodes = new JsonArray();
        var sideFiles = new List<SideFile>();
        float s = doc.ExportScale;
        int meshCount = 0, entityCount = 0;

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
            if (ExportImages.Resolve(info, outputPath, opt.EmbedTextures) is { } image)
            {
                var img = new JsonObject { ["mimeType"] = image.MimeType, ["name"] = info.Name };
                if (image.Embedded != null) img["bufferView"] = View(image.Embedded, null);
                else img["uri"] = ExportImages.ToUri(image.RelativePath!);
                if (image.Copy != null) sideFiles.Add(image.Copy);
                images.Add(img);
                textures.Add(new JsonObject { ["source"] = images.Count - 1, ["sampler"] = 0 });
                pbr["baseColorTexture"] = new JsonObject { ["index"] = textures.Count - 1 };
            }
            else pbr["baseColorFactor"] = new JsonArray(1f, 0f, 1f, 1f);
            var mat = new JsonObject { ["name"] = name, ["pbrMetallicRoughness"] = pbr };
            if (opt.Remap(name) is { } path) mat["extras"] = new JsonObject { ["godot_path"] = path };
            gltfMaterials.Add(mat);
            return materialIndex[name] = gltfMaterials.Count - 1;
        }

        var tri = new List<int>();
        var bakeSources = new List<CollisionSource>();
        int tessBefore = 0, tessAfter = 0;
        foreach (var mesh in doc.Meshes)
        {
            if (mesh.Hidden && !opt.IncludeHidden) continue;
            var cornerNormals = opt.Smoothing ? Topology.CornerNormals(mesh) : FlatNormals(mesh);
            bool collisionOnly = mesh.Collision is CollisionMode.Only or CollisionMode.ConvexOnly;
            bool collides = collisionOnly || (mesh.Collision == CollisionMode.Default && doc.ExportCollision);
            // Default meshes' colliders are baked into world collision; the render node then doesn't collide itself.
            bool baked = opt.BakeCollision && !collisionOnly && collides;
            var faces = new List<int>();
            var clipFaces = new List<int>();
            var triggerFaces = new List<int>();
            var nodrawFaces = new List<int>();
            for (int f = 0; f < mesh.Faces.Count; f++)
            {
                var tool = MaterialLibrary.ToolKind(mesh.Faces[f].Material);
                var target = tool switch
                {
                    ToolMaterial.None => faces,
                    ToolMaterial.Trigger => triggerFaces,
                    ToolMaterial.Skip => null,
                    _ when collisionOnly => faces, // the whole mesh is collision already
                    ToolMaterial.Clip => clipFaces,
                    _ when baked => nodrawFaces, // nodraw: solid but unseen, so it collides with the rest
                    _ => collides ? clipFaces : null, // nodraw
                };
                target?.Add(f);
            }
            if (baked) bakeSources.Add(new CollisionSource(mesh, [.. faces, .. nodrawFaces], AllowConvex: opt.CollisionHulls));

            string name = $"{mesh.Name}_{mesh.Id}";
            string suffix = mesh.Collision switch
            {
                CollisionMode.Only => "-colonly",
                CollisionMode.ConvexOnly => "-convcolonly",
                CollisionMode.None => "",
                _ => doc.ExportCollision && !baked && !opt.Tessellate ? "-col" : "",
            };
            int before = nodes.Count;
            var meshExtras = new JsonObject { ["chip_mesh"] = mesh.Name, ["chip_id"] = mesh.Id };
            // The suffix can't tell None from Default when the document doesn't collide; the extra can.
            if (mesh.Collision != CollisionMode.Default) meshExtras["chip_collision"] = mesh.Collision.ToString();
            if (!collisionOnly)
            {
                // The same remaps per node, keyed by material name: a fallback for importers that drop material extras.
                var remap = new JsonObject();
                foreach (var m in faces.Select(f => mesh.Faces[f].Material).Distinct(StringComparer.OrdinalIgnoreCase))
                    if (opt.Remap(m) is { } path) remap[m] = path;
                if (remap.Count > 0) meshExtras["chip_remap"] = remap;
            }
            // Tessellation (per-vertex lighting) only changes what renders; colliders always come from the original faces.
            if (opt.Tessellate && !collisionOnly && faces.Count > 0)
            {
                var t = Tessellator.Tessellate(mesh, faces, cornerNormals, new TessellateOptions(opt.TessellateSpacing));
                tessBefore += faces.Sum(f => Math.Max(0, mesh.Faces[f].Verts.Length - 2));
                tessAfter += t.TriangleCount;
                AddNode(name, suffix, Primitives(t.Mesh, t.CornerNormals, Enumerable.Range(0, t.Mesh.Faces.Count).ToList(), withMaterial: true), meshExtras);
                if (collides && !baked)
                    AddNode(name + "_col", "-colonly", Primitives(mesh, cornerNormals, faces, withMaterial: false), new JsonObject { ["chip_baked"] = true });
            }
            else AddNode(name, suffix, Primitives(mesh, cornerNormals, faces, withMaterial: !collisionOnly), meshExtras);
            AddNode(name + "_clip", "-colonly", Primitives(mesh, cornerNormals, clipFaces, withMaterial: false),
                new JsonObject { ["chip_tool"] = "clip", ["chip_id"] = mesh.Id });
            AddNode(name + "_trigger", "-colonly", Primitives(mesh, cornerNormals, triggerFaces, withMaterial: false),
                new JsonObject { ["chip_tool"] = "trigger", ["chip_id"] = mesh.Id });
            if (nodes.Count > before) meshCount++;
        }

        // Baked world collision (Source 2 style): one merged, simplified trimesh in chunks, plus convex hulls if asked.
        CollisionBakeStats? collisionStats = null;
        if (bakeSources.Count > 0)
        {
            var bakedCollision = CollisionBake.Bake(bakeSources, new CollisionBakeOptions());
            collisionStats = bakedCollision.Stats;
            var tag = new Func<JsonObject>(() => new JsonObject { ["chip_baked"] = true });
            for (int i = 0; i < bakedCollision.Chunks.Count; i++)
                AddNode($"chip_world_collision_{i}", "-colonly", CollisionPrimitive(bakedCollision.Chunks[i].Positions, bakedCollision.Chunks[i].Triangles), tag());
            foreach (var hull in bakedCollision.Convex)
                AddNode($"chip_hull_{hull.SourceMeshId}", "-convcolonly", CollisionPrimitive(hull.Points, hull.Triangles), tag());
        }

        foreach (var e in doc.Entities)
        {
            if (e.Hidden && !opt.IncludeHidden) continue;
            var node = new JsonObject { ["name"] = EntityNodeName(e) };
            var t = e.Position * s;
            if (t != Vector3.Zero) node["translation"] = new JsonArray(t.X, t.Y, t.Z);
            var q = EntityRotation(e.Rotation);
            if (q != Quaternion.Identity) node["rotation"] = new JsonArray(q.X, q.Y, q.Z, q.W);
            var props = new JsonObject();
            foreach (var (k, v) in e.Properties.OrderBy(kv => kv.Key, StringComparer.Ordinal)) props[k] = v;
            var extras = new JsonObject { ["chip_class"] = e.ClassName, ["chip_id"] = e.Id };
            if (e.Name.Length > 0) extras["chip_name"] = e.Name;
            extras["properties"] = props;
            node["extras"] = extras;
            nodes.Add(node);
            entityCount++;
        }

        void AddNode(string name, string suffix, JsonArray primitives, JsonObject extras)
        {
            if (primitives.Count == 0) return;
            meshes.Add(new JsonObject { ["name"] = name, ["primitives"] = primitives });
            nodes.Add(new JsonObject { ["name"] = name + suffix, ["mesh"] = meshes.Count - 1, ["extras"] = extras });
        }

        // Positions and triangles only: Godot builds the shape from them and drops the mesh.
        JsonArray CollisionPrimitive(Vector3[] positions, int[] triangles)
        {
            if (triangles.Length == 0) return new JsonArray();
            var pos = positions.Select(p => p * s).ToList();
            var min = pos.Aggregate(new Vector3(float.MaxValue), Vector3.Min);
            var max = pos.Aggregate(new Vector3(float.MinValue), Vector3.Max);
            return new JsonArray(new JsonObject
            {
                ["attributes"] = new JsonObject
                {
                    ["POSITION"] = Accessor(pos, 5126, "VEC3", 34962, new JsonObject { ["min"] = new JsonArray(min.X, min.Y, min.Z), ["max"] = new JsonArray(max.X, max.Y, max.Z) }),
                },
                ["indices"] = Accessor(triangles.Select(t => (uint)t).ToList(), 5125, "SCALAR", 34963),
            });
        }

        JsonArray Primitives(PolyMesh mesh, Vector3[][] cornerNormals, List<int> faces, bool withMaterial)
        {
            var primitives = new JsonArray();
            foreach (var group in faces.GroupBy(f => mesh.Faces[f].Material))
            {
                var info = materials.Get(group.Key);
                var pos = new List<Vector3>();
                var nrm = new List<Vector3>();
                var uv = new List<Vector2>();
                var idx = new List<uint>();
                foreach (var f in group)
                {
                    var face = mesh.Faces[f];
                    uint baseIndex = (uint)pos.Count;
                    for (int i = 0; i < face.Verts.Length; i++)
                    {
                        var p = mesh.Positions[face.Verts[i]];
                        pos.Add(p * s);
                        nrm.Add(cornerNormals[f][i]);
                        uv.Add(face.UV(i, p, info.Width, info.Height));
                    }
                    tri.Clear();
                    Triangulator.Triangulate(mesh, f, tri);
                    foreach (var t in tri) idx.Add(baseIndex + (uint)t);
                }
                if (idx.Count == 0) continue;
                var min = pos.Aggregate(new Vector3(float.MaxValue), Vector3.Min);
                var max = pos.Aggregate(new Vector3(float.MinValue), Vector3.Max);
                var primitive = new JsonObject
                {
                    ["attributes"] = new JsonObject
                    {
                        ["POSITION"] = Accessor(pos, 5126, "VEC3", 34962, new JsonObject { ["min"] = new JsonArray(min.X, min.Y, min.Z), ["max"] = new JsonArray(max.X, max.Y, max.Z) }),
                        ["NORMAL"] = Accessor(nrm, 5126, "VEC3", 34962),
                        ["TEXCOORD_0"] = Accessor(uv, 5126, "VEC2", 34962),
                    },
                    ["indices"] = Accessor(idx, 5125, "SCALAR", 34963),
                };
                if (withMaterial) primitive["material"] = Material(group.Key);
                primitives.Add(primitive);
            }
            return primitives;
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
        // glTF arrays must not be empty: drop whatever this document didn't use.
        foreach (var key in new[] { "nodes", "meshes", "materials", "accessors", "bufferViews" })
            if (root[key] is JsonArray { Count: 0 }) root.Remove(key);
        if (bin.Length == 0) root.Remove("buffers");
        if (nodes.Count == 0) root["scenes"]![0]!.AsObject().Remove("nodes");

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
        return new ExportResult(outStream.ToArray(), sideFiles, meshCount, entityCount)
        {
            Collision = collisionStats,
            Tessellated = opt.Tessellate ? (tessBefore, tessAfter) : null,
        };
    }

    /// <summary>The entity's node name: its name, else <c>class_id</c>.</summary>
    public static string EntityNodeName(Entity e) => e.Name.Length > 0 ? e.Name : $"{e.ClassName}_{e.Id}";

    /// <summary><see cref="Entity.Rotation"/> (degrees; yaw Y, then pitch X, then roll Z) as a unit quaternion.</summary>
    public static Quaternion EntityRotation(Vector3 degrees)
    {
        const float k = MathF.PI / 180f;
        return Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(degrees.Y * k, degrees.X * k, degrees.Z * k));
    }

    static Vector3[][] FlatNormals(PolyMesh m)
    {
        var result = new Vector3[m.Faces.Count][];
        for (int f = 0; f < m.Faces.Count; f++) result[f] = Enumerable.Repeat(m.FaceNormal(f), m.Faces[f].Verts.Length).ToArray();
        return result;
    }

    static long Align4(long n) => (n + 3) & ~3L;
}
