using Chip.Core.Scene;

namespace Chip.Core.Tests;

/// <summary>Copying dropped images into a project's texture folder.</summary>
public class TextureImportTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("chip-import-").FullName;
    string Project => Path.Combine(_dir, "project");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    static byte[] Png(int w, int h, byte value = 200) => MaterialLibrary.EncodePng(w, h, Enumerable.Repeat(value, w * h * 4).ToArray());

    string File_(string relative, byte[] bytes)
    {
        var path = Path.GetFullPath(Path.Combine(_dir, relative));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    TextureImportResult Import(bool keepBoth = false, int maxSize = 0, params string[] paths) =>
        TextureImport.Run(TextureImport.Inspect(paths, Project), Project, keepBoth, maxSize);

    [Fact]
    public void A_file_lands_in_a_folder_that_did_not_exist_and_becomes_a_material()
    {
        var result = Import(paths: File_("drop/brick.png", Png(8, 8)));

        Assert.Equal(1, result.Imported);
        Assert.Equal("brick", result.FirstMaterial);
        var lib = new MaterialLibrary();
        lib.SetProjectFolder(Project);
        lib.Rescan();
        Assert.Equal(8, lib.Get("brick").Width);
    }

    [Fact]
    public void A_dropped_folder_keeps_its_layout_and_other_maps_are_counted_not_named_first()
    {
        File_("pack/walls/brick_normal.png", Png(4, 4));
        File_("pack/walls/brick_albedo.png", Png(4, 4));
        File_("pack/readme.txt", [1, 2, 3]);

        var result = Import(paths: Path.Combine(_dir, "pack"));

        Assert.Equal(2, result.Imported);
        Assert.Equal(1, result.OtherMaps);
        Assert.Equal("brick", result.FirstMaterial);
        Assert.True(File.Exists(Path.Combine(Project, "pack", "walls", "brick_albedo.png")));
        Assert.True(File.Exists(Path.Combine(Project, "pack", "walls", "brick_normal.png")));
        Assert.False(File.Exists(Path.Combine(Project, "pack", "readme.txt")));
    }

    /// <summary>A library with a texture of its own settings in a shared folder, on a level whose project has none yet.</summary>
    (MaterialLibrary Lib, string File) Shared()
    {
        var file = File_("shared/brick.png", Png(64, 32));
        var lib = new MaterialLibrary();
        lib.AddFolder(Path.GetDirectoryName(file)!);
        lib.SetProjectFolder(Project);
        lib.Rescan();
        lib.SetTexelDensity("brick", 128);
        lib.SetAlphaMode("brick", AlphaMode.Mask);
        lib.SetDoubleSided("brick", true);
        return (lib, file);
    }

    static void Carry(MaterialLibrary lib, ImportPlan plan)
    {
        foreach (var item in plan.Items)
            if (item.Material is { } m) Assert.True(lib.CarryToProject(m));
    }

    [Fact]
    public void A_library_texture_moves_to_the_project_with_its_settings_and_faces_stay_put()
    {
        var (lib, file) = Shared();
        var before = lib.Get("brick");
        float tile = before.TileWidth;
        var plan = TextureImport.Inspect([file], Project, lib);
        Assert.Equal("brick", plan.Items.Single().Material?.Name);

        Carry(lib, plan);
        TextureImport.Run(plan, Project, keepBoth: false, maxSize: 0);
        lib.Rescan();

        var after = lib.Get("brick");
        Assert.Equal(Path.Combine(Project, "brick.png"), after.Path);
        Assert.Equal(Project, after.Source);
        Assert.Equal(128f, after.TexelDensity);
        Assert.Equal(AlphaMode.Mask, after.Alpha);
        Assert.True(after.DoubleSided);
        Assert.Equal(tile, after.TileWidth);
        Assert.True(File.Exists(file)); // the original is left alone

        var fresh = new MaterialLibrary(); // and it's all on disk, for the next session and for --export
        fresh.SetProjectFolder(Project);
        fresh.Rescan();
        Assert.Equal(128f, fresh.Get("brick").TexelDensity);
    }

    [Fact]
    public void A_size_limit_on_a_library_texture_loses_detail_but_not_placement()
    {
        var (lib, file) = Shared();
        float tile = lib.Get("brick").TileWidth;
        var plan = TextureImport.Inspect([file], Project, lib);

        Carry(lib, plan);
        TextureImport.Run(plan, Project, keepBoth: false, maxSize: 16);
        lib.Rescan();
        lib.PinProjectSizes();

        var after = lib.Get("brick");
        Assert.Equal((16, 8), after.Pixels);
        Assert.Equal((64, 32), (after.Width, after.Height));
        Assert.Equal(tile, after.TileWidth);
    }

    [Fact]
    public void A_library_texture_that_was_resized_carries_the_size_it_tiles_at()
    {
        var (lib, file) = Shared();
        lib.CarryToProject(lib.Get("brick") with { Width = 256, Height = 128, Pixels = (64, 32) });
        TextureImport.Run(TextureImport.Inspect([file], Project), Project, keepBoth: false, maxSize: 0);
        lib.Rescan();

        Assert.Equal(256, lib.Get("brick").Width);
        Assert.Equal((64, 32), lib.Get("brick").Pixels);
    }

    [Fact]
    public void Only_the_image_a_material_uses_now_takes_it_over()
    {
        var (lib, file) = Shared();
        var other = File_("elsewhere/brick.png", Png(8, 8));
        var fresh = File_("elsewhere/fresh.png", Png(8, 8));
        Assert.Null(TextureImport.Inspect([other], Project, lib).Items.Single().Material);
        Assert.Null(TextureImport.Inspect([fresh], Project, lib).Items.Single().Material);
        Assert.NotNull(TextureImport.Inspect([file], Project, lib).Items.Single().Material);

        // Once the project has its own copy, the old file is just another image, and clashes.
        File_("project/brick.png", Png(8, 8));
        lib.Rescan();
        var item = TextureImport.Inspect([file], Project, lib).Items.Single();
        Assert.Null(item.Material);
        Assert.NotNull(item.Clash);
    }

    [Fact]
    public void Dot_folders_are_not_imported_and_do_not_count_as_clashes()
    {
        File_("project/.texhist/brick.png", Png(4, 4));
        File_("pack/.texhist/old.png", Png(4, 4));
        var brick = File_("pack/brick.png", Png(4, 4));

        var plan = TextureImport.Inspect([Path.Combine(_dir, "pack")], Project);

        var item = Assert.Single(plan.Items);
        Assert.Equal(brick, item.Source);
        Assert.Null(item.Clash);
    }

    [Fact]
    public void What_cannot_be_imported_is_rejected_with_a_reason()
    {
        var plan = TextureImport.Inspect(
        [
            File_("a/photo.webp", [1]), File_("a/broken.png", [1, 2, 3]), Path.Combine(_dir, "gone.png"),
            File_("a/brick.png", Png(4, 4)), File_("b/brick.png", Png(4, 4)),
        ], Project);

        Assert.Equal(["brick.png"], plan.Items.Select(i => i.Relative));
        Assert.Equal(4, plan.Rejected.Count);
        Assert.Contains(plan.Rejected, r => r.Contains("photo.webp") && r.Contains("PNG, JPEG or TGA"));
        Assert.Contains(plan.Rejected, r => r.Contains("broken.png") && r.Contains("can't be read"));
        Assert.Contains(plan.Rejected, r => r.Contains("gone.png") && r.Contains("wasn't found"));
        Assert.Contains(plan.Rejected, r => r.Contains("same name"));
    }

    [Fact]
    public void A_file_already_in_the_folder_is_left_alone()
    {
        var inside = File_("project/brick.png", Png(4, 4));
        Assert.Empty(TextureImport.Inspect([inside], Project).Items);
    }

    [Fact]
    public void Clashes_go_by_material_name_and_replacing_removes_the_old_file_where_it_was()
    {
        var old = File_("project/walls/brick.jpg", Png(8, 8)); // the name is what's checked, so PNG bytes will do
        var drop = File_("drop/brick_Albedo.png", Png(16, 16));

        var plan = TextureImport.Inspect([drop], Project);
        Assert.Equal(old, plan.Items.Single().Clash);

        var result = TextureImport.Run(plan, Project, keepBoth: false, maxSize: 0);

        Assert.Equal(1, result.Replaced);
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(Path.Combine(Project, "walls", "brick_Albedo.png")));
    }

    [Fact]
    public void Replacing_keeps_how_the_texture_tiles_because_the_size_is_pinned()
    {
        File_("project/brick.png", Png(8, 8));
        var lib = new MaterialLibrary();
        lib.SetProjectFolder(Project);
        lib.Rescan();
        lib.PinProjectSizes();

        Import(paths: File_("drop/brick.png", Png(32, 32)));
        lib.Rescan();

        Assert.Equal(8, lib.Get("brick").Width);
        Assert.Equal((32, 32), lib.Get("brick").Pixels);
    }

    [Fact]
    public void Keeping_both_takes_a_free_name_and_skips_maps()
    {
        File_("project/brick.png", Png(8, 8));
        File_("project/brick_2.png", Png(8, 8));
        File_("project/brick_normal.png", Png(8, 8));
        var result = Import(true, 0, File_("drop/brick.png", Png(16, 16)), File_("drop/brick_normal.png", Png(16, 16)));

        Assert.Equal(1, result.Imported);
        Assert.Equal("brick_3", result.FirstMaterial);
        Assert.True(MaterialLibrary.TryReadImageSize(Path.Combine(Project, "brick_3.png"), out int w, out _));
        Assert.Equal(16, w);
        Assert.True(MaterialLibrary.TryReadImageSize(Path.Combine(Project, "brick.png"), out w, out _));
        Assert.Equal(8, w);
    }

    [Fact]
    public void A_limit_scales_only_what_is_too_big_keeps_the_shape_and_writes_png()
    {
        var result = Import(false, 64, File_("drop/big.png", Png(256, 128)), File_("drop/small.png", Png(32, 32)));

        Assert.Equal(2, result.Imported);
        Assert.Equal(1, result.Resized);
        Assert.True(MaterialLibrary.TryReadImageSize(Path.Combine(Project, "big.png"), out int w, out int h));
        Assert.Equal((64, 32), (w, h));
        Assert.True(MaterialLibrary.TryReadImageSize(Path.Combine(Project, "small.png"), out w, out _));
        Assert.Equal(32, w);
    }

    [Fact]
    public void A_scaled_targa_arrives_as_a_png_under_the_same_material()
    {
        var header = new byte[18];
        header[2] = 2; header[12] = 8; header[14] = 8; header[16] = 32; // 8x8 uncompressed 32-bit
        var tga = header.Concat(Enumerable.Repeat((byte)255, 8 * 8 * 4)).ToArray();

        var result = Import(false, 4, File_("drop/brick.tga", tga));

        Assert.Empty(result.Problems);
        Assert.Equal("brick", result.FirstMaterial);
        Assert.True(MaterialLibrary.TryReadImageSize(Path.Combine(Project, "brick.png"), out int w, out _));
        Assert.Equal(4, w);
    }

    [Fact]
    public void A_file_that_fails_does_not_stop_the_others()
    {
        var plan = new ImportPlan([new ImportItem(Path.Combine(_dir, "vanished.png"), "vanished.png", null), new ImportItem(File_("drop/ok.png", Png(4, 4)), "ok.png", null)], []);
        var result = TextureImport.Run(plan, Project, false, 0);
        Assert.Equal(1, result.Imported);
        Assert.Contains("vanished.png", Assert.Single(result.Problems));
    }

    [Fact]
    public void Shrinking_averages_and_hidden_colour_does_not_bleed_into_edges()
    {
        byte[] pixels =
        [
            200, 100, 50, 255,   10, 250, 250, 0,
            100, 200, 150, 255,  10, 250, 250, 0,
        ];
        var result = TextureImport.Shrink(pixels, 2, 2, 1, 1);
        Assert.Equal([150, 150, 100, 127], result); // the colour of the visible texels; half the alpha
    }
}
