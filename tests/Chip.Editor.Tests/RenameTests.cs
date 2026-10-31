using System.Numerics;
using Chip.Core.Geometry;
using Chip.Core.Scene;
using Chip.Core.Texturing;
using Chip.Editor.Features.Scene;
using Chip.Editor.Features.SelectionTools;

namespace Chip.Editor.Tests;

/// <summary>Rename (F2): what panels offer, and the selected object it falls back to.</summary>
public class RenameTests
{
    static (Framework.EditorContext Ctx, int Crate, int Light) Level()
    {
        var doc = new Document();
        var crate = new PolyMesh { Name = "crate" };
        var box = MeshOps.CreateBox(Vector3.Zero, Vector3.One, "m", TextureMapping.Default);
        crate.Positions.AddRange(box.Positions);
        crate.Faces.AddRange(box.Faces);
        int crateId = doc.Add(crate).Id;
        int light = doc.Add(EntityClasses.Create("light_point", Vector3.One)).Id;
        return (RegistryTests.Headless(doc), crateId, light);
    }

    [Fact]
    public void A_hovered_offer_beats_a_focused_one_whatever_the_order()
    {
        var (ctx, _, _) = Level();
        string? ran = null;
        ctx.OfferRename(() => ran = "focused", hovered: false);
        ctx.OfferRename(() => ran = "hovered", hovered: true);
        ctx.OfferRename(() => ran = "late focused", hovered: false);
        Assert.True(ctx.Commands.Execute("edit.rename"));
        Assert.Equal("hovered", ran);
        Assert.False(ctx.HasRenameOffer); // used up
    }

    [Fact]
    public void Without_an_offer_it_renames_the_selected_object_and_needs_one()
    {
        var (ctx, crate, light) = Level();
        Assert.False(ctx.Commands.Find("edit.rename")!.Enabled);

        ctx.SetMode(SelectMode.Object);
        ctx.Sel.Items.Add(ElementRef.Object(crate));
        ctx.Sel.Items.Add(ElementRef.Object(light));
        Assert.Equal(light, Outliner.RenameTarget(ctx)); // no active object: the entity first

        ctx.Sel.Active = ElementRef.Object(crate);
        Assert.Equal(crate, Outliner.RenameTarget(ctx));
        ctx.DockOpen = false;
        Assert.True(ctx.Commands.Execute("edit.rename"));
        Assert.True(ctx.DockShowing(Outliner.PanelId)); // the field opens in the Scene tab
    }

    [Fact]
    public void In_face_mode_it_renames_the_mesh_holding_the_selection()
    {
        var (ctx, crate, _) = Level();
        ctx.SetMode(SelectMode.Face);
        ctx.Sel.Add(ElementRef.Face(crate, 2));
        Assert.Equal(crate, Outliner.RenameTarget(ctx));
    }

    [Fact]
    public void Selection_sets_rename_unless_empty_or_taken()
    {
        var (ctx, crate, light) = Level();
        var sets = SelectionSets.For(ctx);
        ctx.SetMode(SelectMode.Object);
        ctx.Sel.Add(ElementRef.Object(crate));
        sets.Save(ctx, "crates");
        ctx.Sel.Add(ElementRef.Object(light));
        sets.Save(ctx, "both");

        Assert.False(sets.Rename(ctx, sets.All[0], "  "));
        Assert.False(sets.Rename(ctx, sets.All[0], "both"));
        Assert.True(sets.Rename(ctx, sets.All[1], " everything "));
        Assert.Equal(["crates", "everything"], sets.All.Select(s => s.Name));
        Assert.Equal("everything", sets.Last!.Name); // the set Restore brings back keeps up with its name
    }
}
