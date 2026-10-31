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
    /// <summary>Entity values left out of the file, and why.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>
/// Writes a glTF 2.0 binary. One node per mesh at the world origin (stable across re-exports), or, for a mesh with its own
/// <see cref="ObjectOrigin"/>, at that origin (translation, rotation) with its vertices relative to it; one primitive per material,
/// per-corner normals (flat, or smoothed across soft edges unless <see cref="ExportSettings.Smoothing"/> is off), explicit
/// UVs where present else the face projection, and material images embedded as PNG (or referenced, see
/// <see cref="ExportSettings.EmbedTextures"/>). Colliding nodes carry <c>extras.chip_collider</c> (<c>trimesh</c> or
/// <c>convex</c>) and, when they aren't drawn, <c>extras.chip_render = false</c>. When the project wants them
/// (<see cref="GameProject.CollisionSuffixes"/>, Godot's, and the default outside a project) node names also get Godot's
/// import suffixes: <c>-col</c> (render + trimesh collider, from <see cref="Document.ExportCollision"/> or
/// <see cref="CollisionMode.Default"/>), <c>-colonly</c> and <c>-convcolonly</c> (<see cref="CollisionMode.ConvexOnly"/>).
/// Collision-only nodes carry no material.
/// <see cref="Surfaces"/> (tool materials and the game's own) split a mesh's faces: unseen solid faces go to a
/// <c>name_surface-colonly</c> collider, volume faces to a non-solid <c>name_surface-colonly</c> node with
/// <c>extras.chip_volume</c>, faces that render but never collide to their own <c>name_surface</c> node; each carries
/// <c>extras.chip_surface</c> (the built-in clip and trigger also <c>chip_tool</c>). Rendered surfaces' materials carry
/// <c>extras.chip_surface</c> too.
/// Mesh nodes carry <c>extras.chip_mesh</c>/<c>chip_id</c> and nonempty mesh tags as <c>chip_tags</c> (also on their surface
/// nodes). <c>chip_remap</c> maps material names to engine paths for the remaps they use; remapped materials carry
/// <c>extras.chip_path</c>. Entities
/// follow as empty nodes (translation, rotation) with <c>extras = { chip_class, chip_id, chip_uid?, chip_scene?,
/// chip_name?, properties }</c>: the class's engine scene, and the values the entity sets, typed by its class. A mesh
/// entity's meshes are its node's children, in its space, never baked into world collision and never suffixed.
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
        var warnings = new List<string>();
        float s = doc.ExportScale;
        bool suffixes = EntityClasses.Project?.CollisionSuffixes ?? true;
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
            if (Surfaces.Of(name)?.Name != "sky" && ExportImages.Resolve(info, outputPath, opt.EmbedTextures) is { } image)
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
            if (pbr.ContainsKey("baseColorTexture"))
                switch (materials.AlphaOf(name))
                {
                    case AlphaMode.Mask: mat["alphaMode"] = "MASK"; mat["alphaCutoff"] = MaterialAlpha.Cutoff; break;
                    case AlphaMode.Blend: mat["alphaMode"] = "BLEND"; break;
                }
            if (info.DoubleSided) mat["doubleSided"] = true;
            var matExtras = new JsonObject();
            if (opt.Remap(name) is { } path) matExtras["chip_path"] = path;
            if (Surfaces.Of(name) is { } surface) matExtras["chip_surface"] = surface.Name; // a rendered surface (a screen) the game swaps
            if (matExtras.Count > 0) mat["extras"] = matExtras;
            gltfMaterials.Add(mat);
            return materialIndex[name] = gltfMaterials.Count - 1;
        }

        var tri = new List<int>();
        var bakeSources = new List<CollisionSource>();
        int tessBefore = 0, tessAfter = 0;
        bool ExportsEntity(Entity e) => (!e.Hidden || opt.IncludeHidden) && ReferenceModels.Exports(e); // reference models are editor-only unless exported as markers
        // Mesh entities' meshes become children of the entity's node, in its space (they move with it in the game).
        var ownerOf = new Dictionary<int, Entity>();
        foreach (var e in doc.Entities.Where(e => e.IsMeshEntity && ExportsEntity(e)))
            foreach (var id in e.Meshes) ownerOf.TryAdd(id, e);
        var children = new Dictionary<int, List<int>>();
        var place = Matrix4x4.Identity; // world to the space the current node's vertices are written in
        Entity? owner = null;
        ObjectOrigin? nodeOrigin = null; // the current mesh's own origin, which its nodes are placed at
        foreach (var mesh in doc.Meshes)
        {
            if (mesh.Hidden && !opt.IncludeHidden) continue;
            owner = ownerOf.GetValueOrDefault(mesh.Id);
            place = Matrix4x4.Identity;
            // A mesh entity's meshes are in its space; another mesh with an origin is written relative to that origin.
            nodeOrigin = owner == null ? mesh.Origin : null;
            if (owner != null) Matrix4x4.Invert(EntityOps.World(owner), out place);
            else if (nodeOrigin is { } o) place = o.Local;
            var cornerNormals = opt.Smoothing ? Topology.CornerNormals(mesh) : FlatNormals(mesh);
            bool collisionOnly = mesh.Collision is CollisionMode.Only or CollisionMode.ConvexOnly;
            bool collides = collisionOnly || (mesh.Collision == CollisionMode.Default && doc.ExportCollision);
            // Default meshes' colliders are baked into world collision; the render node then doesn't collide itself.
            // A mesh entity's geometry moves, so it keeps its own collider.
            bool baked = opt.BakeCollision && !collisionOnly && collides && owner == null;
            var faces = new List<int>();
            var nodrawFaces = new List<int>();
            // Per surface: faces that render without colliding, solid unseen faces, and volume faces.
            var passFaces = new Dictionary<Surface, List<int>>();
            var solidFaces = new Dictionary<Surface, List<int>>();
            var volumeFaces = new Dictionary<Surface, List<int>>();
            static void Put(Dictionary<Surface, List<int>> map, Surface s, int f)
            {
                if (!map.TryGetValue(s, out var list)) map[s] = list = new List<int>();
                list.Add(f);
            }
            for (int f = 0; f < mesh.Faces.Count; f++)
            {
                if (Surfaces.Of(mesh.Faces[f].Material) is not { } sf) { faces.Add(f); continue; }
                if (sf.Skip) continue;
                if (sf.Volume) Put(volumeFaces, sf, f);
                if (collisionOnly) { if (sf.Collide != SurfaceCollide.None) faces.Add(f); continue; } // the whole mesh is collision already
                if (sf.Render)
                {
                    if (sf.Collide == SurfaceCollide.None) Put(passFaces, sf, f);
                    else faces.Add(f);
                    if (sf.Collide == SurfaceCollide.Always && !collides) Put(solidFaces, sf, f);
                }
                else if (sf.Collide == SurfaceCollide.Always) Put(solidFaces, sf, f);
                else if (sf.Collide == SurfaceCollide.Mesh && collides)
                {
                    if (baked) nodrawFaces.Add(f); // solid but unseen, so it collides with the rest
                    else Put(solidFaces, sf, f);
                }
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
            if (!mesh.Tags.IsEmpty) meshExtras["chip_tags"] = MeshTags(mesh);
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
            // Surface nodes: chip_surface names the surface; the built-in clip and trigger also keep chip_tool.
            JsonObject SurfaceExtras(Surface sf)
            {
                var x = new JsonObject { ["chip_surface"] = sf.Name, ["chip_id"] = mesh.Id };
                if (!mesh.Tags.IsEmpty) x["chip_tags"] = MeshTags(mesh);
                if (sf.Name is "clip" or "trigger") x["chip_tool"] = sf.Name;
                return x;
            }
            foreach (var (sf, list) in passFaces)
                AddNode($"{name}_{sf.Name}", "", Primitives(mesh, cornerNormals, list, withMaterial: true), SurfaceExtras(sf));
            foreach (var (sf, list) in solidFaces)
                AddNode($"{name}_{sf.Name}", "-colonly", Primitives(mesh, cornerNormals, list, withMaterial: false), SurfaceExtras(sf));
            foreach (var (sf, list) in volumeFaces)
            {
                var x = SurfaceExtras(sf);
                x["chip_volume"] = true;
                AddNode($"{name}_{sf.Name}", "-colonly", Primitives(mesh, cornerNormals, list, withMaterial: false), x);
            }
            if (nodes.Count > before) meshCount++;
        }
        owner = null;
        nodeOrigin = null;
        place = Matrix4x4.Identity;

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

        var exportedIds = doc.Entities.Where(ExportsEntity).Select(e => e.Id).ToHashSet();
        foreach (var e in doc.Entities)
        {
            if (!ExportsEntity(e)) continue;
            var node = new JsonObject { ["name"] = EntityNodeName(e) };
            var t = e.Position * s;
            if (t != Vector3.Zero) node["translation"] = new JsonArray(t.X, t.Y, t.Z);
            var q = EntityRotation(e.Rotation);
            if (q != Quaternion.Identity) node["rotation"] = new JsonArray(q.X, q.Y, q.Z, q.W);
            var cls = EntityClasses.Get(e.ClassName);
            var extras = new JsonObject { ["chip_class"] = e.ClassName, ["chip_id"] = e.Id };
            if (cls.Uid != null) extras["chip_uid"] = cls.Uid;
            if (cls.Scene != null) extras["chip_scene"] = cls.Scene;
            if (e.Name.Length > 0) extras["chip_name"] = e.Name;
            extras["properties"] = EntityProperties(e, cls, exportedIds, warnings);
            if (!e.Tags.IsEmpty) extras["chip_tags"] = new JsonArray(e.Tags.Select(t => (JsonNode)JsonValue.Create(t)!).ToArray());
            var io = new JsonArray();
            foreach (var c in e.Outputs)
                if (c.Target.Kind == TargetKind.Entity && !exportedIds.Contains(c.Target.Entity)) warnings.Add($"{SceneOps.DisplayName(e)}: connection {c.On} points at an entity that isn't exported; left out");
                else io.Add(c.ToJson(defaults: true));
            if (io.Count > 0) extras["chip_io"] = io;
            node["extras"] = extras;
            if (children.TryGetValue(e.Id, out var kids)) node["children"] = new JsonArray(kids.Select(k => (JsonNode)k).ToArray());
            nodes.Add(node);
            entityCount++;
        }

        // Every collider says what it is in extras; Godot projects also get the name suffix its importer reads (but not
        // under a mesh entity, whose scene instance takes the shapes itself).
        void AddNode(string name, string suffix, JsonArray primitives, JsonObject extras)
        {
            if (primitives.Count == 0) return;
            // A volume (a trigger) is not a wall: it says what it is, so it never claims to collide.
            bool volume = extras["chip_volume"]?.GetValue<bool>() == true;
            if (suffix.Length > 0 && !volume) extras["chip_collider"] = suffix.StartsWith("-conv", StringComparison.Ordinal) ? "convex" : "trimesh";
            if (suffix.EndsWith("only", StringComparison.Ordinal)) extras["chip_render"] = false;
            meshes.Add(new JsonObject { ["name"] = name, ["primitives"] = primitives });
            var node = new JsonObject { ["name"] = suffixes && owner == null ? name + suffix : name, ["mesh"] = meshes.Count - 1, ["extras"] = extras };
            if (nodeOrigin is { } o)
            {
                var t = o.Position * s;
                if (t != Vector3.Zero) node["translation"] = new JsonArray(t.X, t.Y, t.Z);
                if (o.Rotation != Quaternion.Identity) node["rotation"] = new JsonArray(o.Rotation.X, o.Rotation.Y, o.Rotation.Z, o.Rotation.W);
            }
            nodes.Add(node);
            if (owner != null)
            {
                if (!children.TryGetValue(owner.Id, out var list)) children[owner.Id] = list = new List<int>();
                list.Add(nodes.Count - 1);
            }
        }

        // Positions and triangles only: the engine builds the shape from them and drops the mesh.
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
                        pos.Add(Vector3.Transform(p, place) * s);
                        nrm.Add(Vector3.Normalize(Vector3.TransformNormal(cornerNormals[f][i], place)));
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
            ["scenes"] = new JsonArray(new JsonObject
            {
                ["nodes"] = new JsonArray(Enumerable.Range(0, nodes.Count).Except(children.Values.SelectMany(c => c)).Select(i => (JsonNode)i).ToArray()),
            }),
            ["nodes"] = nodes,
            ["meshes"] = meshes,
            ["materials"] = gltfMaterials,
            ["accessors"] = accessors,
            ["bufferViews"] = bufferViews,
            ["buffers"] = new JsonArray(new JsonObject { ["byteLength"] = Align4(bin.Length) }),
        };
        if (EntityClasses.Project is { Flags.Count: > 0 } project)
        {
            var flags = new JsonObject();
            foreach (var f in project.Flags.Values) flags[f.Name] = new JsonObject { ["type"] = f.Type.ToString().ToLowerInvariant(), ["default"] = JsonNode.Parse(f.Default.GetRawText()), ["scope"] = f.Scope.ToString().ToLowerInvariant() };
            root["scenes"]![0]!["extras"] = new JsonObject { ["chip_flags"] = flags };
        }
        if (EntityClasses.Current.World != null || doc.World.Count > 0)
        {
            var cls = EntityClasses.Current.World ?? EntityClasses.Generic("");
            var scene = root["scenes"]![0]!;
            scene["extras"] ??= new JsonObject();
            scene["extras"]!["chip_world"] = new JsonObject
            {
                ["chip_class"] = cls.Name,
                ["properties"] = EntityProperties(new Entity { Properties = doc.World }, cls, exportedIds, warnings),
            };
        }
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
            Warnings = warnings,
            Collision = collisionStats,
            Tessellated = opt.Tessellate ? (tessBefore, tessAfter) : null,
        };
    }

    static JsonArray MeshTags(PolyMesh mesh) => new(mesh.Tags.Select(t => (JsonNode)JsonValue.Create(t)!).ToArray());

    /// <summary>
    /// The values the entity sets, typed by its class (a v2 string "10" for a float goes out as 10). Values the class
    /// can't read, and references to entities that aren't exported, are left out with a warning, so the engine keeps
    /// the scene's value (a list just loses those items). Properties the class doesn't declare go out as they are.
    /// </summary>
    static JsonObject EntityProperties(Entity e, EntityClass cls, HashSet<int> exportedIds, List<string> warnings)
    {
        var props = new JsonObject();
        foreach (var (k, v) in e.Properties.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var value = v;
            if (cls.Prop(k) is { } spec)
            {
                if (EntityValues.Coerce(spec, v, out var error) is not { } typed)
                {
                    warnings.Add($"{SceneOps.DisplayName(e)}: {error}; left out");
                    continue;
                }
                value = typed;
            }
            var mapped = EntityValues.MapRefs(value, id => exportedIds.Contains(id) ? id : null, out int dropped);
            if (mapped is not { } exported)
            {
                warnings.Add($"{SceneOps.DisplayName(e)}: {k} points at an entity that isn't exported; left out");
                continue;
            }
            if (dropped > 0)
                warnings.Add($"{SceneOps.DisplayName(e)}: {dropped} of {k}'s entities {(dropped == 1 ? "isn't" : "aren't")} exported; left out of the list");
            props[k] = JsonNode.Parse(exported.GetRawText());
        }
        return props;
    }

    /// <summary>The entity's node name: its name, else <c>class_id</c> (a reference model's marker: <c>model_id</c>).</summary>
    public static string EntityNodeName(Entity e) =>
        e.Name.Length > 0 ? e.Name
        : ReferenceModels.Is(e) && ReferenceModels.DisplayName(ReferenceModels.ModelOf(e)) is { Length: > 0 } model ? $"{model}_{e.Id}"
        : $"{e.ClassName}_{e.Id}";

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
