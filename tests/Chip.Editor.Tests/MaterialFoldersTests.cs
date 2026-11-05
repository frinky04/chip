using System.Numerics;
using System.Text.Json.Nodes;
using Chip.Core.Geometry;
using Chip.Core.IO;
using Chip.Core.Scene;
using Chip.Core.Texturing;
using Chip.Editor.Features.Materials;
using Chip.Editor.Settings;

namespace Chip.Editor.Tests;

/// <summary>The library's folders, shared by the editor and <c>--export</c>, and what a headless export gets from them.</summary>
public class MaterialFoldersTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("chip-folders-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    string Folder(string name, params string[] images)
    {
        var folder = Path.Combine(_dir, name);
        Directory.CreateDirectory(folder);
        foreach (var image in images)
            File.WriteAllBytes(Path.Combine(folder, image + ".png"), MaterialLibrary.EncodePng(4, 4, Enumerable.Repeat((byte)200, 64).ToArray()));
        return folder;
    }

    /// <summary>A store reading a real settings.json, as <c>--export</c> does.</summary>
    SettingsStore Settings(params string[] folders)
    {
        var dir = Path.Combine(_dir, "settings");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "settings.json"),
            new JsonObject { ["TextureFolders"] = new JsonArray(folders.Select(f => (JsonNode)f).ToArray()) }.ToJsonString());
        return new SettingsStore(dir);
    }

    static Document BoxOf(string material)
    {
        var doc = new Document();
        doc.Add(MeshOps.CreateBox(Vector3.Zero, Vector3.One, material, TextureMapping.Default));
        return doc;
    }

    static JsonNode MaterialIn(byte[] glb, string name)
    {
        var json = JsonNode.Parse(System.Text.Encoding.UTF8.GetString(glb, 20, (int)BitConverter.ToUInt32(glb, 12)))!;
        return json["materials"]!.AsArray().Single(m => m!["name"]!.GetValue<string>() == name)!;
    }

    [Fact]
    public void The_order_is_bundled_then_settings_then_environment()
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "assets");
        Assert.Equal(
            [Path.Combine(bundled, "textures"), Path.Combine(bundled, "hotspots"), Path.Combine(bundled, "tools"), @"C:\mine", @"D:\env1", @"D:\env2"],
            MaterialFolders.All([@"C:\mine"], @"D:\env1;D:\env2"));
    }

    [Fact]
    public void The_editor_builds_its_library_from_the_same_list()
    {
        var ctx = RegistryTests.Headless();
        Assert.Equal(MaterialFolders.All([]), ctx.Materials.Folders);
    }

    [Fact]
    public void A_headless_export_finds_a_settings_folder_with_its_picture_and_side_car()
    {
        var folder = Folder("art", "fence");
        File.WriteAllText(Path.Combine(folder, MaterialLibrary.SidecarName), """{ "doubleSided": { "fence": true }, "texelDensity": { "fence": 128 } }""");
        var problems = new StringWriter();

        var lib = MaterialFolders.LoadHeadless(Settings(folder), problems, environment: "");
        var result = GlbExporter.Build(BoxOf("fence"), lib, null);

        Assert.Empty(problems.ToString());
        Assert.Empty(result.Warnings);
        Assert.Equal(128, lib.Get("fence").TexelsPerMeter);
        var fence = MaterialIn(result.Data, "fence");
        Assert.True(fence["doubleSided"]!.GetValue<bool>());
        Assert.NotNull(fence["pbrMetallicRoughness"]!["baseColorTexture"]);
        Assert.Null(fence["pbrMetallicRoughness"]!["baseColorFactor"]);
    }

    [Fact]
    public void A_headless_export_finds_an_environment_folder_and_settings_win_a_name_clash()
    {
        var mine = Folder("mine", "brick");
        var env = Folder("env", "brick", "tile");
        var lib = MaterialFolders.LoadHeadless(Settings(mine), new StringWriter(), environment: env);
        Assert.Equal(Path.Combine(mine, "brick.png"), lib.Get("brick").Path);
        Assert.Equal(Path.Combine(env, "tile.png"), lib.Get("tile").Path);
    }

    [Fact]
    public void A_headless_export_prefers_the_textures_beside_a_level_outside_any_project_and_writes_nothing()
    {
        var mine = Folder("mine", "brick");
        var project = Folder(Path.Combine("level", GameProject.DefaultTexturesPath), "brick");
        var problems = new StringWriter();

        var lib = MaterialFolders.LoadHeadless(Settings(mine), problems, environment: "", levelPath: Path.Combine(_dir, "level", "demo.chip"));

        Assert.Equal(Path.Combine(project, "brick.png"), lib.Get("brick").Path);
        Assert.Equal(project, lib.Folders[0]);
        Assert.Empty(problems.ToString());
        Assert.False(File.Exists(Path.Combine(project, MaterialLibrary.SidecarName)));
    }

    [Fact]
    public void A_project_folder_that_does_not_exist_yet_is_not_reported_missing()
    {
        var lib = new MaterialLibrary();
        lib.SetProjectFolder(Path.Combine(_dir, "not-yet"));
        Assert.Equal(0, lib.Rescan().MissingFolders);
        Assert.Empty(MaterialFolders.Missing(lib));
    }

    [Fact]
    public void A_level_finds_its_project_textures_from_the_project_else_beside_itself()
    {
        var level = Path.Combine(_dir, "game", "levels", "demo.chip");
        Assert.Equal(Path.Combine(_dir, "game", "levels", "textures"), MaterialFolders.Project(null, level));
        Assert.Null(MaterialFolders.Project(null, null));

        var root = Path.Combine(_dir, "game");
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, GameProject.FileName);
        Assert.Equal(Path.Combine(root, "textures"), MaterialFolders.Project(GameProject.Parse("{}", file), level));
        Assert.Equal(Path.Combine(root, "art", "tex"), MaterialFolders.Project(GameProject.Parse("""{ "textures": "art/tex" }""", file), level));
        Assert.Throws<InvalidDataException>(() => GameProject.Parse("""{ "textures": 3 }""", file));
    }

    [Theory]
    [InlineData("brick.png", true)]
    [InlineData("sub/brick.TGA", true)]
    [InlineData(MaterialLibrary.SidecarName, true)]
    [InlineData("sub", true)]
    [InlineData("brick.png.import", false)]
    [InlineData("~brick.tmp", false)]
    [InlineData(".texhist", false)]
    [InlineData(".texhist/brick.png", false)]
    [InlineData("sub\\.texhist\\v2\\brick.png", false)]
    public void Only_changes_that_can_matter_to_the_library_wake_it(string name, bool relevant) =>
        Assert.Equal(relevant, ProjectTextures.Relevant(name));

    [Fact]
    public void Saving_a_project_texture_reaches_the_library_without_changing_how_it_tiles()
    {
        var ctx = RegistryTests.Headless();
        var folder = Folder("project");
        var file = Path.Combine(folder, "brick.png");
        File.WriteAllBytes(file, MaterialLibrary.EncodePng(8, 8, new byte[8 * 8 * 4]));
        using var textures = new ProjectTextures();

        textures.Use(ctx, folder);
        textures.Poll(ctx); // starts watching
        var before = ctx.Materials.Get("brick");
        Assert.Equal((8, 8), (before.Width, before.Height));
        Assert.True(File.Exists(Path.Combine(folder, MaterialLibrary.SidecarName)));

        var edited = Path.Combine(_dir, "edited.png");
        File.WriteAllBytes(edited, MaterialLibrary.EncodePng(4, 4, new byte[4 * 4 * 4]));
        // Starting to watch queues a rescan of its own, so wait for the result, not for any rescan.
        File.Copy(edited, file, overwrite: true);
        var stop = Environment.TickCount64 + 10_000;
        while (ctx.Materials.Get("brick").Pixels == null && Environment.TickCount64 < stop)
        {
            textures.Poll(ctx);
            Thread.Sleep(20);
        }

        var after = ctx.Materials.Get("brick");
        Assert.Equal((4, 4), after.Pixels);
        Assert.Equal((8, 8), (after.Width, after.Height));
        Assert.NotEqual(before.Modified, after.Modified);
    }

    [Fact]
    public void Dropping_an_image_imports_it_and_makes_it_active()
    {
        var ctx = RegistryTests.Headless();
        var project = Path.Combine(_dir, "project");
        using var textures = new ProjectTextures();
        textures.Use(ctx, project);
        var importer = new TextureImporter(textures);
        var image = Path.Combine(Folder("drop", "fresh"), "fresh.png");

        importer.Drop(ctx, [image]);
        var stop = Environment.TickCount64 + 10_000;
        while (!ctx.Materials.Contains("fresh") && Environment.TickCount64 < stop)
        {
            importer.Update(ctx);
            Thread.Sleep(10);
        }

        Assert.True(ctx.Materials.Contains("fresh"));
        Assert.Equal("fresh", ctx.ActiveMaterial);
        Assert.Equal(4, ctx.Materials.Get("fresh").Width);
    }

    [Fact]
    public void Dropping_with_nowhere_to_put_it_copies_nothing()
    {
        var ctx = RegistryTests.Headless();
        var importer = new TextureImporter(new ProjectTextures());
        int before = ctx.Materials.All.Count;
        importer.Drop(ctx, [Path.Combine(Folder("drop", "fresh"), "fresh.png")]);
        importer.Update(ctx);
        Assert.Equal(before, ctx.Materials.All.Count);
    }

    [Fact]
    public void The_import_summary_says_what_happened_and_the_first_thing_that_did_not()
    {
        var summary = TextureImporter.Summary(new TextureImportResult(3, 1, 2, 1, "a", ["b.png: locked"]), ["c.webp isn't a PNG, JPEG or TGA"]);
        Assert.Equal("Imported 3 textures; 1 replaced; 2 scaled down; 1 other map, such as a normal map, isn't listed; 2 skipped, first: b.png: locked", summary);
    }

    [Fact]
    public void A_missing_folder_and_unreadable_settings_are_reported()
    {
        var problems = new StringWriter();
        MaterialFolders.LoadHeadless(Settings(Path.Combine(_dir, "gone")), problems, environment: "");
        Assert.Contains("gone", problems.ToString());

        var dir = Path.Combine(_dir, "bad");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "settings.json"), "{ not json");
        problems = new StringWriter();
        MaterialFolders.LoadHeadless(new SettingsStore(dir), problems, environment: "");
        Assert.Contains("Settings couldn't be read", problems.ToString());
    }
}
