using System.Numerics;
using System.Text.Json;
using Chip.Core.Picking;

namespace Chip.Core.Scene;

/// <summary>
/// The entity classes in use: the game project's (found by walking up from the level or its export path to
/// <c>chip.project.json</c> or <c>project.godot</c>, see <see cref="GameProject"/>), read from its entity manifest, else
/// the built-in ones. <see cref="Track"/> follows the project and reloads the manifest when it changes; a manifest that
/// can't be read keeps the last good classes and says why in <see cref="Problem"/>.
/// </summary>
public static class EntityClasses
{
    static readonly EntityClassTracker Tracker = new();

    public static EntityCatalog Current => Tracker.Current;
    /// <summary>Changes whenever <see cref="Current"/> does, so caches of class boxes can rebuild.</summary>
    public static int Revision => Tracker.Revision;
    /// <summary>The game project the level is in, or null.</summary>
    public static GameProject? Project => Tracker.Project;
    /// <summary>The project folder the level is in, or null.</summary>
    public static string? ProjectRoot => Tracker.ProjectRoot;
    /// <summary>The engine's name for messages: the project's, else Godot (whose classes the built-ins are).</summary>
    public static string Engine => Tracker.Project?.Engine ?? "Godot";
    /// <summary>Why the project's classes aren't in use (no manifest yet, or one that can't be read); null when they are.</summary>
    public static string? Problem => Tracker.Problem;

    /// <inheritdoc cref="EntityClassTracker.Track"/>
    public static bool Track(params string?[] near) => Tracker.Track(near);

    /// <summary>Uses <paramref name="catalog"/> until the next <see cref="Track"/> that finds a change (tests).</summary>
    public static void Use(EntityCatalog catalog) => Tracker.Use(catalog);

    /// <summary>Back to the built-in classes (tests).</summary>
    public static void Reset() => Tracker.Use(EntityCatalog.BuiltIn);

    public static IReadOnlyList<EntityClass> All => Current.Classes;

    /// <summary>The class, or a generic one (<see cref="EntityClass.Known"/> false) for a name the catalog doesn't define.</summary>
    public static EntityClass Get(string className) => Current.Find(className) ?? Generic(className);

    public static bool IsKnown(string className) => Current.Find(className) != null;

    public static EntityClass Generic(string name) => new() { Name = name, Label = name, Known = false };

    /// <summary>A new entity of the class (id assigned by <see cref="Document.Add(Entity)"/>). It sets no properties: every one has its class default until changed.</summary>
    public static Entity Create(string className, Vector3 position, float yawDegrees = 0) => new()
    {
        ClassName = className, Position = position, Rotation = new Vector3(0, yawDegrees, 0),
    };
}

/// <summary>Follows the game project a level is in and the entity classes its manifest declares (see <see cref="EntityClasses"/>).</summary>
public sealed class EntityClassTracker
{
    public EntityCatalog Current { get; private set; } = EntityCatalog.BuiltIn;
    public int Revision { get; private set; }
    public GameProject? Project { get; private set; }
    public string? ProjectRoot => Project?.Root;
    public string? Problem { get; private set; }

    string? _key;
    long _stamp;

