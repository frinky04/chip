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
        _feature = FeatureLoader.LoadAll(ctx).OfType<DressingFeature>().Single();
        return ctx;
    }

    DressingFeature _feature = null!;

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
        Assert.Equal(["baseboard", "pillar-bevel", "pipe", "railings", "stairs", "wall-lights", "rubble"], level.Stats.Select(s => s.Rule));
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
    public void Bake_makes_editable_meshes_in_one_undo_step()
    {
        var ctx = Sample();
        var rail = ctx.Doc.Meshes.First(m => m.Name == "rail-north");
        ctx.SetMode(SelectMode.Object);
        ctx.Sel.Add(ElementRef.Object(rail.Id));
        Assert.True(ctx.Commands.Find("dressing.bake")!.Enabled);
        int before = ctx.Doc.Meshes.Count;
        Assert.True(ctx.Commands.Execute("dressing.bake"));
        Assert.Equal(before + 1, ctx.Doc.Meshes.Count);
        Assert.Equal(Core.Geometry.CollisionMode.Only, ctx.Doc.Find(rail.Id)!.Collision);
        Assert.Equal("Bake dressing", ctx.Session.UndoLabel);
        var level = ctx.Dressed()!;
        Assert.DoesNotContain(level.Proxies, p => p.SourceId == rail.Id); // no longer a proxy
        ctx.Commands.Execute("edit.undo");
        Assert.Equal(before, ctx.Doc.Meshes.Count);
        Assert.Contains(ctx.Dressed()!.Proxies, p => p.SourceId == rail.Id);
    }

    [Fact]
    public void Per_mesh_overrides_redress_just_that_mesh()
    {
        var ctx = Sample();
        var rail = ctx.Doc.Meshes.First(m => m.Name == "rail-north");
        int Faces() => ctx.Dressed()!.Meshes.Where(m => ctx.Dressed()!.Origin(m).SourceId == rail.Id).Sum(m => m.Faces.Count);
        int before = Faces();
        ctx.Session.Do("Override", [rail.Id], () => rail.Dressing = rail.Dressing.WithParam("railings", "balusterEvery", System.Text.Json.Nodes.JsonValue.Create(0.4)));
        Assert.True(Faces() < before);
        ctx.Commands.Execute("edit.undo");
        Assert.Equal(before, Faces());
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

    // ---- Editing rules in the editor ----

    [Fact]
    public void Rule_edits_are_written_to_the_file_redress_at_once_and_undo()
    {
        var ctx = Sample();
        var path = DressingRules.DefaultPathFor(ctx.Session.Path!);
        string original = File.ReadAllText(path);
        Assert.True(_feature.EditRules("Switch off baseboard", w => w.SetEnabled("baseboard", false)));
        Assert.Equal("Switch off baseboard", ctx.Session.UndoLabel);
        Assert.False(ctx.Dressed()!.Stats.First(s => s.Rule == "baseboard").Enabled); // no quarter-second wait
        Assert.Contains("\"enabled\": false", File.ReadAllText(path));
        Assert.Empty(ctx.Dressed()!.Rules.Diagnostics);
        Assert.Equal(7, _feature.Workspace()!.Rules.Count);

        ctx.Commands.Execute("edit.undo");
        Assert.Equal(original, File.ReadAllText(path)); // the file as it was, comments and all
        Assert.True(ctx.Dressed()!.Stats.First(s => s.Rule == "baseboard").Enabled);
        ctx.Commands.Execute("edit.redo");
        Assert.False(ctx.Dressed()!.Stats.First(s => s.Rule == "baseboard").Enabled);
    }

    [Fact]
    public void Renaming_a_rule_carries_per_mesh_overrides_in_the_same_undo_step()
    {
        var ctx = Sample();
        var rail = ctx.Doc.Meshes.First(m => m.Name == "rail-north");
        ctx.Session.Do("Override", [rail.Id], () => rail.Dressing = rail.Dressing.WithParam("railings", "balusterEvery", System.Text.Json.Nodes.JsonValue.Create(0.4)));
        Assert.True(_feature.EditRules("Rename", w => w.Rename("railings", "balcony"), DressingOps.Mentioning(ctx.Doc, "railings"),
            () => DressingOps.RenameRule(ctx.Doc, "railings", "balcony")));
        Assert.NotNull(ctx.Doc.Find(rail.Id)!.Dressing.OverridesFor("balcony"));
        Assert.Contains(ctx.Dressed()!.Proxies, p => p.SourceId == rail.Id && p.Rule == "balcony");
        Assert.Equal(0, ctx.Dressed()!.ErrorCount);
        ctx.Commands.Execute("edit.undo");
        Assert.NotNull(ctx.Doc.Find(rail.Id)!.Dressing.OverridesFor("railings"));
        Assert.Contains(ctx.Dressed()!.Proxies, p => p.SourceId == rail.Id && p.Rule == "railings");
    }

    [Fact]
    public void Dress_with_tags_the_selection_and_adds_a_recipe_when_needed()
    {
        var ctx = Sample();
        var pillar = ctx.Doc.Meshes.First(m => m.Name == "plinth"); // no shape rule of its own, so it can become a railing
        ctx.SetMode(SelectMode.Object);
        ctx.Sel.Add(ElementRef.Object(pillar.Id));
        var ws = _feature.Workspace()!;

        // A new rule from a recipe, in one step with the tags.
        DressWithPopup.ApplyRecipe(ctx, _feature, ws, Recipes.Find("crown")!);
        Assert.Equal("Dress with Crown moulding", ctx.Session.UndoLabel);
        int sides = Enumerable.Range(0, pillar.Faces.Count).Count(f => MathF.Abs(pillar.FaceNormal(f).Y) < 0.3f);
        Assert.Equal(sides, ctx.Doc.Find(pillar.Id)!.Faces.Count(f => f.Tags.Contains("wall"))); // its sides, not its tops
        Assert.Contains(ctx.Dressed()!.Stats, s => s.Rule == "crown");
        Assert.Equal("crown", _feature.Window.Selected);

        // An existing rule: a box becomes a railing; again takes it out.
        var railing = _feature.Workspace()!.Find("railings")!;
        DressWithPopup.Apply(ctx, _feature, railing, _feature.Workspace()!);
        Assert.Contains(ctx.Dressed()!.Proxies, p => p.SourceId == pillar.Id);
        DressWithPopup.Apply(ctx, _feature, railing, _feature.Workspace()!);
        Assert.DoesNotContain(ctx.Dressed()!.Proxies, p => p.SourceId == pillar.Id);

        ctx.Commands.Execute("edit.undo");
        ctx.Commands.Execute("edit.undo");
        ctx.Commands.Execute("edit.undo");
        Assert.DoesNotContain(ctx.Dressed()!.Stats, s => s.Rule == "crown");
        Assert.Equal(0, ctx.Doc.Find(pillar.Id)!.Faces.Count(f => f.Tags.Contains("wall")));
    }

    [Fact]
    public void The_first_rule_creates_the_rules_file_for_a_saved_level()
    {
        var ctx = Sample();
        var path = DressingRules.DefaultPathFor(ctx.Session.Path!);
        File.Delete(path);
        _now += 1;
        Assert.Null(ctx.Dressed());
        Assert.Equal(path, _feature.EditPath);
        Assert.Empty(_feature.Workspace()!.Rules);
        Assert.True(_feature.EditRules("Add rule", w => { w.Add("baseboard", Recipes.Find("baseboard")!.Rule()); return true; }));
        Assert.True(File.Exists(path));
        Assert.Contains(ctx.Dressed()!.Stats, s => s.Rule == "baseboard" && s.Outputs > 0);
        ctx.Commands.Execute("edit.undo");
        Assert.False(File.Exists(path)); // undo takes the new file away again
    }

    [Fact]
    public void An_unsaved_level_has_nowhere_to_keep_rules()
    {
        var ctx = RegistryTests.Headless();
        var feature = ctx.Commands.Find("dressing.rules");
        Assert.NotNull(feature);
        Assert.False(ctx.Commands.Find("dressing.dressWith")!.Enabled); // nothing selected
    }

    [Fact]
    public void The_viewport_tints_proxies_in_the_blockout_view_the_hovered_rule_and_tags()
    {
        var ctx = Sample();
        Assert.Null(ctx.ViewportTints()); // dressed view, nothing hovered
        ctx.Commands.Execute("view.dressed");
        var stripes = ctx.ViewportTints()!;
        Assert.All(stripes, t => Assert.True(t.Striped && t.Face == -1));
        Assert.Same(stripes, ctx.ViewportTints()); // same list while nothing changes
        _feature.HighlightRule = "baseboard";
        var lit = ctx.ViewportTints()!;
        Assert.Equal(stripes.Count + ctx.Dressed()!.Targets("baseboard").Count, lit.Count);
        _feature.HighlightRule = null;
        ctx.Commands.Execute("view.tagColours");
        Assert.Contains(ctx.ViewportTints()!, t => t.Face >= 0 && !t.Striped);
    }
}
