using System.Numerics;
using Chip.Core.Geometry;
using Chip.Core.Picking;
using Chip.Core.Scene;
using Chip.Core.Texturing;

namespace Chip.Core.Tests;

/// <summary>
/// Object-mode picking when things overlap: which object a click takes, what clicking again cycles to, and the pick
/// filter. Uses its own classes (a 10 m volume and a 2 m crate), so it shares the global entity classes collection.
/// </summary>
[Collection("Global entity classes")]
public class ObjectPickTests : IDisposable
{
    const string Manifest = """
        { "version": 1, "classes": {
            "volume": { "bounds": [[-5, -5, -5], [5, 5, 5]] },
            "crate": { "bounds": [[-1, -1, -1], [1, 1, 1]] },
            "pillar": { "bounds": [[-1, 0, -1], [1, 10, 1]] } } }
        """;

    public ObjectPickTests() => EntityClasses.Use(EntityCatalog.Parse(Manifest, null));

    public void Dispose() => EntityClasses.Reset();

    static ViewInfo View(Vector3 eye, Vector3 target)
    {
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(75 * MathF.PI / 180, 16 / 9f, 0.02f, 2000);
        return new ViewInfo(Matrix4x4.CreateLookAt(eye, target, Vector3.UnitY) * projection, new(1600, 900), eye);
    }

    static readonly ViewInfo Front = View(new(0, 0, 20), Vector3.Zero);

    static Vector2 At(ViewInfo view, Vector3 p)
    {
        Assert.True(view.Project(p, out var s));
        return s;
    }

    /// <summary>A slab facing the camera: x and y ±3, z from <paramref name="back"/> to <paramref name="front"/>.</summary>
    static PolyMesh Wall(float back, float front) => MeshOps.CreateBox(new(-3, -3, back), new(3, 3, front), "m", TextureMapping.Default);

    static Session Session(Document doc)
    {
        var s = new Session();
        s.Reset(doc, null);
        return s;
    }

    /// <summary>Both pickers, which must agree: the whole list, and the single pick (its first entry).</summary>
    static List<int> Pick(Document doc, ViewInfo view, Vector2 mouse, PickFilter filter = PickFilter.All)
    {
        var expected = Picker.PickObjects(doc, view, mouse, filter: filter);
        var cache = new PickCache(Session(doc));
        var list = new List<ElementRef>();
        cache.PickObjects(view, mouse, list, filter: filter);
        Assert.Equal(expected, list);
        var first = expected.Count > 0 ? expected[0] : (ElementRef?)null;
        Assert.Equal(first, Picker.Pick(doc, view, mouse, SelectMode.Object, filter: filter));
        Assert.Equal(first, cache.Pick(view, mouse, SelectMode.Object, filter: filter));
        return list.Select(e => e.Mesh).ToList();
    }

    [Fact]
    public void A_box_inside_a_bigger_box_wins()
    {
        var doc = new Document();
        var volume = doc.Add(EntityClasses.Create("volume", Vector3.Zero));
        var crate = doc.Add(EntityClasses.Create("crate", new(2, 0, 0)));
        // Through the crate's box, away from both glyphs. The ray enters the volume first but leaves the crate first.
        Assert.Equal([crate.Id, volume.Id], Pick(doc, Front, At(Front, new(2.6f, 0.6f, 1))));
    }

    [Fact]
    public void A_glyph_under_the_cursor_beats_a_box_in_front_of_it()
    {
        var doc = new Document();
        var volume = doc.Add(EntityClasses.Create("volume", Vector3.Zero));
        var crate = doc.Add(EntityClasses.Create("crate", new(2, 0, -8))); // behind the volume
        Assert.Equal([crate.Id, volume.Id], Pick(doc, Front, At(Front, crate.Position)));
    }

    [Fact]
    public void A_surface_inside_a_volume_wins_over_the_volume()
    {
        var doc = new Document();
        var volume = doc.Add(EntityClasses.Create("volume", Vector3.Zero));
        var wall = doc.Add(Wall(-1, 1));
        // The wireframe volume is see-through: the wall is what the user clicked. The volume is next.
        Assert.Equal([wall.Id, volume.Id], Pick(doc, Front, At(Front, new(2.6f, 0.6f, 1))));
    }

