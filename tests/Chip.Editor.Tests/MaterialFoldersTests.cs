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
