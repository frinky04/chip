using Chip.Editor.Framework;
using Chip.Editor.Settings;

namespace Chip.Editor.Tests;

public sealed class SettingsTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "chip-settings-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Missing_file_gives_defaults()
    {
        var store = new SettingsStore(_dir);
        var s = store.Current;
        Assert.Null(store.LoadError);
        Assert.Null(s.UiScale);
        Assert.Equal(0.25f, s.GridDefault);
        Assert.Equal(60, s.AutosaveSeconds);
        Assert.Empty(s.RecentFiles);
        Assert.Empty(s.Hotkeys);
        Assert.Equal(AppSettings.DefaultInspectorWidth, s.InspectorWidth);
        Assert.Equal(AppSettings.DefaultToolStripWidth, s.ToolStripWidth);
    }

    [Fact]
    public void Settings_round_trip()
    {
        var store = new SettingsStore(_dir);
        var s = store.Current;
        s.UiScale = 1.5f;
        s.GridDefault = 1;
        s.AutosaveSeconds = 300;
        s.TextureFolders.Add(@"D:\textures");
        s.AddRecentFile(Path.Combine(_dir, "a.chip"));
        s.AddRecentCommand("view.focus");
        s.Hotkeys["view.focus"] = ["Shift+F2"];
        s.Hotkeys["edit.hide"] = [];
        s.InspectorWidth = 400;
        Assert.Null(store.Save());
        Assert.False(File.Exists(store.FilePath + ".tmp"));

        var loaded = new SettingsStore(_dir).Current;
        Assert.Equal(1.5f, loaded.UiScale);
        Assert.Equal(1f, loaded.GridDefault);
        Assert.Equal(300, loaded.AutosaveSeconds);
        Assert.Equal([@"D:\textures"], loaded.TextureFolders);
        Assert.Equal([Path.Combine(_dir, "a.chip")], loaded.RecentFiles);
        Assert.Equal(["view.focus"], loaded.RecentCommands);
        Assert.Equal(["Shift+F2"], loaded.Hotkeys["view.focus"]);
        Assert.Empty(loaded.Hotkeys["edit.hide"]);
        Assert.Equal(400, loaded.InspectorWidth);
    }

    [Fact]
    public void Corrupt_file_falls_back_to_defaults_and_is_kept()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "settings.json"), "{ not json");
        var store = new SettingsStore(_dir);
        Assert.NotNull(store.LoadError);
        Assert.Equal(0.25f, store.Current.GridDefault);
        Assert.True(File.Exists(Path.Combine(_dir, "settings.json.bad")));
    }

    [Fact]
    public void Out_of_range_values_are_repaired()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "settings.json"),
            """{ "UiScale": 40, "GridDefault": 0.3, "AutosaveSeconds": 2, "InspectorWidth": 5000, "ToolStripWidth": -1, "RecentFiles": null, "Hotkeys": { "view.focus": null } }""");
        var s = new SettingsStore(_dir).Current;
        Assert.Null(s.UiScale);
        Assert.Equal(0.25f, s.GridDefault);
        Assert.Equal(10, s.AutosaveSeconds);
        Assert.Equal(720, s.InspectorWidth);
        Assert.Equal(AppSettings.DefaultToolStripWidth, s.ToolStripWidth);
        Assert.Empty(s.RecentFiles);
        Assert.Empty(s.Hotkeys["view.focus"]);
    }

    [Fact]
    public void Recent_files_are_unique_newest_first_and_capped()
    {
        var s = new AppSettings();
        for (int i = 0; i < 15; i++) s.AddRecentFile(Path.Combine(_dir, $"{i}.chip"));
        s.AddRecentFile(Path.Combine(_dir, "12.CHIP")); // same file, different case
        Assert.Equal(AppSettings.MaxRecentFiles, s.RecentFiles.Count);
        Assert.Equal(Path.Combine(_dir, "12.CHIP"), s.RecentFiles[0]);
        Assert.Equal(Path.Combine(_dir, "14.chip"), s.RecentFiles[1]);
        Assert.Single(s.RecentFiles, p => p.EndsWith("12.chip", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void In_memory_store_never_touches_disk()
    {
        var store = SettingsStore.InMemory();
        store.Current.GridDefault = 2;
        Assert.Null(store.Save());
        Assert.Null(store.FilePath);
    }

    // ---- Hotkey overrides ----

    [Fact]
    public void Overrides_rebind_commands()
    {
        var ctx = RegistryTests.Headless();
        var errors = ctx.Commands.ApplyHotkeyOverrides(new Dictionary<string, string[]> { ["view.focus"] = ["Shift+F2"], ["edit.hide"] = [] });
        Assert.Empty(errors);
        Assert.Equal([Hotkey.Parse("Shift+F2")], ctx.Commands.Find("view.focus")!.Hotkeys);
        Assert.Empty(ctx.Commands.Find("edit.hide")!.Hotkeys);
        Assert.Equal([Hotkey.Parse("F")], ctx.Commands.Find("view.focus")!.DefaultHotkeys);
        Assert.Equal("view.focus", ctx.Commands.Match(h => h == Hotkey.Parse("Shift+F2"), false)?.Id);
        Assert.Null(ctx.Commands.Match(h => h == Hotkey.Parse("F"), false));
        Assert.Empty(ctx.Commands.Conflicts());

        var saved = ctx.Commands.HotkeyOverrides();
        Assert.Equal(["Shift+F2"], saved["view.focus"]);
        Assert.Empty(saved["edit.hide"]);
        Assert.Equal(2, saved.Count);
    }

    [Fact]
    public void Overrides_can_swap_two_shortcuts()
    {
        var ctx = RegistryTests.Headless();
        var errors = ctx.Commands.ApplyHotkeyOverrides(new Dictionary<string, string[]> { ["view.gridSmaller"] = ["]"], ["view.gridLarger"] = ["["] });
        Assert.Empty(errors);
        Assert.Equal("view.gridSmaller", ctx.Commands.Match(h => h == Hotkey.Parse("]"), false)?.Id);
        Assert.Empty(ctx.Commands.Conflicts());
    }

    [Fact]
    public void Conflicting_override_is_rejected_with_a_message()
    {
        var ctx = RegistryTests.Headless();
        var errors = ctx.Commands.ApplyHotkeyOverrides(new Dictionary<string, string[]> { ["view.focus"] = ["Ctrl+S"] });
        var error = Assert.Single(errors);
        Assert.Contains("Ctrl+S is already used by Save", error);
        Assert.Equal([Hotkey.Parse("F")], ctx.Commands.Find("view.focus")!.Hotkeys); // kept its default
        Assert.Equal("file.save", ctx.Commands.Match(h => h == Hotkey.Parse("Ctrl+S"), false)?.Id);
        Assert.Empty(ctx.Commands.Conflicts());
    }

    [Fact]
    public void Unknown_commands_and_bad_keys_are_reported_not_thrown()
    {
        var ctx = RegistryTests.Headless();
        var errors = ctx.Commands.ApplyHotkeyOverrides(new Dictionary<string, string[]> { ["nope.missing"] = ["F9"], ["view.focus"] = ["Hyper+Q"] });
        Assert.Equal(2, errors.Count);
        Assert.Equal([Hotkey.Parse("F")], ctx.Commands.Find("view.focus")!.Hotkeys);
    }

    [Fact]
    public void Rebind_rejects_conflicts_and_reset_restores_defaults()
    {
        var c = RegistryTests.Headless().Commands;
        Assert.Contains("already used by Undo", c.Rebind("view.focus", [Hotkey.Parse("Ctrl+Z")]));
        Assert.False(c.Find("view.focus")!.IsRebound);
        Assert.Null(c.Rebind("view.focus", [Hotkey.Parse("Ctrl+F")]));
        Assert.True(c.Find("view.focus")!.IsRebound);
        Assert.Null(c.ResetHotkeys("view.focus"));
        Assert.False(c.Find("view.focus")!.IsRebound);
        Assert.NotNull(c.Rebind("nope.missing", []));
    }

    [Theory]
    [InlineData("Ctrl+,")]
    [InlineData("Shift+NumMultiply")]
    [InlineData("Alt+F13")]
    [InlineData("Ctrl+ScrollLock")]
    [InlineData("'")]
    public void Captured_keys_round_trip_through_text(string text)
    {
        var h = Hotkey.Parse(text);
        Assert.Equal(h, Hotkey.Parse(h.ToString()));
    }

    [Fact]
    public void Shell_commands_are_registered()
    {
        var ctx = RegistryTests.Headless();
        Assert.Equal("Ctrl+P", ctx.Commands.Find("shell.palette")?.HotkeyText);
        Assert.Equal("Ctrl+Shift+P", ctx.Commands.Find("shell.preferences")?.HotkeyText);
        Assert.NotNull(ctx.Commands.Find("shell.resetLayout"));
    }
}
