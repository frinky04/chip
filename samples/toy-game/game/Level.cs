using System.Numerics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ToyGame;

/// <summary>
/// A collision shape in world space: triangles, three corners each. <see cref="Surface"/> names the game surface it was
/// painted with (<c>monster_clip</c>), if any; <see cref="Owner"/> is the mesh entity it belongs to, if any.
/// </summary>
public sealed record Collider(string Node, Vector3[] Triangles, bool Convex, bool Rendered, string? Surface, GameEntity? Owner);

/// <summary>A volume (faces painted with a volume surface such as Chip's trigger), as its world-space box.</summary>
public sealed record Trigger(string Node, string Surface, Vector3 Min, Vector3 Max);

/// <summary>A rendered mesh painted with one of the game's surfaces (the video surface), for the renderer to swap.</summary>
public sealed record SurfaceUse(string Node, string Surface, GameEntity? Owner);

/// <summary>
/// A level exported by Chip, read the way a custom engine would: plain glTF plus the <c>chip_*</c> extras
/// (<c>docs/engines.md</c>). Entity nodes (<c>extras.chip_class</c>) become <see cref="GameEntity"/> instances with
/// their values applied and references resolved, and a mesh entity's child nodes are its geometry; nodes with
/// <c>extras.chip_collider</c> become colliders, <c>extras.chip_volume</c> nodes become triggers, and materials with
/// <c>extras.chip_surface</c> are listed for the renderer. Render meshes are otherwise left to the renderer.
/// </summary>
public sealed class Level
{
    public List<GameEntity> Entities { get; } = [];
    public List<Collider> Colliders { get; } = [];
    public List<Trigger> Triggers { get; } = [];
    public List<SurfaceUse> Surfaces { get; } = [];
    /// <summary>Things the level asked for that the game can't do (an unknown class, a value of the wrong type).</summary>
    public List<string> Warnings { get; } = [];

    public IEnumerable<T> All<T>() where T : GameEntity => Entities.OfType<T>();

    public static Level Load(string path) => Load(File.ReadAllBytes(path));

    public static Level Load(byte[] glb)
    {
        if (glb.Length < 20 || BitConverter.ToUInt32(glb, 0) != 0x46546C67) throw new InvalidDataException("not a .glb");
        int jsonLength = (int)BitConverter.ToUInt32(glb, 12);
        var json = JsonNode.Parse(Encoding.UTF8.GetString(glb, 20, jsonLength))!.AsObject();
        int binStart = 20 + jsonLength + 8;
        var level = new Level();
        var byId = new Dictionary<int, GameEntity>();
        var pending = new List<(GameEntity Entity, FieldInfo Field, JsonNode Value)>();
        var nodes = json["nodes"]?.AsArray() ?? [];
        foreach (var root in json["scenes"]?[0]?["nodes"]?.AsArray() ?? [])
            Visit(root!.GetValue<int>(), Matrix4x4.Identity, null);

        void Visit(int index, Matrix4x4 parent, GameEntity? owner)
        {
            var node = nodes[index]!.AsObject();
            var world = Local(node) * parent;
            var extras = node["extras"] as JsonObject;
            string name = node["name"]?.GetValue<string>() ?? $"node{index}";
            string? surface = extras?["chip_surface"]?.GetValue<string>();
            if (extras?["chip_class"]?.GetValue<string>() is { } cls) owner = level.AddEntity(cls, name, world, extras, byId, pending);
            else if (node["mesh"]?.GetValue<int>() is int mesh)
            {
                if (extras?["chip_volume"]?.GetValue<bool>() == true)
                {
                    var tris = Triangles(json, glb, binStart, mesh, world);
                    level.Triggers.Add(new Trigger(name, surface ?? "trigger", tris.Aggregate(new Vector3(float.MaxValue), Vector3.Min), tris.Aggregate(new Vector3(float.MinValue), Vector3.Max)));
                }
                else if (extras?["chip_collider"]?.GetValue<string>() is { } shape)
                {
                    var collider = new Collider(name, Triangles(json, glb, binStart, mesh, world), shape == "convex",
                        extras["chip_render"]?.GetValue<bool>() ?? true, surface, owner);
                    level.Colliders.Add(collider);
                    owner?.Colliders.Add(collider);
                }
                foreach (var prim in json["meshes"]![mesh]!["primitives"]!.AsArray())
                    if (prim?["material"]?.GetValue<int>() is int m && json["materials"]![m]!["extras"]?["chip_surface"]?.GetValue<string>() is { } used)
                        level.Surfaces.Add(new SurfaceUse(name, used, owner));
            }
            foreach (var child in node["children"]?.AsArray() ?? []) Visit(child!.GetValue<int>(), world, owner);
        }

        // References point at entity ids, so they resolve once every entity exists.
        foreach (var (entity, field, value) in pending)
        {
            GameEntity? Find(JsonNode? v) => v?["entity"]?.GetValue<int>() is int id && byId.TryGetValue(id, out var e)
                && (field.FieldType.IsInstanceOfType(e) || IsListOf(field, e)) ? e : null;
            if (value is JsonArray items)
            {
                var list = (System.Collections.IList)field.GetValue(entity)!;
                list.Clear();
                foreach (var item in items) if (Find(item) is { } target) list.Add(target);
            }
            else field.SetValue(entity, Find(value));
        }
        return level;
    }

