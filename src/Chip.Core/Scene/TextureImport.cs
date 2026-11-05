using Chip.Core.IO;
using StbImageSharp;

namespace Chip.Core.Scene;

/// <summary>
/// A file to copy: where it is, where it goes below the folder, and the file already there under its material name.
/// <see cref="Material"/> is set when the file is the image a library material uses now, so the copy takes that material over.
/// <see cref="Takeover"/> is set when it's a different image with the name of a library material the level uses: project
/// textures win, so faces would switch to it.
/// </summary>
public sealed record ImportItem(string Source, string Relative, string? Clash, MaterialInfo? Material = null, MaterialInfo? Takeover = null)
{
    /// <summary>Importing this changes something already there, so the user gets a say.</summary>
    public bool NeedsAnswer => Clash != null || Takeover != null;
}

/// <summary>What a drop would do. <see cref="Rejected"/> says what's being left out, and why. <see cref="Names"/> are the library's, which a renamed copy avoids.</summary>
public sealed record ImportPlan(IReadOnlyList<ImportItem> Items, IReadOnlyList<string> Rejected, IReadOnlyList<string>? Names = null);

/// <summary>What an import did. <see cref="FirstMaterial"/> is the first new material, to make active.</summary>
public sealed record TextureImportResult(int Imported, int Replaced, int Resized, int OtherMaps, string? FirstMaterial, IReadOnlyList<string> Problems);