    /// <summary>
    /// Uses the project found from the first of <paramref name="near"/> that is inside one (the level's export path,
    /// then the level), reloading its manifest when it or the project file changed on disk. True when the classes changed.
    /// </summary>
    public bool Track(params string?[] near)
    {
        string? root = near.Select(GameProject.FindRoot).FirstOrDefault(r => r != null);
        string? projectFile = root != null ? Path.Combine(root, GameProject.FileName) : null;
        long projectStamp = projectFile != null ? Stamp(projectFile) : 0;
        // The project file names the manifest, so it's read first; a change to either reloads.
        string? problem = null;
        var project = root != null ? GameProject.Find(root, out problem) : null;
        string? manifest = project?.Manifest;
        long stamp = (manifest != null ? Stamp(manifest) : 0) ^ (projectStamp * 31);
        string? key = project != null ? $"{root}|{manifest}" : null;
        if (key == _key && stamp == _stamp) return false;
        _key = key;
        _stamp = stamp;
        Project = project;
        if (project == null) Set(EntityCatalog.BuiltIn, null);
        else if (problem != null) Set(EntityCatalog.BuiltIn, problem + "; until it's fixed Chip offers its built-in classes");
        else if (!File.Exists(manifest))
            Set(EntityCatalog.BuiltIn, project.IsGodot && project.File == null
                ? $"{Path.GetFileName(root)} has no entity classes yet. Enable the Chip import plugin in Godot " +
                  "(Project > Project Settings > Plugins); until then Chip offers its built-in classes"
                : $"{Path.GetFileName(root)} has no entity classes yet: write {project.Relative(manifest!)} " +
                  "(docs/engines.md); until then Chip offers its built-in classes");
        else
            try { Set(EntityCatalog.Parse(File.ReadAllText(manifest!), manifest), null); }
            catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                // Keep the project's last good classes (the addon may be mid-write; the next change reloads).
                Set(Current.Source == manifest ? Current : EntityCatalog.BuiltIn,
                    $"Can't read the entity classes in {Path.Combine(Path.GetFileName(root)!, project.Relative(manifest!))}: {e.Message}");
            }
        return true;
    }

    /// <summary>Uses <paramref name="catalog"/> until the next <see cref="Track"/> that finds a change.</summary>
    public void Use(EntityCatalog catalog)
    {
        Project = catalog.Source != null ? GameProject.Find(catalog.Source) : null;
        _key = Project != null ? $"{Project.Root}|{catalog.Source}" : null;
        _stamp = catalog.Source != null ? Stamp(catalog.Source) ^ (Project?.File is { } f ? Stamp(f) * 31 : 0) : 0;
        Set(catalog, null);
    }

    void Set(EntityCatalog catalog, string? problem)
    {
        Current = catalog;
        Problem = problem;
        Revision++;
    }

    static long Stamp(string path)
    {
        try { return File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks ^ new FileInfo(path).Length : 0; }
        catch (IOException) { return 0; }
        catch (UnauthorizedAccessException) { return 0; }
    }
}

/// <summary>A point on an entity's surface, with the outward normal facing the ray (world space).</summary>
public readonly record struct EntitySurfaceHit(Entity Entity, float Distance, Vector3 Point, Vector3 Normal);

/// <summary>Entity orientation, frames and hit-testing. Forward is -Z (glTF and Godot), up is +Y.</summary>
public static class EntityOps
{
    const float Deg = MathF.PI / 180;

    /// <summary>
    /// <see cref="Entity.Rotation"/> as a quaternion: yaw about Y, pitch about X, roll about Z, composed so roll applies first
    /// in the object's frame (Godot's YXZ order).
    /// </summary>
    public static Quaternion Orientation(Vector3 eulerDegrees) =>
        Quaternion.CreateFromYawPitchRoll(eulerDegrees.Y * Deg, eulerDegrees.X * Deg, eulerDegrees.Z * Deg);