    static bool IsListOf(FieldInfo field, GameEntity e) =>
        field.FieldType.IsGenericType && field.FieldType.GetGenericArguments()[0].IsInstanceOfType(e);

    /// <summary>The entity built from an entity node, or null for a class the game doesn't have.</summary>
    GameEntity? AddEntity(string cls, string name, Matrix4x4 world, JsonObject extras, Dictionary<int, GameEntity> byId,
        List<(GameEntity, FieldInfo, JsonNode)> pending)
    {
        var type = Manifest.EntityTypes().FirstOrDefault(t => t.GetCustomAttribute<EntityAttribute>()!.Name == cls);
        if (type == null) { Warnings.Add($"{name}: no entity class {cls}; skipped"); return null; }
        var e = (GameEntity)Activator.CreateInstance(type)!;
        Matrix4x4.Decompose(world, out _, out e.Rotation, out e.Position);
        e.Id = extras["chip_id"]?.GetValue<int>() ?? 0;
        e.Name = extras["chip_name"]?.GetValue<string>() ?? "";
        // Only values the designer changed are in the file; every other field keeps the default the manifest showed.
        var values = extras["properties"] as JsonObject ?? [];
        foreach (var (field, _) in Manifest.Props(type))
        {
            if (!values.TryGetPropertyValue(Manifest.SnakeCase(field.Name), out var v) || v == null) continue;
            bool reference = typeof(GameEntity).IsAssignableFrom(field.FieldType) || IsEntityList(field.FieldType);
            if (reference) { pending.Add((e, field, v)); continue; }
            try { field.SetValue(e, Read(field.FieldType, v)); }
            catch (Exception x) when (x is InvalidOperationException or FormatException or ArgumentException)
            {
                Warnings.Add($"{name}: {field.Name} can't be {v.ToJsonString()}; kept {field.GetValue(e)}");
            }
        }
        Entities.Add(e);
        if (e.Id != 0) byId[e.Id] = e;
        return e;
    }

