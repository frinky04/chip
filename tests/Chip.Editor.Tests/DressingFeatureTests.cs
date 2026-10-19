using Chip.Core.Dressing;
using Chip.Core.IO;
using Chip.Core.Scene;
using Chip.Editor.Features.Dressing;
using Chip.Editor.Framework;

namespace Chip.Editor.Tests;

/// <summary>Dressing in the editor: the sample level dresses, exports dressed, toggles view and reloads edited rules.</summary>
public class DressingFeatureTests : IDisposable
{
    readonly string _folder = Path.Combine(Path.GetTempPath(), "chip-dressing-tests-" + Guid.NewGuid().ToString("N"));
    double _now;

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    EditorContext Sample()
    {
        var level = DressingSample.Write(_folder);
        var session = new Session();
        session.Reset(DocumentSerializer.Load(level), level);
        var ctx = new EditorContext(session, new MaterialLibrary(), clock: () => _now);
        FeatureLoader.LoadAll(ctx);
        return ctx;
    }

    [Fact]
    public void A_level_without_rules_isnt_dressed()
    {
        var ctx = RegistryTests.Headless();
        Assert.Null(ctx.Dressed());
        Assert.Same(ctx.Doc, ctx.ExportDocument());
        Assert.False(ctx.Commands.Find("view.dressed")!.Enabled);
        Assert.False(ctx.Commands.Find("dressing.new")!.Enabled); // unsaved: nowhere to put the file
    }

    [Fact]
    public void The_sample_dresses_and_exports_its_dressing()
    {
        var ctx = Sample();
        var level = ctx.Dressed();
        Assert.NotNull(level);
        Assert.Equal(0, level!.ErrorCount);
        Assert.Equal(0, level.WarningCount);
        Assert.Equal(["baseboard", "pillar-bevel", "pipe"], level.Stats.Select(s => s.Rule));
        Assert.All(level.Stats, s => Assert.True(s.Outputs > 0, s.Rule));
        Assert.Same(level, ctx.Dressed()); // nothing changed: the same result, no work

        var export = ctx.ExportDocument();
        Assert.True(export.Meshes.Count > ctx.Doc.Meshes.Count);
        Assert.Contains(export.Meshes, m => m.Name.StartsWith("baseboard_"));
        Assert.Contains(export.Meshes, m => m.Name == "pillar_collision" && m.Collision == Core.Geometry.CollisionMode.Only);

        Assert.True(ctx.Commands.Find("view.dressed")!.Enabled);
        ctx.Commands.Execute("view.dressed");
        Assert.False(ctx.ShowDressed);
        Assert.NotNull(ctx.Dressed()); // the blockout view still dresses for export
    }

    [Fact]
    public void Editing_the_blockout_or_the_rules_file_redresses()
    {
        var ctx = Sample();
        var before = ctx.Dressed()!;
        var west = ctx.Doc.Meshes.First(m => m.Name == "west");
        ctx.Session.Do("Move", [west.Id], () =>
        {
            for (int i = 0; i < west.Positions.Count; i++) west.Positions[i] += new System.Numerics.Vector3(0.5f, 0, 0);
        });
        Assert.NotSame(before, ctx.Dressed());

        var rules = DressingRules.DefaultPathFor(ctx.Session.Path!);
        File.WriteAllText(rules, File.ReadAllText(rules).Replace("\"kind\": \"sweep\",\n      \"note\": \"Along", "\"kind\": \"sweep\", \"enabled\": false,\n      \"note\": \"Along"));
        File.SetLastWriteTimeUtc(rules, DateTime.UtcNow.AddSeconds(5));
        var stale = ctx.Dressed()!;
        Assert.True(stale.Stats.First(s => s.Rule == "baseboard").Enabled); // not checked yet
        _now += 1;                                                         // a quarter second later it is
        var reloaded = ctx.Dressed()!;
        Assert.False(reloaded.Stats.First(s => s.Rule == "baseboard").Enabled);
        Assert.Contains("Reloaded", ctx.StatusMessage);
    }

    [Fact]
    public void New_rules_file_writes_a_starter_and_schema_beside_the_level()
    {
        var ctx = Sample();
        var path = DressingRules.DefaultPathFor(ctx.Session.Path!);
        File.Delete(path);
        _now += 1;
        Assert.Null(ctx.Dressed());
        Assert.True(ctx.Commands.Execute("dressing.new"));
        Assert.True(File.Exists(path));
        Assert.True(File.Exists(Path.Combine(_folder, DressingSchema.FileName)));
        _now += 1;
        Assert.NotNull(ctx.Dressed());
    }
}
