using Chip.Editor.Features.Shell;
using Chip.Editor.Framework;

namespace Chip.Editor.Tests;

public class PaletteTests
{
    static Command Cmd(string id, string label, string? menu = null) => new() { Id = id, Label = label, Menu = menu, Execute = () => { } };

    static readonly Command[] Sample =
    [
        Cmd("file.save", "Save", "File"),
        Cmd("file.saveAs", "Save as...", "File"),
        Cmd("select.all", "Select all", "Select"),
        Cmd("mesh.flip", "Flip normals", "Mesh"),
        Cmd("view.focus", "Focus selection", "View"),
        Cmd("view.frameAll", "Frame everything", "View"),
        Cmd("tool.gizmoRotate", "Rotate", "Tools"),
        Cmd("edit.undo", "Undo", "Edit"),
        Cmd("view.gridSmaller", "Smaller grid", "View"),
    ];

    static List<string> Ids(string query, params string[] recent) => PaletteSearch.Rank(query, Sample, recent).Select(c => c.Id).ToList();

    [Fact]
    public void Exact_and_prefix_label_matches_rank_first()
    {
        var ids = Ids("save");
        Assert.Equal(["file.save", "file.saveAs"], ids.Take(2));
        Assert.Equal("edit.undo", Ids("undo")[0]);
        Assert.Equal("file.save", Ids("sa")[0]);
    }

    [Fact]
    public void Word_initials_beat_scattered_letters()
    {
        var ids = Ids("fn");
        Assert.Equal("mesh.flip", ids[0]);
        Assert.Contains("view.focus", ids); // f…n still matches, just lower
    }

    [Fact]
    public void Menu_and_id_are_searchable()
    {
        Assert.Equal("mesh.flip", Ids("mesh flip")[0]);
        Assert.Equal("tool.gizmoRotate", Ids("gizmo")[0]);
        Assert.Equal("view.gridSmaller", Ids("grid")[0]);
    }

    [Fact]
    public void Non_matches_are_excluded()
    {
        Assert.Empty(Ids("xyzzy"));
        Assert.DoesNotContain("edit.undo", Ids("save"));
    }

    [Fact]
    public void Empty_query_lists_recent_commands_first()
    {
        var ids = Ids("", "view.focus", "edit.undo");
        Assert.Equal(["view.focus", "edit.undo"], ids.Take(2));
        Assert.Equal(Sample.Length, ids.Count);
        Assert.Equal("file.save", ids[2]); // then menu order (File first), then label
    }

    [Fact]
    public void Recent_command_wins_a_tie()
    {
        Command[] twins = [Cmd("view.snapA", "Snap", "View"), Cmd("mesh.snapB", "Snap", "Mesh")];
        Assert.Equal("view.snapA", PaletteSearch.Rank("snap", twins, [])[0].Id);
        Assert.Equal("mesh.snapB", PaletteSearch.Rank("snap", twins, ["mesh.snapB"])[0].Id);
    }

    [Fact]
    public void Match_scores_word_starts_higher()
    {
        Assert.True(PaletteSearch.Match("sa", "Select all") > PaletteSearch.Match("sa", "Mesh tablets"));
        Assert.Null(PaletteSearch.Match("ba", "ab"));
        Assert.Equal(0, PaletteSearch.Match("", "anything"));
    }

    [Fact]
    public void Every_registered_command_is_reachable_by_its_label()
    {
        var ctx = RegistryTests.Headless();
        foreach (var c in ctx.Commands.All)
            Assert.Contains(c, PaletteSearch.Rank(c.Label, ctx.Commands.All, []).Take(5));
    }
}
