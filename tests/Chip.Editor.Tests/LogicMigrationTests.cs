using Chip.Core.Scene;
using Chip.Editor.Features.Scene;

namespace Chip.Editor.Tests;

/// <summary>Opening a level in a project whose classes were renamed moves its values to the new names, as one undo step.</summary>
[Collection("Editor global entity classes")]
public sealed class LogicMigrationTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("chip-rename-").FullName;

    public void Dispose()
    {
        EntityClasses.Reset();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void A_renamed_class_and_property_carry_the_level_over_in_one_undo_step()
    {
        File.WriteAllText(Path.Combine(_dir, GameProject.FileName), """{ "version": 1 }""");
        Directory.CreateDirectory(Path.Combine(_dir, ".chip"));
        File.WriteAllText(Path.Combine(_dir, GameProject.DefaultManifest), """
        { "version": 1, "classes": { "door": { "was": ["func_door"], "props": [ { "name": "open_angle", "was": ["angle"], "type": "float", "default": 90 } ] } } }
        """);
        var level = Path.Combine(_dir, "level.chip");
        var doc = new Document();
        var e = doc.Add(new Entity { ClassName = "func_door" });
        e.Properties["angle"] = EntityValues.Of(45.0);
        Core.IO.DocumentSerializer.Save(doc, level);

        var ctx = RegistryTests.Headless();
        ctx.Session.Reset(Core.IO.DocumentSerializer.Load(level), level);
        var tracker = new ClassTracker();
        tracker.Update(ctx);

        var door = Assert.Single(ctx.Doc.Entities);
        Assert.Equal("door", door.ClassName);
        Assert.Equal(45.0, EntityValues.Number(door.Properties["open_angle"]));
        Assert.Equal("Apply Renamed Names", ctx.Session.UndoLabel);

        // Nothing left to rename: no second step, even after the classes reload.
        tracker.Update(ctx);
        ctx.Commands.Execute("edit.undo");
        Assert.Equal("func_door", Assert.Single(ctx.Doc.Entities).ClassName);
    }

    [Fact]
    public void Renames_wait_for_a_tool_gesture_to_finish_instead_of_being_skipped()
    {
        File.WriteAllText(Path.Combine(_dir, GameProject.FileName), """{ "version": 1 }""");
        Directory.CreateDirectory(Path.Combine(_dir, ".chip"));
        File.WriteAllText(Path.Combine(_dir, GameProject.DefaultManifest), """{ "version": 1, "classes": { "door": { "was": ["func_door"] } } }""");
        var level = Path.Combine(_dir, "level.chip");
        var doc = new Document();
        doc.Add(new Entity { ClassName = "func_door" });
        Core.IO.DocumentSerializer.Save(doc, level);

        var ctx = RegistryTests.Headless();
        ctx.Session.Reset(Core.IO.DocumentSerializer.Load(level), level);
        EntityClasses.Track(level);
        var tracker = new ClassTracker();
        var busy = new BusyTool();
        ctx.SetTool(busy);
        tracker.Migrate(ctx);
        Assert.Equal("func_door", Assert.Single(ctx.Doc.Entities).ClassName); // not mid-gesture

        busy.Busy = false;
        tracker.Migrate(ctx);
        Assert.Equal("door", Assert.Single(ctx.Doc.Entities).ClassName); // once it lets go
    }

    sealed class BusyTool : Framework.IViewportTool
    {
        public string Id => "test.busy";
        public string Name => "Busy";
        public UI.Icon Icon => UI.Icon.Select;
        public string? Hotkey => null;
        public int Order => 0;
        public bool Busy { get; set; } = true;
        public (string Key, string Action)[] Hints => [];
        public bool Cancel(Framework.EditorContext ctx) => false;
        public void Update(Framework.EditorContext ctx) { }
        public void Draw(Framework.EditorContext ctx, ImGuiNET.ImDrawListPtr dl, System.Numerics.Vector2 origin) { }
    }
}
