using System.Numerics;
using System.Text.Json;

namespace Chip.Core.Scene;

/// <summary>How a surface's faces collide in the export.</summary>
public enum SurfaceCollide
{
    /// <summary>Never solid.</summary>
    None,
    /// <summary>Solid when its mesh collides (the mesh's collision setting decides), like an ordinary face.</summary>
    Mesh,
    /// <summary>Always solid, whatever the mesh's setting (player clip).</summary>
    Always,
}

public enum SurfacePattern { Solid, Stripes, Checker }

/// <summary>How a surface draws in Chip. Never exported: the engine decides how a surface looks in the game.</summary>
public sealed record SurfaceLook
{
    /// <summary>Linear 0..1 RGB.</summary>
    public Vector3 Color { get; init; } = new(0.6f);
    public SurfacePattern Pattern { get; init; } = SurfacePattern.Stripes;
    /// <summary>0..1, used when View > Translucent tool surfaces is on.</summary>
    public float Opacity { get; init; } = 1;
    /// <summary>An image drawn through the faces' UVs instead of the pattern (absolute path), e.g. a placeholder frame.</summary>
    public string? Image { get; init; }
    /// <summary>Unlit equirectangular image sampled by the camera ray, with a procedural sky when absent.</summary>
    public bool Panorama { get; init; }
    /// <summary>A world asset property whose kind resolves the image, before Image.</summary>
    public string? ImageFrom { get; init; }
}

/// <summary>
/// A material name that changes how faces export: whether they render, collide or form a volume. Chip's tool surfaces
/// (<c>nodraw</c>, <c>clip</c>, <c>trigger</c>, <c>skip</c>) are built in; a game adds its own, or changes the built-ins,
/// in <c>chip.project.json</c> (<see cref="GameProject.Surfaces"/>). A face whose material is a surface's name is that
/// surface. Exported nodes and materials carry <c>extras.chip_surface</c>, so the game can give each its meaning.
/// </summary>
public sealed record Surface
{
    public required string Name { get; init; }
    /// <summary>Faces render (as a glTF material named after the surface). Off: they're only collision or a volume.</summary>
    public bool Render { get; init; }
    public SurfaceCollide Collide { get; init; } = SurfaceCollide.Mesh;
    /// <summary>Faces form a volume the game tests for overlap (a trigger, water): exported as their own non-solid node.</summary>
    public bool Volume { get; init; }
    /// <summary>Left out of the export entirely (overrides everything else).</summary>
    public bool Skip { get; init; }
    public string Doc { get; init; } = "";
    public SurfaceLook Look { get; init; } = new();

    /// <summary>Whether the surface draws translucent: anything that doesn't render in the game.</summary>
    public bool IsTool => !Render;
}

/// <summary>The surfaces in use: the project's (<see cref="GameProject.Surfaces"/>) over Chip's built-in tool surfaces.</summary>
public static class Surfaces
{
    static SurfaceLook Tool(uint rgb) => new()
    {
        Color = new Vector3((rgb >> 16 & 0xFF) / 255f, (rgb >> 8 & 0xFF) / 255f, (rgb & 0xFF) / 255f), Opacity = 0.45f,
    };

    /// <summary>Source/Hammer's tool textures, in browser order.</summary>
    public static readonly IReadOnlyList<Surface> BuiltIn =
    [
        new() { Name = "nodraw", Collide = SurfaceCollide.Mesh, Doc = "Never rendered; still solid when its mesh collides", Look = Tool(0xE8C547) },
        new() { Name = "clip", Collide = SurfaceCollide.Always, Doc = "Collision only (player clip), whatever the mesh's setting", Look = Tool(0x9B5DE5) },
        new() { Name = "trigger", Collide = SurfaceCollide.None, Volume = true, Doc = "A trigger volume", Look = Tool(0x2BA89A) },
        new() { Name = "skip", Skip = true, Collide = SurfaceCollide.None, Doc = "Left out of the export", Look = Tool(0x4A7FE0) },
        new() { Name = "sky", Render = true, Doc = "Shows the level's sky, as if infinitely far away. Paint the outside of windows, open roofs and the level's outer shell.", Look = new() { Panorama = true, Pattern = SurfacePattern.Solid } },
    ];

    /// <summary>Every surface the current project can use: its own first, then the built-ins it doesn't replace.</summary>
    public static IReadOnlyList<Surface> All
    {
        get
        {
            var project = EntityClasses.Project?.Surfaces ?? [];
            return project.Concat(BuiltIn.Where(b => !project.Any(p => Same(p.Name, b.Name)))).ToList();
        }
    }

    /// <summary>The surface a material name is, or null for an ordinary material. Accepts Hammer's <c>toolsnodraw</c> style and <c>playerclip</c>.</summary>
    public static Surface? Of(string material) => Of(material, EntityClasses.Project?.Surfaces);