    /// <summary>Inverse of <see cref="Orientation"/>, in degrees, rounded to a thousandth so typed values stay clean.</summary>
    public static Vector3 ToEuler(Quaternion q)
    {
        var m = Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(q));
        // m is row-vector (v * m); R = transpose(m) in column form, R = Ry * Rx * Rz.
        float sx = Math.Clamp(-m.M32, -1, 1);
        float x = MathF.Asin(sx), y, z;
        if (MathF.Abs(sx) < 0.99999f)
        {
            y = MathF.Atan2(m.M31, m.M33);
            z = MathF.Atan2(m.M12, m.M22);
        }
        else
        {
            y = MathF.Atan2(-m.M13, m.M11);
            z = 0;
        }
        static float Clean(float rad)
        {
            float d = MathF.Round(rad / Deg * 1000) / 1000;
            return d == 0 ? 0 : d; // no negative zero in the inspector
        }
        return new Vector3(Clean(x), Clean(y), Clean(z));
    }

    public static Quaternion Orientation(Entity e) => Orientation(e.Rotation);

    // ---- Edits (run inside a Session step) ----

    /// <summary>Changes the class. Values stay, so changing back loses nothing; ones the new class doesn't have are listed as unused.</summary>
    public static void SetClass(Entity e, string className) => e.ClassName = className.Trim();

    /// <summary>Sets or adds a value. Returns an error message (and changes nothing) when the key is blank.</summary>
    public static string? SetProperty(Entity e, string key, JsonElement value)
    {
        key = key.Trim();
        if (key.Length == 0) return "Type a key for the property";
        e.Properties[key] = value;
        return null;
    }

    public static string? SetProperty(Entity e, string key, string value) => SetProperty(e, key, EntityValues.Of(value));

    /// <summary>
    /// Sets a class property, checked against its type. A value equal to the class default is removed instead, so the
    /// entity follows the default if the engine changes it later.
    /// </summary>
    public static string? SetProperty(Entity e, PropSpec p, JsonElement value)
    {
        if (EntityValues.Coerce(p, value, out var error) is not { } ok) return error;
        if (EntityValues.Same(ok, p.Default)) e.Properties.Remove(p.Name);
        else e.Properties[p.Name] = ok;
        return null;
    }

    /// <summary>Renames a key, keeping its value. Returns an error message (and changes nothing) when the new key is blank or taken.</summary>
    public static string? RenameProperty(Entity e, string oldKey, string newKey)
    {
        newKey = newKey.Trim();
        if (newKey == oldKey) return null;
        if (newKey.Length == 0) return "A property key can't be empty. Use the remove button to delete it";
        if (e.Properties.ContainsKey(newKey)) return $"There is already a property called {newKey}";
        if (!e.Properties.Remove(oldKey, out var value)) return $"There is no property called {oldKey}";
        e.Properties[newKey] = value;
        return null;
    }

    /// <summary>Entity space to world.</summary>
    public static Matrix4x4 World(Entity e) => Matrix4x4.CreateFromQuaternion(Orientation(e)) * Matrix4x4.CreateTranslation(e.Position);

    public static Vector3 Forward(Entity e) => Vector3.Transform(-Vector3.UnitZ, Orientation(e));

    public static Vector3 Up(Entity e) => Vector3.Transform(Vector3.UnitY, Orientation(e));

    /// <summary>The entity's box in entity space: a loaded reference model's bounds, else the class box.</summary>
    public static (Vector3 Min, Vector3 Max) Box(Entity e)
    {
        if (ReferenceModels.Get(e).Model is { } model) return (model.Min, model.Max);
        var c = EntityClasses.Get(e.ClassName);
        return (c.BoxMin, c.BoxMax);
    }

    /// <summary>World-space centre of the box (where the glyph is drawn).</summary>
    public static Vector3 Center(Entity e)
    {
        var (min, max) = Box(e);
        return Vector3.Transform((min + max) * 0.5f, World(e));
    }

    /// <summary>The box's eight corners in world space (bit 0 = x, bit 1 = y, bit 2 = z).</summary>
    public static Vector3[] BoxCorners(Entity e)
    {
        var (min, max) = Box(e);
        var w = World(e);
        var result = new Vector3[8];
        for (int i = 0; i < 8; i++)
            result[i] = Vector3.Transform(new Vector3((i & 1) == 0 ? min.X : max.X, (i & 2) == 0 ? min.Y : max.Y,
                (i & 4) == 0 ? min.Z : max.Z), w);
        return result;
    }

    /// <summary>
    /// Distance along the ray to the entity, or null on a miss: a loaded reference model by its triangles (so clicks go
    /// through a doorway), anything else by its oriented box (slab test in entity space).
    /// </summary>
    public static float? RayHit(Entity e, Ray ray)
    {
        var (min, max) = Box(e);
        var inv = Quaternion.Conjugate(Orientation(e));
        var o = Vector3.Transform(ray.Origin - e.Position, inv);
        var d = Vector3.Transform(ray.Dir, inv);
        if (ReferenceModels.Get(e).Model is { } model) return ModelHit(model, new Ray(o, d));
        float tmin = 0, tmax = float.MaxValue;
        for (int a = 0; a < 3; a++)
        {
            float oa = a == 0 ? o.X : a == 1 ? o.Y : o.Z, da = a == 0 ? d.X : a == 1 ? d.Y : d.Z;
            float lo = a == 0 ? min.X : a == 1 ? min.Y : min.Z, hi = a == 0 ? max.X : a == 1 ? max.Y : max.Z;
            if (MathF.Abs(da) < 1e-9f)
            {
                if (oa < lo || oa > hi) return null;
                continue;
            }
            float t1 = (lo - oa) / da, t2 = (hi - oa) / da;
            if (t1 > t2) (t1, t2) = (t2, t1);
            tmin = MathF.Max(tmin, t1);
            tmax = MathF.Min(tmax, t2);
            if (tmin > tmax) return null;
        }
        return tmin;
    }

    /// <summary>
    /// Where the ray meets the entity's surface, to place something on it: a loaded model's nearest triangle, else the
    /// face of its box the ray enters. The normal is in world space and faces the ray. Null on a miss, or when the ray
    /// starts inside the box (there's no outside surface to stand on).
    /// </summary>
    public static EntitySurfaceHit? SurfaceHit(Entity e, Ray ray)
    {
        var rotation = Orientation(e);
        var inv = Quaternion.Conjugate(rotation);
        var local = new Ray(Vector3.Transform(ray.Origin - e.Position, inv), Vector3.Transform(ray.Dir, inv));
        Vector3 normal;
        float t;
        if (ReferenceModels.Get(e).Model is { } model)
        {
            float? best = null;
            normal = default;
            var tris = model.Triangles;
            for (int i = 0; i + 2 < tris.Length; i += 3)
            {
                if (!Picker.RayTriangle(local, tris[i], tris[i + 1], tris[i + 2], out float hit) || (best is { } b && hit >= b)) continue;
                best = hit;
                normal = Vector3.Cross(tris[i + 1] - tris[i], tris[i + 2] - tris[i]);
            }
            if (best is not { } bt || normal.LengthSquared() < 1e-12f) return null;
            t = bt;
            normal = Vector3.Normalize(normal);
        }
        else
        {
            var (min, max) = Box(e);
            float near = float.MinValue, far = float.MaxValue;
            int axis = -1;
            for (int a = 0; a < 3; a++)
            {
                float o = local.Origin[a], d = local.Dir[a];
                if (MathF.Abs(d) < 1e-9f) { if (o < min[a] || o > max[a]) return null; continue; }
                float t1 = (min[a] - o) / d, t2 = (max[a] - o) / d;
                if (t1 > t2) (t1, t2) = (t2, t1);
                if (t1 > near) { near = t1; axis = a; }
                far = MathF.Min(far, t2);
                if (near > far) return null;
            }
            if (axis < 0 || near < 0) return null; // inside, or behind
            t = near;
            normal = axis == 0 ? Vector3.UnitX : axis == 1 ? Vector3.UnitY : Vector3.UnitZ;
        }
        normal = Vector3.Transform(normal, rotation);
        if (Vector3.Dot(normal, ray.Dir) > 0) normal = -normal;
        return new EntitySurfaceHit(e, t, ray.At(t), normal);
    }

    /// <summary>The nearest visible entity surface along the ray (see <see cref="SurfaceHit"/>), skipping <paramref name="ignore"/>.</summary>
    public static EntitySurfaceHit? FirstSurface(Document doc, Ray ray, Entity? ignore = null)
    {
        EntitySurfaceHit? best = null;
        foreach (var e in doc.Entities)
        {
            if (e.Hidden || e == ignore) continue;
            if (SurfaceHit(e, ray) is { } hit && (best is not { } b || hit.Distance < b.Distance)) best = hit;
        }
        return best;
    }

    /// <summary>Nearest triangle hit, from either side, in model space.</summary>
    static float? ModelHit(ReferenceModel model, Ray ray)
    {
        float? best = null;
        var t = model.Triangles;
        for (int i = 0; i + 2 < t.Length; i += 3)
        {
            if (!Picker.RayTriangle(ray, t[i], t[i + 1], t[i + 2], out float hit)) continue;
            if (best is not { } b || hit < b) best = hit;
        }
        return best;
    }
}
