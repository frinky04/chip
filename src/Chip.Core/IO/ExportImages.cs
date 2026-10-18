using Chip.Core.Scene;

namespace Chip.Core.IO;

/// <summary>A file an export writes beside its main output, relative to that output's folder.</summary>
public sealed record SideFile(string RelativePath, byte[] Data);

/// <summary>
/// How an export points at a material's image: embedded bytes, or a relative reference. References go straight to the
/// library file when the format allows it (PNG/JPEG on the same drive); otherwise a converted copy is written into a
/// <c>name_textures</c> folder beside the output.
/// </summary>
public static class ExportImages
{
    public readonly record struct ImageRef(byte[]? Embedded, string MimeType, string? RelativePath, SideFile? Copy);

    public static ImageRef? Resolve(MaterialInfo info, string? outputPath, bool embed)
    {
        if (info.Path == null || !File.Exists(info.Path)) return null;
        if (embed || outputPath == null)
            return MaterialLibrary.ExportImage(info) is { } img ? new ImageRef(img.Data, img.MimeType, null, null) : null;

        var dir = Path.GetDirectoryName(Path.GetFullPath(outputPath))!;
        var ext = Path.GetExtension(info.Path).ToLowerInvariant();
        if (ext is ".png" or ".jpg" or ".jpeg")
        {
            var rel = Path.GetRelativePath(dir, Path.GetFullPath(info.Path));
            if (!Path.IsPathRooted(rel))
                return new ImageRef(null, ext == ".png" ? "image/png" : "image/jpeg", rel.Replace('\\', '/'), null);
        }
        if (MaterialLibrary.ExportImage(info) is not { } data) return null;
        var copy = $"{Path.GetFileNameWithoutExtension(outputPath)}_textures/{info.Name}{(data.MimeType == "image/png" ? ".png" : ".jpg")}";
        return new ImageRef(null, data.MimeType, copy, new SideFile(copy, data.Data));
    }

    /// <summary>Percent-encodes each path segment for a glTF <c>uri</c>.</summary>
    public static string ToUri(string relativePath) => string.Join('/', relativePath.Split('/').Select(s => s == ".." ? s : Uri.EscapeDataString(s)));

    /// <summary>Writes side files under the output's folder (each atomically).</summary>
    public static void WriteAll(string outputPath, IEnumerable<SideFile> files)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(outputPath))!;
        foreach (var f in files)
        {
            var full = Path.Combine(dir, f.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            AtomicFile.Write(full, f.Data);
        }
    }
}
