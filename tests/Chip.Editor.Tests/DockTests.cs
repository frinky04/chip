using Chip.Core.Scene;
using Chip.Editor.Framework;
using Chip.Editor.Inspector;

namespace Chip.Editor.Tests;

/// <summary>The left dock's tabs and the hotkeys that show them, and what the inspector shows with nothing selected.</summary>
public class DockTests
{
    [Fact]
    public void The_dock_has_its_four_tabs_in_order_and_opens_on_materials()
    {
        var ctx = RegistryTests.Headless();
        Assert.Equal(["materials", "scene", "place", "dressing"], ctx.DockPanels.Select(p => p.Id));
        Assert.Equal("materials", ctx.DockTab);
        Assert.True(ctx.DockShowing("materials"));
        foreach (var p in ctx.DockPanels)
            Assert.True(p.Command == null || ctx.Commands.Find(p.Command) != null, $"{p.Id} names a command that doesn't exist");
    }

    [Theory]
    [InlineData("materials.browser", "materials", "T")]
    [InlineData("scene.outliner", "scene", "Ctrl+Shift+O")]
    [InlineData("dressing.rules", "dressing", "Ctrl+Shift+D")]
    public void A_tab_hotkey_shows_its_tab_and_again_collapses_the_dock(string command, string tab, string hotkey)
    {
        var ctx = RegistryTests.Headless();
        var c = ctx.Commands.Find(command)!;
        Assert.Equal(hotkey, c.HotkeyText);
        if (tab == "materials") ctx.ShowDock("scene");
        c.Execute();
        Assert.True(ctx.DockShowing(tab));
        Assert.True(c.IsChecked!());
        c.Execute();
        Assert.False(ctx.DockOpen);
        Assert.Equal(tab, ctx.DockTab); // reopening comes back to it
        c.Execute();
        Assert.True(ctx.DockShowing(tab));
    }

    [Fact]
    public void The_side_panel_toggle_keeps_the_tab()
    {
        var ctx = RegistryTests.Headless();
        ctx.ShowDock("place");
        ctx.Commands.Execute("shell.toggleDock");
        Assert.False(ctx.DockOpen);
        ctx.Commands.Execute("shell.toggleDock");
        Assert.True(ctx.DockShowing("place"));
        Assert.Equal("Ctrl+\\", ctx.Commands.Find("shell.toggleDock")!.HotkeyText);
    }

    [Fact]
    public void The_inspector_shows_the_level_with_nothing_selected_and_the_selection_otherwise()
    {
        var doc = new Document();
        var mesh = doc.Add(Core.Geometry.MeshOps.CreateBox(System.Numerics.Vector3.Zero, System.Numerics.Vector3.One, "dev_a", Core.Texturing.TextureMapping.Default));
        var ctx = RegistryTests.Headless(doc);
        Assert.True(Section("level").Visible(ctx));
        Assert.False(Section("selection").Visible(ctx));
        Assert.False(Section("actions").Visible(ctx));

        ctx.SetMode(SelectMode.Face);
        ctx.Sel.Add(ElementRef.Face(mesh.Id, 0));
        Assert.False(Section("level").Visible(ctx));
        Assert.True(Section("selection").Visible(ctx));
        Assert.True(Section("actions").Visible(ctx));
        Assert.True(Section("material").Visible(ctx));

        IInspectorSection Section(string id) => ctx.Sections.Single(s => s.Id == id);
    }

    [Fact]
    public void Every_action_button_is_a_real_command()
    {
        var ctx = RegistryTests.Headless();
        var field = typeof(ActionsSection).GetField("ByMode", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var byMode = (Dictionary<SelectMode, (string Command, string Label)[]>)field.GetValue(null)!;
        foreach (var (mode, items) in byMode)
            foreach (var (command, _) in items)
                Assert.True(ctx.Commands.Find(command) != null, $"{mode}: {command} isn't a command");
    }
}
