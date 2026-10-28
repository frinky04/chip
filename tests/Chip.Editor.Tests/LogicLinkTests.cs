using System.Numerics;
using Chip.Core.Scene;
using Chip.Editor.Features.Logic;

namespace Chip.Editor.Tests;

[CollectionDefinition("Editor global entity classes", DisableParallelization = true)]
public class EditorGlobalClassesCollection;

/// <summary>Links between entities (references), the eyedropper that fills a reference, and what a reference accepts.</summary>
[Collection("Editor global entity classes")]
public sealed class LogicLinkTests : IDisposable
{
    const string Manifest = """
    { "version": 1, "classes": {
      "door": { "props": [ { "name": "partner", "type": "entity", "class": "door" } ] },
      "button": { "props": [ { "name": "doors", "type": "array", "of": "entity", "class": "door" }, { "name": "any", "type": "entity" } ] }
    } }
    """;

    public LogicLinkTests() => EntityClasses.Use(EntityCatalog.Parse(Manifest, null));
    public void Dispose() => EntityClasses.Reset();

    static (Framework.EditorContext Ctx, Entity A, Entity B, Entity Button) Level()
    {
        var doc = new Document();
        var a = doc.Add(EntityClasses.Create("door", new Vector3(0, 0, 0)));
        var b = doc.Add(EntityClasses.Create("door", new Vector3(4, 0, 0)));
        var button = doc.Add(EntityClasses.Create("button", new Vector3(2, 1, 0)));
        a.Properties["partner"] = EntityValues.Ref(b.Id);
        button.Properties["doors"] = EntityValues.List([EntityValues.Ref(a.Id), EntityValues.Ref(b.Id), EntityValues.Ref(999)]);
        return (RegistryTests.Headless(doc), a, b, button);
    }

    [Fact]
    public void Links_list_what_an_entity_points_at_and_what_points_at_it_skipping_missing_targets()
    {
        var (ctx, a, b, button) = Level();
        Assert.Equal([new Link(button.Id, a.Id, "Doors", false), new Link(button.Id, b.Id, "Doors", false)], EntityLinks.From(ctx, button.Id));
        Assert.Equal([new Link(a.Id, b.Id, "Partner", false), new Link(button.Id, b.Id, "Doors", false)], EntityLinks.To(ctx, b.Id));
        Assert.Empty(EntityLinks.To(ctx, button.Id));
    }

    [Fact]
    public void Links_follow_edits_and_undo()
    {
        var (ctx, a, b, _) = Level();
        Assert.Single(EntityLinks.From(ctx, a.Id));
        Features.Scene.EntitySection.Edit(ctx, a.Id, "Clear", x => x.Properties.Remove("partner") ? null : "none");
        Assert.Empty(EntityLinks.From(ctx, a.Id));
        ctx.Commands.Execute("edit.undo");
        Assert.Equal(b.Id, Assert.Single(EntityLinks.From(ctx, ctx.Doc.FindEntity(a.Id)!.Id)).To);
    }

    [Fact]
    public void A_reference_accepts_only_its_class_and_says_what_it_wants()
    {
        var (_, a, _, button) = Level();
        var partner = EntityClasses.Get("door").Prop("partner")!;
        var any = EntityClasses.Get("button").Prop("any")!;
        Assert.True(EntityRefs.Accepts(partner, a));
        Assert.False(EntityRefs.Accepts(partner, button));
        Assert.True(EntityRefs.Accepts(any, button));
        Assert.Equal("a door", EntityRefs.Wants(partner));
        Assert.Equal("doors", EntityRefs.Wants(partner, plural: true));
        Assert.Equal("an entity", EntityRefs.Wants(any));
    }

    [Fact]
    public void The_eyedropper_takes_over_the_viewport_and_hands_it_back_on_cancel()
    {
        var (ctx, a, _, _) = Level();
        var before = ctx.ActiveTool;
        Entity? picked = null;
        LogicFeature.Picker.Start(ctx, "field", "a door", e => e.ClassName == "door", e => picked = e);
        Assert.Same(LogicFeature.Picker, ctx.ActiveTool);
        Assert.True(LogicFeature.Picker.Armed(ctx, "field"));
        Assert.False(LogicFeature.Picker.Armed(ctx, "other"));
        LogicFeature.Picker.Cancel(ctx);
        Assert.Same(before, ctx.ActiveTool);
        Assert.Null(picked);
        Assert.False(LogicFeature.Picker.Armed(ctx, "field"));
    }

    [Fact]
    public void The_links_section_shows_only_for_one_linked_entity()
    {
        var (ctx, a, b, button) = Level();
        var section = ctx.Sections.OfType<LinksSection>().Single();
        ctx.Sel.Add(Core.Scene.ElementRef.Object(a.Id));
        Assert.True(section.Visible(ctx));
        ctx.Sel.Add(Core.Scene.ElementRef.Object(b.Id));
        Assert.False(section.Visible(ctx));
    }
}
