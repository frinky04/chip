using Chip.Core.Scene;

namespace Chip.Editor.Tests;

public class TexelDensityEditorTests
{
    [Fact]
    public void The_material_library_follows_the_open_level_through_undo()
    {
        var ctx = RegistryTests.Headless(new Document { TexelDensity = 512 });
        ctx.SyncTexelDensity();
        Assert.Equal(512f, ctx.Materials.DefaultTexelsPerMeter);

        ctx.Session.Do("Texel Density", () => TexelDensityOps.Change(ctx.Doc, ctx.Materials, 64));
        ctx.SyncTexelDensity();
        Assert.Equal(64f, ctx.Materials.DefaultTexelsPerMeter);
        var material = ctx.Materials.All[0];
        if (material.TexelDensity == null) Assert.Equal(1f / 64, ctx.Materials.MappingFor(material.Name).ScaleU, 6); // new blocks

        ctx.Commands.Execute("edit.undo");
        ctx.SyncTexelDensity();
        Assert.Equal(512f, ctx.Materials.DefaultTexelsPerMeter);

        ctx.Session.Reset(new Document(), null); // File > New
        ctx.SyncTexelDensity();
        Assert.Equal(Document.NewLevelTexelDensity, ctx.Materials.DefaultTexelsPerMeter);
    }
}