    static bool IsEntityList(Type t) =>
        t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>) && typeof(GameEntity).IsAssignableFrom(t.GetGenericArguments()[0]);

    static object Read(Type t, JsonNode v)
    {
        float F(int i) => v[i]!.GetValue<float>();
        if (t == typeof(bool)) return v.GetValue<bool>();
        if (t == typeof(int)) return v.GetValue<int>();
        if (t == typeof(float)) return v.GetValue<float>();
        if (t == typeof(string)) return v.GetValue<string>();
        if (t.IsEnum) return Enum.Parse(t, v.GetValue<string>(), ignoreCase: true);
        if (t == typeof(Vector4)) return new Vector4(F(0), F(1), F(2), F(3));
        if (t == typeof(Vector3)) return new Vector3(F(0), F(1), F(2));
        if (t == typeof(Vector2)) return new Vector2(F(0), F(1));
        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>))
        {
            var list = (System.Collections.IList)Activator.CreateInstance(t)!;
            foreach (var item in v.AsArray()) list.Add(Read(t.GetGenericArguments()[0], item!));
            return list;
        }
        throw new ArgumentException($"no reader for {t.Name}");
    }

    static Matrix4x4 Local(JsonObject node)
    {
        float[] A(string key, params float[] fallback) => node[key]?.AsArray().Select(x => x!.GetValue<float>()).ToArray() ?? fallback;
        var t = A("translation", 0, 0, 0);
        var r = A("rotation", 0, 0, 0, 1);
        var s = A("scale", 1, 1, 1);
        return Matrix4x4.CreateScale(s[0], s[1], s[2]) * Matrix4x4.CreateFromQuaternion(new Quaternion(r[0], r[1], r[2], r[3]))
            * Matrix4x4.CreateTranslation(t[0], t[1], t[2]);
    }

    /// <summary>Every triangle of the mesh's primitives, in world space. Chip writes float positions and uint indices.</summary>
    static Vector3[] Triangles(JsonObject json, byte[] glb, int binStart, int mesh, Matrix4x4 world)
    {
        var result = new List<Vector3>();
        foreach (var prim in json["meshes"]![mesh]!["primitives"]!.AsArray())
        {
            var pos = Floats(json, glb, binStart, prim!["attributes"]!["POSITION"]!.GetValue<int>());
            var idx = Indices(json, glb, binStart, prim["indices"]!.GetValue<int>());
            foreach (var i in idx) result.Add(Vector3.Transform(new Vector3(pos[i * 3], pos[i * 3 + 1], pos[i * 3 + 2]), world));
        }
        return [.. result];
    }

    static (int Offset, int Count) View(JsonObject json, int binStart, int accessor)
    {
        var acc = json["accessors"]![accessor]!;
        var view = json["bufferViews"]![acc["bufferView"]!.GetValue<int>()]!;
        return (binStart + (view["byteOffset"]?.GetValue<int>() ?? 0) + (acc["byteOffset"]?.GetValue<int>() ?? 0), acc["count"]!.GetValue<int>());
    }

    static float[] Floats(JsonObject json, byte[] glb, int binStart, int accessor)
    {
        var (offset, count) = View(json, binStart, accessor);
        var result = new float[count * 3];
        for (int i = 0; i < result.Length; i++) result[i] = BitConverter.ToSingle(glb, offset + i * 4);
        return result;
    }

    static int[] Indices(JsonObject json, byte[] glb, int binStart, int accessor)
    {
        var (offset, count) = View(json, binStart, accessor);
        bool shorts = json["accessors"]![accessor]!["componentType"]!.GetValue<int>() == 5123;
        var result = new int[count];
        for (int i = 0; i < count; i++) result[i] = shorts ? BitConverter.ToUInt16(glb, offset + i * 2) : (int)BitConverter.ToUInt32(glb, offset + i * 4);
        return result;
    }

    /// <summary>The nearest collider hit straight down from <paramref name="from"/>, as a height, or null over the void.</summary>
    public float? GroundBelow(Vector3 from)
    {
        float? best = null;
        foreach (var c in Colliders)
            for (int i = 0; i + 2 < c.Triangles.Length; i += 3)
                if (DownHit(from, c.Triangles[i], c.Triangles[i + 1], c.Triangles[i + 2]) is { } y && y <= from.Y + 1e-4f && (best is not { } b || y > b))
                    best = y;
        return best;
    }

    static float? DownHit(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
    {
        // Barycentric test in the XZ plane, then the triangle's height there.
        float d = (b.Z - c.Z) * (a.X - c.X) + (c.X - b.X) * (a.Z - c.Z);
        if (MathF.Abs(d) < 1e-9f) return null;
        float u = ((b.Z - c.Z) * (p.X - c.X) + (c.X - b.X) * (p.Z - c.Z)) / d;
        float v = ((c.Z - a.Z) * (p.X - c.X) + (a.X - c.X) * (p.Z - c.Z)) / d;
        if (u < 0 || v < 0 || u + v > 1) return null;
        return u * a.Y + v * b.Y + (1 - u - v) * c.Y;
    }
}