    public static Surface? Of(string material, IReadOnlyList<Surface>? project)
    {
        if (project != null)
            foreach (var s in project)
                if (Same(s.Name, material)) return s;
        var n = material.StartsWith("tools", StringComparison.OrdinalIgnoreCase) && material.Length > 5 ? material[5..] : material;
        if (Same(n, "playerclip")) n = "clip";
        if (!Same(n, material) && project != null)
            foreach (var s in project)
                if (Same(s.Name, n)) return s;
        foreach (var s in BuiltIn)
            if (Same(s.Name, n)) return s;
        return null;
    }

    /// <summary>True for surfaces that render: ordinary materials, and surfaces with <see cref="Surface.Render"/>.</summary>
    public static bool Renders(string material) => Of(material) is not { } s || (s.Render && !s.Skip);

    static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reads <c>chip.project.json</c>'s <c>"surfaces"</c> object. Image paths resolve against <paramref name="root"/>.
    /// Throws <see cref="InvalidDataException"/> with what's wrong.
    /// </summary>
    public static List<Surface> Parse(JsonElement surfaces, string root)
    {
        if (surfaces.ValueKind != JsonValueKind.Object) throw new InvalidDataException("\"surfaces\" should be an object of name: surface");
        var list = new List<Surface>();
        foreach (var s in surfaces.EnumerateObject())
        {
            if (s.Value.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"surface {s.Name} should be an object");
            var builtIn = BuiltIn.FirstOrDefault(b => Same(b.Name, s.Name));
            var surface = (builtIn ?? new Surface { Name = s.Name, Render = true }) with { Name = s.Name };
            JsonElement? look = null;
            foreach (var f in s.Value.EnumerateObject())
                switch (f.Name)
                {
                    case "render": surface = surface with { Render = Bool(s.Name, f) }; break;
                    case "volume": surface = surface with { Volume = Bool(s.Name, f) }; break;
                    case "skip": surface = surface with { Skip = Bool(s.Name, f) }; break;
                    case "doc": surface = surface with { Doc = f.Value.GetString() ?? "" }; break;
                    case "collide":
                        surface = surface with
                        {
                            Collide = f.Value.GetString() switch
                            {
                                "none" => SurfaceCollide.None, "mesh" => SurfaceCollide.Mesh, "always" => SurfaceCollide.Always,
                                _ => throw new InvalidDataException($"surface {s.Name}: \"collide\" should be \"none\", \"mesh\" or \"always\""),
                            },
                        };
                        break;
                    case "look": look = f.Value; break;
                    default:
                        throw new InvalidDataException($"surface {s.Name} has an unknown field \"{f.Name}\" (known: render, collide, volume, skip, doc, look)");
                }
            // A new surface starts from a look that matches what it does: a tool surface (it doesn't render) striped and
            // see-through, a rendered one solid. The fields given replace parts of that.
            var baseLook = builtIn?.Look ?? (surface.Render ? new SurfaceLook { Pattern = SurfacePattern.Solid } : Tool(0x8A8F98));
            surface = surface with { Look = look is { } l ? ParseLook(s.Name, l, baseLook, root) : baseLook };
            list.Add(surface);
        }
        return list;
    }

    static bool Bool(string surface, JsonProperty f) => f.Value.ValueKind switch
    {
        JsonValueKind.True => true, JsonValueKind.False => false,
        _ => throw new InvalidDataException($"surface {surface}: \"{f.Name}\" should be true or false"),
    };

    static SurfaceLook ParseLook(string surface, JsonElement o, SurfaceLook look, string root)
    {
        if (o.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"surface {surface}: \"look\" should be an object");
        foreach (var f in o.EnumerateObject())
            switch (f.Name)
            {
                case "color":
                    look = look with
                    {
                        Color = EntityValues.Color(f.Value) is { } c ? new Vector3(c.X, c.Y, c.Z)
                            : throw new InvalidDataException($"surface {surface}: \"color\" should be \"#rrggbb\" or [r, g, b]"),
                    };
                    break;
                case "pattern":
                    look = look with
                    {
                        Pattern = f.Value.GetString() switch
                        {
                            "solid" => SurfacePattern.Solid, "stripes" => SurfacePattern.Stripes, "checker" => SurfacePattern.Checker,
                            _ => throw new InvalidDataException($"surface {surface}: \"pattern\" should be \"solid\", \"stripes\" or \"checker\""),
                        },
                    };
                    break;
                case "opacity":
                    look = look with
                    {
                        Opacity = f.Value.ValueKind == JsonValueKind.Number ? Math.Clamp(f.Value.GetSingle(), 0.05f, 1f)
                            : throw new InvalidDataException($"surface {surface}: \"opacity\" should be a number from 0 to 1"),
                    };
                    break;
                case "panorama": look = look with { Panorama = Bool(surface, f) }; break;
                case "imageFrom": look = look with { ImageFrom = f.Value.GetString() }; break;
                case "image":
                    look = look with
                    {
                        Image = f.Value.GetString() is { Length: > 0 } p ? Path.GetFullPath(Path.Combine(root, p))
                            : throw new InvalidDataException($"surface {surface}: \"image\" should be a path relative to the project file"),
                    };
                    break;
                default:
                    throw new InvalidDataException($"surface {surface}: \"look\" has an unknown field \"{f.Name}\" (known: color, pattern, opacity, image, panorama, imageFrom)");
            }
        return look;
    }
}
