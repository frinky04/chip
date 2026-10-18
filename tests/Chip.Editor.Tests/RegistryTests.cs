using System.Numerics;
using Chip.Core.Geometry;
using Chip.Core.Scene;
using Chip.Core.Texturing;
using Chip.Editor.Framework;
using ImGuiNET;

namespace Chip.Editor.Tests;

/// <summary>
/// Guards the extension seams every workstream relies on. Runs without a window or GL: features register
/// against a headless <see cref="EditorContext"/>.
/// </summary>
public class RegistryTests
{
    /// <summary>A fully registered context with a clock that doesn't touch ImGui.</summary>
    public static EditorContext Headless(Document? doc = null)
    {
        var session = new Session();
        session.Reset(doc ?? new Document(), null);
        var lib = new MaterialLibrary();
        lib.Add(new MaterialInfo("dev_a", null, 512, 512));
        lib.Add(new MaterialInfo("dev_b", null, 128, 128));
        var ctx = new EditorContext(session, lib, clock: () => 0);
        FeatureLoader.LoadAll(ctx);
        return ctx;
    }

    [Fact]
    public void All_features_register_without_errors()
    {
        var ctx = Headless();
        Assert.NotEmpty(ctx.Commands.All);
        Assert.NotNull(ctx.DefaultTool);
        Assert.Equal("select", ctx.DefaultTool!.Id);
        Assert.Contains(ctx.Tools, t => t.Id == "block");
        Assert.Contains(ctx.Sections, s => s.Id == "selection");
    }

    [Fact]
    public void No_two_commands_share_a_hotkey()
    {
        var conflicts = Headless().Commands.Conflicts().ToList();
        Assert.True(conflicts.Count == 0, "Hotkey conflicts: " + string.Join("; ", conflicts.Select(c => $"{c.Hotkey} -> {string.Join(", ", c.Commands)}")));
    }

    [Fact]
    public void Commands_are_well_formed()
    {
        var ctx = Headless();
        foreach (var c in ctx.Commands.All)
        {
            Assert.Matches(@"^[a-z]+\.[A-Za-z0-9]+$", c.Id);
            Assert.False(string.IsNullOrWhiteSpace(c.Label), c.Id);
            if (c.Menu != null) Assert.Contains(c.Menu, CommandRegistry.MenuOrder);
        }
        foreach (var t in ctx.Tools) Assert.NotNull(ctx.Commands.Find($"tool.{t.Id}"));
    }

    [Theory]
    [InlineData("Shift+Ctrl+G", ImGuiKey.G, true, true, false, "Ctrl+Shift+G")]
    [InlineData("Ctrl+Alt+Num5", ImGuiKey.Keypad5, true, false, true, "Ctrl+Alt+Num5")]
    [InlineData("[", ImGuiKey.LeftBracket, false, false, false, "[")]
    [InlineData("Del", ImGuiKey.Delete, false, false, false, "Del")]
    [InlineData("F1", ImGuiKey.F1, false, false, false, "F1")]
    [InlineData("Shift+Alt+0", ImGuiKey._0, false, true, true, "Shift+Alt+0")]
    [InlineData("Alt+Left", ImGuiKey.LeftArrow, false, false, true, "Alt+Left")]
    public void Hotkeys_parse_and_print(string text, ImGuiKey key, bool ctrl, bool shift, bool alt, string printed)
    {
        var h = Hotkey.Parse(text);
        Assert.Equal(new Hotkey(key, ctrl, shift, alt), h);
        Assert.Equal(printed, h.ToString());
        Assert.Equal(h, Hotkey.Parse(h.ToString()));
    }

    [Fact]
    public void Match_respects_interaction_and_enabled_state()
    {
        var ctx = Headless();
        var undo = Hotkey.Parse("Ctrl+Z");
        var delete = Hotkey.Parse("Del");
        Assert.Null(ctx.Commands.Match(h => h == delete, interacting: false)); // nothing selected: disabled
        Assert.Null(ctx.Commands.Match(h => h == undo, interacting: true));    // nothing to undo: disabled
        ctx.Session.Do("Add", () => ctx.Doc.Add(MeshOps.CreateBox(Vector3.Zero, Vector3.One, "dev_a", TextureMapping.Default)));
        Assert.Equal("edit.undo", ctx.Commands.Match(h => h == undo, interacting: true)?.Id);
        ctx.Sel.Add(ElementRef.Object(ctx.Doc.Meshes[0].Id));
        Assert.Null(ctx.Commands.Match(h => h == delete, interacting: true));  // delete waits for the tool to finish
        Assert.Equal("edit.delete", ctx.Commands.Match(h => h == delete, interacting: false)?.Id);
    }

    [Fact]
    public void Commands_edit_through_the_session()
    {
        var doc = new Document();
        doc.Add(MeshOps.CreateBox(Vector3.Zero, new Vector3(2, 1, 2), "dev_a", TextureMapping.Default));
        var ctx = Headless(doc);

        Assert.True(ctx.Commands.Execute("select.modeFace"));
        Assert.Equal(SelectMode.Face, ctx.Sel.Mode);
        ctx.Commands.Execute("select.all");
        Assert.Equal(6, ctx.Sel.Count);

        ctx.ActiveMaterial = "dev_b";
        ctx.Commands.Execute("texture.apply");
        Assert.All(ctx.Doc.Meshes[0].Faces, f => Assert.Equal("dev_b", f.Material));

        ctx.Commands.Execute("edit.undo");
        Assert.All(ctx.Doc.Meshes[0].Faces, f => Assert.Equal("dev_a", f.Material));

        ctx.Commands.Execute("tool.block");
        Assert.Equal("block", ctx.ActiveTool!.Id);
        ctx.Commands.Execute("edit.cancel"); // Esc leaves the tool
        Assert.Equal("select", ctx.ActiveTool!.Id);
        ctx.Commands.Execute("edit.cancel"); // then clears the selection
        Assert.True(ctx.Sel.IsEmpty);
    }

    [Fact]
    public void Mouse_bindings_cannot_collide()
    {
        var ctx = Headless();
        Assert.Throws<InvalidOperationException>(() => ctx.AddMouseBinding(
            new MouseBinding(ImGuiMouseButton.Right, false, true, false, "Texture", "duplicate", _ => { })));
    }
}