/// <summary>
/// Copies images into a project's texture folder. A dropped folder keeps its layout. Material names decide what clashes, so
/// <c>brick.jpg</c> arriving as <c>brick.png</c> replaces it instead of being hidden behind it.
/// </summary>
public static class TextureImport
{
    /// <summary>
    /// Works out what importing <paramref name="paths"/> (files or folders) into <paramref name="folder"/> would do. Copies
    /// nothing. <paramref name="library"/> tells which files are images its materials use now, and <paramref name="level"/>
    /// which of its materials a different image would take over from faces.
    /// </summary>
    public static ImportPlan Inspect(IEnumerable<string> paths, string folder, MaterialLibrary? library = null, Document? level = null)
    {
        var used = level == null ? [] : MaterialLibrary.Usage(level).Select(u => u.Material).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        var existing = Existing(target);
        var items = new List<ImportItem>();
        var rejected = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string file, string relative)
        {
            if (Path.GetFullPath(file).StartsWith(target + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return; // already here
            string name = Key(file);
            if (!MaterialLibrary.TryReadImageSize(file, out _, out _)) rejected.Add($"{Path.GetFileName(file)} can't be read as an image");
            else if (!seen.Add(name)) rejected.Add($"{Path.GetFileName(file)} has the same name as another dropped image");
            else
            {
                var clash = existing.GetValueOrDefault(name);
                var material = InUse(file);
                items.Add(new ImportItem(file, relative, clash, material, clash == null && material == null ? TakenOver(file) : null));
            }
        }

        // A scanned material only: one added in code, such as a surface, outranks project textures, so nothing would change.
        MaterialInfo? TakenOver(string file)
        {
            if (library == null || MaterialLibrary.MaterialName(Path.GetFileNameWithoutExtension(file)) is not { } material || !used.Contains(material)) return null;
            var info = library.Get(material);
            return info.Source.Length > 0 ? info : null;
        }

        MaterialInfo? InUse(string file)
        {
            if (library == null || MaterialLibrary.MaterialName(Path.GetFileNameWithoutExtension(file)) is not { } material) return null;
            var info = library.Get(material);
            return string.Equals(info.Path, Path.GetFullPath(file), StringComparison.OrdinalIgnoreCase) ? info : null;
        }

        foreach (var path in paths)
        {
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            if (Directory.Exists(full))
            {
                var parent = Path.GetDirectoryName(full) ?? full;
                foreach (var file in Images(full).Order(StringComparer.OrdinalIgnoreCase)) Add(file, Path.GetRelativePath(parent, file));
            }
            else if (!File.Exists(full)) rejected.Add($"{Path.GetFileName(full)} wasn't found");
            else if (!IsImage(full)) rejected.Add($"{Path.GetFileName(full)} isn't a PNG, JPEG or TGA");
            else Add(full, Path.GetFileName(full));
        }
        return new ImportPlan(items, rejected, library?.All.Select(m => m.Name).ToList());
    }

    /// <summary>
    /// Copies the plan's files into <paramref name="folder"/>. A clash is replaced and a takeover goes ahead, or with
    /// <paramref name="keepBoth"/> the new image takes a free name instead. An image longer than <paramref name="maxSize"/> (0 for no limit) on its longest side is
    /// scaled down and written as PNG. Safe on a worker thread. A file that fails doesn't stop the others.
    /// </summary>
    public static TextureImportResult Run(ImportPlan plan, string folder, bool keepBoth, int maxSize)
    {
        var target = Path.GetFullPath(folder);
        var taken = new HashSet<string>(Existing(target).Keys.Concat(plan.Names ?? []), StringComparer.OrdinalIgnoreCase);
        int imported = 0, replaced = 0, resized = 0, otherMaps = 0;
        string? first = null;
        var problems = new List<string>();
        foreach (var item in plan.Items)
        {
            try
            {
                bool map = MaterialLibrary.MaterialName(Path.GetFileNameWithoutExtension(item.Source)) == null;
                if (item.Clash != null && keepBoth && map) continue; // no material would use a renamed map
                var (bytes, extension, scaled) = Read(item.Source, maxSize);
                string dest = keepBoth && item.NeedsAnswer ? Path.Combine(target, Path.GetDirectoryName(item.Relative) ?? "", Free(Key(item.Source), taken))
                    : item.Clash != null ? Path.Combine(Path.GetDirectoryName(item.Clash)!, Path.GetFileName(item.Relative))
                    : Path.Combine(target, item.Relative);
                dest = Path.ChangeExtension(dest, extension);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                AtomicFile.Write(dest, bytes);
                if (item.Clash != null && !keepBoth)
                {
                    if (!string.Equals(item.Clash, dest, StringComparison.OrdinalIgnoreCase)) File.Delete(item.Clash);
                    replaced++;
                }
                imported++;
                if (scaled) resized++;
                if (map) otherMaps++;
                else first ??= MaterialLibrary.MaterialName(Path.GetFileNameWithoutExtension(dest));
            }
            catch (Exception e) // unreadable pixels, a full disk, a locked file: say so and carry on
            {
                problems.Add($"{Path.GetFileName(item.Source)}: {e.Message}");
            }
        }
        return new TextureImportResult(imported, replaced, resized, otherMaps, first, problems);
    }

    /// <summary>Box-filters RGBA8 pixels to a smaller size, weighting colour by alpha so hidden texels don't bleed into edges.</summary>
    public static byte[] Shrink(byte[] rgba, int width, int height, int newWidth, int newHeight)
    {
        var result = new byte[newWidth * newHeight * 4];
        for (int y = 0; y < newHeight; y++)
        {
            int y0 = y * height / newHeight, y1 = Math.Max(y0 + 1, (y + 1) * height / newHeight);
            for (int x = 0; x < newWidth; x++)
            {
                int x0 = x * width / newWidth, x1 = Math.Max(x0 + 1, (x + 1) * width / newWidth);
                long r = 0, g = 0, b = 0, a = 0;
                for (int sy = y0; sy < y1; sy++)
                    for (int sx = x0; sx < x1; sx++)
                    {
                        int i = (sy * width + sx) * 4, alpha = rgba[i + 3];
                        r += rgba[i] * alpha; g += rgba[i + 1] * alpha; b += rgba[i + 2] * alpha; a += alpha;
                    }
                int count = (x1 - x0) * (y1 - y0), o = (y * newWidth + x) * 4;
                if (a > 0)
                {
                    result[o] = (byte)(r / a); result[o + 1] = (byte)(g / a); result[o + 2] = (byte)(b / a);
                }
                result[o + 3] = (byte)(a / count);
            }
        }
        return result;
    }

    static (byte[] Bytes, string Extension, bool Scaled) Read(string source, int maxSize)
    {
        var extension = Path.GetExtension(source);
        if (maxSize <= 0 || !MaterialLibrary.TryReadImageSize(source, out int w, out int h) || Math.Max(w, h) <= maxSize)
            return (File.ReadAllBytes(source), extension, false);
        var file = File.ReadAllBytes(source);
        byte[] rgba;
        if (extension.Equals(".tga", StringComparison.OrdinalIgnoreCase) && MaterialLibrary.TryDecodeTga(file, out int tw, out int th, out var pixels))
            (w, h, rgba) = (tw, th, pixels);
        else
        {
            var image = ImageResult.FromMemory(file, ColorComponents.RedGreenBlueAlpha);
            (w, h, rgba) = (image.Width, image.Height, image.Data);
        }
        double scale = (double)maxSize / Math.Max(w, h);
        int nw = Math.Max(1, (int)Math.Round(w * scale)), nh = Math.Max(1, (int)Math.Round(h * scale));
        return (MaterialLibrary.EncodePng(nw, nh, Shrink(rgba, w, h, nw, nh)), ".png", true);
    }

    /// <summary>The first free "name_2" style file name (no extension yet), and takes it.</summary>
    static string Free(string name, HashSet<string> taken)
    {
        for (int n = 2; ; n++)
        {
            var candidate = $"{name}_{n}";
            if (taken.Add(candidate)) return candidate + ".";
        }
    }

    /// <summary>What a file clashes under: its material name, or for a map no material lists, its own.</summary>
    static string Key(string file)
    {
        var stem = Path.GetFileNameWithoutExtension(file);
        return MaterialLibrary.MaterialName(stem) ?? stem;
    }

    static Dictionary<string, string> Existing(string folder)
    {
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(folder))
            foreach (var file in Images(folder).Order(StringComparer.OrdinalIgnoreCase)) found.TryAdd(Key(file), file);
        return found;
    }

    static IEnumerable<string> Images(string folder) => Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
        .Where(f => IsImage(f) && !MaterialLibrary.InDotFolder(Path.GetRelativePath(folder, f)));

    static bool IsImage(string file) => MaterialLibrary.Extensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase);
}
