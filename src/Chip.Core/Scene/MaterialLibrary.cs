using System.Buffers.Binary;

namespace Chip.Core.Scene;

/// <summary>A material is one image file; its pixel size sets texel density (Scythe reads Base Color size the same way).</summary>
public sealed record MaterialInfo(string Name, string? Path, int Width, int Height);

/// <summary>Materials keyed by name (file stem). Unknown names resolve to a "missing" material so documents always load.</summary>
public sealed class MaterialLibrary
{
    public static readonly MaterialInfo Missing = new("missing", null, 64, 64);

    readonly Dictionary<string, MaterialInfo> _byName = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<MaterialInfo> All { get; private set; } = [];

    public static MaterialLibrary LoadFolder(string folder)
    {
        var lib = new MaterialLibrary();
        if (Directory.Exists(folder))
            foreach (var file in Directory.EnumerateFiles(folder, "*.png", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase))
                if (TryReadPngSize(file, out int w, out int h))
                    lib.Add(new MaterialInfo(System.IO.Path.GetFileNameWithoutExtension(file), file, w, h));
        if (lib.All.Count == 0) lib.Add(Missing with { Name = "default" });
        return lib;
    }

    public void Add(MaterialInfo info)
    {
        _byName[info.Name] = info;
        All = [.. All, info];
    }

    public MaterialInfo Get(string name) => _byName.GetValueOrDefault(name) ?? Missing;

    static bool TryReadPngSize(string file, out int width, out int height)
    {
        width = height = 0;
        Span<byte> header = stackalloc byte[24];
        using var fs = File.OpenRead(file);
        if (fs.Read(header) != 24 || header[1] != 'P' || header[2] != 'N' || header[3] != 'G') return false;
        width = BinaryPrimitives.ReadInt32BigEndian(header[16..]);
        height = BinaryPrimitives.ReadInt32BigEndian(header[20..]);
        return width > 0 && height > 0;
    }
}
