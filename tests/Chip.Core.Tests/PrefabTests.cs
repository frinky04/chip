using System.Numerics;
using Chip.Core.Geometry;
using Chip.Core.IO;
using Chip.Core.Scene;
using Chip.Core.Texturing;

namespace Chip.Core.Tests;

/// <summary>Prefab files: found in the project, placed as linked copies, written when a copy is edited, read back anywhere.</summary>
public sealed class PrefabTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "chip-prefab-" + Guid.NewGuid().ToString("N"));

    public PrefabTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "levels"));
        File.WriteAllText(Path.Combine(_root, GameProject.FileName), """{ "version": 1, "prefabs": { "default": "props" } }""");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    string Level(string name) => Path.Combine(_root, "levels", name + ".chip");

    static PolyMesh Box(Vector3 min, Vector3 max) => MeshOps.CreateBox(min, max, "dev_a", TextureMapping.Default);

    static void AssertNear(Vector3 expected, Vector3 actual) =>
        Assert.True(Vector3.Distance(expected, actual) < 1e-3f, $"expected {expected}, got {actual}");

    /// <summary>A level with a crate made into a prefab saved at props/crate.prefab.chip, and a second copy at x = 5.</summary>
    (Session Session, InstanceDefinition Def, string File) LevelWithPrefab(string level = "a")
    {
        var doc = new Document();
        var crate = doc.Add(Box(new Vector3(-0.5f, 0, -0.5f), new Vector3(0.5f, 1, 0.5f)));
        var s = new Session();
        s.Reset(doc, Level(level));
        var file = Path.Combine(Prefabs.DefaultFolder(_root), "crate" + Prefabs.Extension);
        s.Do("Make prefab", () =>
        {
            var copy = InstanceOps.Make(s.Doc, [crate.Id])!;
            var (def, text) = Prefabs.Promote(s.Doc.FindDefinition(copy.Instance)!, _root, file);
            s.Doc.Definitions = [def];
            s.WriteFile(file, text);
            InstanceOps.Place(s.Doc, def, ObjectOrigin.At(new Vector3(5, 0, 0)));
        });
        return (s, s.Doc.Definitions[0], file);
    }

    static PolyMesh Part(Session s, Group copy) => s.Doc.Find(InstanceOps.MeshIds(s.Doc, copy)[0])!;

    [Fact]
    public void A_promoted_prefab_is_a_file_in_the_default_folder_and_the_project_finds_it()
    {
        var (_, def, file) = LevelWithPrefab();
        Assert.True(File.Exists(file));
        Assert.Equal("props/crate.prefab.chip", def.Source!.Path);
        Assert.Equal("crate", def.Name);
        var found = Assert.Single(Prefabs.Scan(_root));
        Assert.Equal(def.Source.Guid, found.Guid);
        Assert.Equal("props/crate.prefab.chip", found.RelativePath);
        // It's an ordinary level too: its meshes are the parts, around its origin.
        var prefab = DocumentSerializer.Load(file);
        Assert.Equal(def.Source.Guid, prefab.Prefab!.Guid);
        AssertNear(new Vector3(-0.5f, 0, -0.5f), prefab.Meshes[0].Bounds().Min);
    }

    [Fact]
    public void Editing_a_copy_writes_the_prefab_and_undo_puts_it_back()
    {
        var (s, def, file) = LevelWithPrefab();
        string before = File.ReadAllText(file);
        var mesh = Part(s, InstanceOps.Copies(s.Doc, def.Id)[0]);
        int top = Enumerable.Range(0, mesh.Positions.Count).First(i => mesh.Positions[i].Y > 0.5f);
        s.Do("Raise", [mesh.Id], () => mesh.Positions[top] += new Vector3(0, 2, 0));
        var written = DocumentSerializer.Load(file);
        Assert.Equal(3, written.Meshes[0].Bounds().Max.Y, 3);
        Assert.Equal(def.Source!.Guid, written.Prefab!.Guid);

        Assert.True(s.Undo());
        Assert.Equal(before, File.ReadAllText(file));
    }

    [Fact]
    public void Another_level_picks_up_the_edit_when_it_opens()
    {
        var (s, def, file) = LevelWithPrefab();
        File.WriteAllText(Level("a"), DocumentSerializer.Serialize(s.Doc));
        // Level b uses the same prefab.
        var b = new Session();
        b.Reset(new Document(), Level("b"));
        b.Do("Place", () =>
        {
            var d = Prefabs.Load(b.Doc, _root, file);
            b.Doc.Definitions = [d];
            InstanceOps.Place(b.Doc, d, ObjectOrigin.At(new Vector3(0, 0, 10)));
        });
        File.WriteAllText(Level("b"), DocumentSerializer.Serialize(b.Doc));

        // Edit the prefab through level a.
        var mesh = Part(s, InstanceOps.Copies(s.Doc, def.Id)[1]);
        int top = Enumerable.Range(0, mesh.Positions.Count).First(i => mesh.Positions[i].Y > 0.5f);
        s.Do("Raise", [mesh.Id], () => mesh.Positions[top] += new Vector3(0, 2, 0));

        // Opening level b again shows the new shape, though b's file still has the old one.
        var reopened = new Session();
        reopened.Reset(DocumentSerializer.Load(Level("b")), Level("b"));
        Assert.Empty(reopened.PrefabProblems);
        var copy = reopened.Doc.Groups.Single(g => g.IsInstance);
        Assert.Equal(3, Part(reopened, copy).Bounds().Max.Y, 3);
        Assert.Equal(10, Part(reopened, copy).Bounds().Min.Z + 0.5f, 3); // still where b put it
    }

    [Fact]
    public void A_moved_prefab_is_found_by_its_id_and_a_missing_one_keeps_its_last_shape()
    {
        var (s, def, file) = LevelWithPrefab();
        File.WriteAllText(Level("a"), DocumentSerializer.Serialize(s.Doc));
        Directory.CreateDirectory(Path.Combine(_root, "props", "crates"));
        var moved = Path.Combine(_root, "props", "crates", "wooden" + Prefabs.Extension);
        File.Move(file, moved);

        var reopened = new Session();
        reopened.Reset(DocumentSerializer.Load(Level("a")), Level("a"));
        Assert.Empty(reopened.PrefabProblems);
        Assert.Equal("props/crates/wooden.prefab.chip", reopened.Doc.Definitions[0].Source!.Path);

        File.Delete(moved);
        var missing = new Session();
        missing.Reset(DocumentSerializer.Load(Level("a")), Level("a"));
        Assert.Contains("missing", Assert.Single(missing.PrefabProblems));
        Assert.Equal(2, missing.Doc.Groups.Count(g => g.IsInstance));
        Assert.Single(missing.Doc.Definitions[0].Parts);
    }

    [Fact]
    public void A_prefab_edited_on_its_own_updates_a_level_on_refresh()
    {
        var (s, def, file) = LevelWithPrefab();
        // Open the prefab file as a level, add a lid, save.
        var prefab = DocumentSerializer.Load(file);
        prefab.Add(Box(new Vector3(-0.5f, 1, -0.5f), new Vector3(0.5f, 1.1f, 0.5f)));
        DocumentSerializer.Save(prefab, file);
        string saved = File.ReadAllText(file);

        var status = default(Prefabs.Status);
        s.Do("Reload", () => status = Prefabs.Refresh(s.Doc, _root).Single().Status);
        Assert.Equal(Prefabs.Status.Updated, status);
        foreach (var copy in InstanceOps.Copies(s.Doc, def.Id)) Assert.Equal(2, InstanceOps.MeshIds(s.Doc, copy).Count);
        // Reading it didn't write it back.
        Assert.Equal(saved, File.ReadAllText(file));
    }

    [Fact]
    public void Opening_a_prefab_sets_the_level_aside_and_going_back_restores_it()
    {
        var (s, def, file) = LevelWithPrefab();
        var copy = InstanceOps.Copies(s.Doc, def.Id)[0];
        s.Sel.Add(ElementRef.Object(InstanceOps.MeshIds(s.Doc, copy)[0]));
        var level = s.Doc;
        Assert.True(s.Dirty);
        Assert.Equal("Make prefab", s.UndoLabel);

        s.Push(DocumentSerializer.Load(file), file);
        Assert.True(s.IsNested);
        Assert.Equal(Level("a"), s.ParentPath);
        Assert.False(s.Dirty);
        Assert.True(s.AnyDirty); // the level still has unsaved changes
        Assert.Null(s.UndoLabel);
        var mesh = s.Doc.Meshes[0];
        s.Do("Raise", [mesh.Id], () => mesh.Positions[2] += new Vector3(0, 1, 0));
        DocumentSerializer.Save(s.Doc, file);

        Assert.True(s.Pop());
        Assert.False(s.IsNested);
        Assert.Same(level, s.Doc);
        Assert.True(s.Dirty);
        Assert.Equal("Make prefab", s.UndoLabel);
        Assert.Single(s.Sel.Items);
        Assert.False(s.Pop());

        // The level then takes the prefab as saved.
        s.Do("Update", () => Prefabs.Refresh(s.Doc, _root));
        Assert.Equal(2, Part(s, InstanceOps.Copies(s.Doc, def.Id)[0]).Bounds().Max.Y, 3);
    }

    [Fact]
    public void Project_prefab_roots_limit_the_search()
    {
        LevelWithPrefab();
        Directory.CreateDirectory(Path.Combine(_root, "elsewhere"));
        File.Copy(Path.Combine(_root, "props", "crate" + Prefabs.Extension), Path.Combine(_root, "elsewhere", "other" + Prefabs.Extension));
        Assert.Equal(2, Prefabs.Scan(_root).Count);
        File.WriteAllText(Path.Combine(_root, GameProject.FileName), """{ "version": 1, "prefabs": { "roots": ["props"] } }""");
        Assert.Equal("props/crate.prefab.chip", Assert.Single(Prefabs.Scan(_root)).RelativePath);
    }

    [Fact]
    public void A_level_outside_any_project_uses_its_own_folder()
    {
        var lone = Path.Combine(Path.GetTempPath(), "chip-lone-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(lone);
        try { Assert.Equal(lone, Prefabs.RootFor(Path.Combine(lone, "level.chip"))); }
        finally { Directory.Delete(lone, true); }
        Assert.Null(Prefabs.RootFor(null));
    }
}