    [Fact]
    public void A_volume_the_camera_is_inside_comes_after_what_is_in_front()
    {
        var doc = new Document();
        var volume = doc.Add(EntityClasses.Create("volume", Vector3.Zero));
        var wall = doc.Add(Wall(-2, -1));
        var view = View(new(0, 0, 4), new(0, 0, -10));
        Assert.Equal([wall.Id, volume.Id], Pick(doc, view, At(view, new(1.5f, 1.5f, -1))));
    }

    [Fact]
    public void An_entity_behind_a_wall_is_next_and_the_entities_filter_picks_it_first()
    {
        var doc = new Document();
        var wall = doc.Add(Wall(-1, 1));
        var crate = doc.Add(EntityClasses.Create("crate", new(0, 0, -4)));
        var mouse = At(Front, crate.Position);
        Assert.Equal([wall.Id, crate.Id], Pick(doc, Front, mouse));
        Assert.Equal([crate.Id], Pick(doc, Front, mouse, PickFilter.Entities));
        Assert.Equal([wall.Id], Pick(doc, Front, mouse, PickFilter.Geometry));
    }

    [Fact]
    public void A_locked_wall_still_hides_entities_but_its_own_hit_is_skipped()
    {
        var doc = new Document();
        var wall = doc.Add(Wall(-1, 1));
        wall.Locked = true;
        var back = doc.Add(Wall(-9, -8));
        var crate = doc.Add(EntityClasses.Create("crate", new(0, 0, -4)));
        Assert.Equal([crate.Id, back.Id], Pick(doc, Front, At(Front, crate.Position)));
    }

    [Fact]
    public void A_mesh_entity_is_one_entry_and_counts_as_an_entity()
    {
        var doc = new Document();
        var front = doc.Add(Wall(-1, 1));
        var back = doc.Add(Wall(-5, -4));
        var plain = doc.Add(Wall(-9, -8));
        var door = MeshEntityOps.Tie(doc, [front.Id, back.Id], "crate")!;
        var mouse = At(Front, new(2, 2, 1)); // off the door's origin marker
        Assert.Equal([front.Id, plain.Id], Pick(doc, Front, mouse));
        Assert.Equal([front.Id], Pick(doc, Front, mouse, PickFilter.Entities));
        Assert.Equal([plain.Id], Pick(doc, Front, mouse, PickFilter.Geometry));
        // On the origin marker, the door's marker and both its meshes are still one entry.
        var onMarker = Pick(doc, Front, At(Front, door.Position));
        Assert.Single(onMarker, id => id == door.Id || id == front.Id || id == back.Id);
    }

    [Fact]
    public void Marquee_filters_objects_and_counts_entities_by_their_glyph()
    {
        var doc = new Document();
        var wall = doc.Add(Wall(-1, 1));
        var tall = doc.Add(EntityClasses.Create("pillar", new(0, -5, 0))); // origin at the bottom; glyph at the centre
        var cache = new PickCache(Session(doc));
        var centre = At(Front, Vector3.Zero);
        Vector2 a = centre - new Vector2(40), b = centre + new Vector2(40);
        foreach (var filter in Enum.GetValues<PickFilter>())
            Assert.Equal(Picker.Marquee(doc, Front, a, b, SelectMode.Object, filter: filter), cache.Marquee(Front, a, b, SelectMode.Object, filter: filter));
        Assert.Equal([wall.Id, tall.Id], cache.Marquee(Front, a, b, SelectMode.Object).Select(e => e.Mesh));
        Assert.Equal([tall.Id], cache.Marquee(Front, a, b, SelectMode.Object, filter: PickFilter.Entities).Select(e => e.Mesh));
        Assert.Equal([wall.Id], cache.Marquee(Front, a, b, SelectMode.Object, filter: PickFilter.Geometry).Select(e => e.Mesh));
    }

    [Fact]
    public void Select_all_follows_the_filter()
    {
        var doc = new Document();
        var wall = doc.Add(Wall(-1, 1));
        var panel = doc.Add(Wall(-5, -4));
        var door = MeshEntityOps.Tie(doc, [panel.Id], "crate")!;
        var crate = doc.Add(EntityClasses.Create("crate", new(0, 0, -8)));
        var sel = new Selection();
        sel.SelectAll(doc, PickFilter.Entities);
        Assert.Equal(new[] { panel.Id, door.Id, crate.Id }.Order(), sel.MeshIds().Order());
        sel.Invert(doc, PickFilter.Geometry);
        Assert.Equal([wall.Id], sel.MeshIds());
    }
}
