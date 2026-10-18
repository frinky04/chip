using System.Numerics;
using Chip.Core.Diagnostics;
using Chip.Core.Geometry;
using Chip.Core.Picking;
using Chip.Core.Scene;
using Chip.Core.Texturing;

namespace Chip.Core.Tests;

public class PickingCacheTests
{
    static readonly SelectMode[] Modes = Enum.GetValues<SelectMode>();

    static Session Session(Document doc)
    {
        var session = new Session();
        session.Reset(doc, null);
        return session;
    }

    static ViewInfo View(Vector3 eye, Vector3 target, bool ortho = false)
    {
        var direction = Vector3.Normalize(target - eye);
        var up = MathF.Abs(Vector3.Dot(direction, Vector3.UnitY)) > 0.99f ? Vector3.UnitZ : Vector3.UnitY;
        var projection = ortho ? Matrix4x4.CreateOrthographic(320, 180, 0.01f, 2000) :
            Matrix4x4.CreatePerspectiveFieldOfView(75 * MathF.PI / 180, 16 / 9f, 0.02f, 2000);
        return new ViewInfo(Matrix4x4.CreateLookAt(eye, target, up) * projection, new(1600, 900), eye);
    }

    static PolyMesh Box(Vector3 min, Vector3 max) => MeshOps.CreateBox(min, max, "m", TextureMapping.Default);

    static Document Fixture()
    {
        var doc = new Document();
        doc.Add(Box(new(-2, -1, -2), new(2, 1, 2)));
        doc.Add(Box(new(-2, -1, -2), new(2, 1, 2))); // Exact ties between meshes.
        var concave = new PolyMesh
        {
            Positions = [new(-3, 2, -3), new(3, 2, -3), new(3, 2.1f, 3), new(0, 2, 0), new(-3, 2, 3), new(0, 5, 0)],
            Faces = [new Face([0, 1, 2, 3, 4], "m", TextureMapping.Default), new Face([0, 0, 1], "m", TextureMapping.Default)],
        };
        doc.Add(concave); // Concave, non-planar, degenerate, plus an isolated vertex.
        doc.Add(new PolyMesh());
        doc.Add(EntityClasses.Create("info_player_start", new(0, 3, 0), 45));
        doc.Add(EntityClasses.Create("custom", new(-2, 3, -2)));
        doc.Entities[1].Rotation = new(17, 83, 29);
        return doc;
    }

    // Deterministic property tests: the unchanged static picker is the oracle, including list order and exact hit data.
    [Theory]
    [InlineData(17, true)]
    [InlineData(91, false)]
    public void RandomQueriesMatchBruteForce(int seed, bool stress)
    {
        var rng = new Random(seed);
        var doc = stress ? StressLevel.Generate(seed: seed) : Fixture();
        for (int i = 0; i < doc.Meshes.Count; i++)
        {
            doc.Meshes[i].Hidden = i % 17 == 8;
            doc.Meshes[i].Locked = i % 19 == 9;
        }
        for (int i = 0; i < doc.Entities.Count; i++)
        {
            doc.Entities[i].Hidden = i % 11 == 5;
            doc.Entities[i].Locked = i % 13 == 6;
        }
        var cache = new PickCache(Session(doc));
        var views = stress ? new[]
        {
            View(new(-10, 25, -10), new(120, 0, 120)),
            View(new(130, 200, 130), new(130, 0, 130), true),
            View(new(100, 2, 100), new(130, 0, 130)), // Bounds crossing the eye plane.
        } : new[] { View(new(7, 6, 8), Vector3.Zero), View(new(0, 200, 0), Vector3.Zero, true), View(Vector3.Zero, Vector3.UnitZ) };
        foreach (var view in views)
        {
            for (int i = 0; i < (stress ? 48 : 96); i++)
            {
                var mouse = new Vector2(rng.NextSingle() * 2000 - 200, rng.NextSingle() * 1200 - 150);
                // Bias half the samples toward actual vertices and silhouettes; the others sample empty screen space.
                if (i % 2 == 0)
                {
                    var mesh = doc.Meshes[rng.Next(doc.Meshes.Count)];
                    if (mesh.Positions.Count > 0 && view.Project(mesh.Positions[rng.Next(mesh.Positions.Count)], out var p))
                        mouse = p + new Vector2(rng.NextSingle() * 20 - 10, rng.NextSingle() * 20 - 10);
                }
                float scale = new[] { 0.5f, 1, 1.5f, 2 }[i % 4];
                bool backfaces = i % 3 == 0;
                foreach (var mode in Modes)
                    Assert.Equal(Picker.Pick(doc, view, mouse, mode, scale, backfaces), cache.Pick(view, mouse, mode, scale, backfaces));
                var ray = view.ScreenRay(mouse);
                Assert.Equal(Picker.RaycastFace(doc, ray, backfaces, i % 2 == 0), cache.RaycastFace(ray, backfaces, i % 2 == 0));
                // Independent rays, including origins inside bounds and axis-parallel directions.
                var origin = stress ? new Vector3(rng.NextSingle() * 280, rng.NextSingle() * 20, rng.NextSingle() * 280) :
                    new Vector3(rng.NextSingle() * 10 - 5, rng.NextSingle() * 10 - 5, rng.NextSingle() * 10 - 5);
                var direction = i % 4 == 0 ? -Vector3.UnitY : Vector3.Normalize(new Vector3(rng.NextSingle() - 0.5f, -rng.NextSingle(), rng.NextSingle() - 0.5f));
                ray = new Ray(origin, direction);
                Assert.Equal(Picker.RaycastFace(doc, ray, backfaces), cache.RaycastFace(ray, backfaces));
                float? surface = i % 2 == 0 ? rng.NextSingle() * 200 : null;
                Assert.Same(Picker.PickEntity(doc, view, mouse, scale, surface), cache.PickEntity(view, mouse, scale, surface));
                if (i % 8 == 0)
                {
                    var other = new Vector2(rng.NextSingle() * 1600, rng.NextSingle() * 900);
                    foreach (var mode in Modes)
                        Assert.Equal(Picker.Marquee(doc, view, mouse, other, mode, backfaces), cache.Marquee(view, mouse, other, mode, backfaces));
                }
            }
        }
    }

    [Fact]
    public void EditsUndoRedoCancelAndCloneSwapsInvalidateOnlyChangedMeshes()
    {
        var session = Session(Fixture());
        var cache = new PickCache(session);
        var ray = new Ray(new(0, 20, 0), -Vector3.UnitY);
        void Check(int builds)
        {
            Assert.Equal(Picker.RaycastFace(session.Doc, ray, true), cache.RaycastFace(ray, true));
            Assert.Equal(builds, cache.MeshBuildCount);
        }
        int count = session.Doc.Meshes.Count;
        Check(count);
        void Move()
        {
            var mesh = session.Doc.Meshes[0];
            for (int i = 0; i < mesh.Positions.Count; i++) mesh.Positions[i] += new Vector3(0, 10, 0);
        }
        session.Do("Move", Move);
        Check(++count);
        Assert.True(session.Undo());
        Check(++count);
        Assert.True(session.Redo());
        Check(++count);
        session.Begin("Drag");
        Move();
        session.Touch();
        Check(++count);
        session.Cancel();
        Check(++count);
        // A clone swap with identical contents still returns the current document's mesh object.
        session.Begin("No change");
        session.Cancel();
        Check(count);
        session.Do("Topology", () => session.Doc.Meshes[0].Faces.RemoveAt(0));
        Check(++count);
    }

    [Fact]
    public void AddDeleteReorderAndResetRefreshMembership()
    {
        var session = Session(Fixture());
        var cache = new PickCache(session);
        var view = View(new(7, 6, 8), Vector3.Zero);
        void Check()
        {
            foreach (var mode in Modes)
            {
                Assert.Equal(Picker.Pick(session.Doc, view, new(800, 450), mode, backfaces: true), cache.Pick(view, new(800, 450), mode, backfaces: true));
                Assert.Equal(Picker.Marquee(session.Doc, view, new(-1000), new(3000), mode, true), cache.Marquee(view, new(-1000), new(3000), mode, true));
            }
        }
        Check();
        session.Do("Delete", () => session.Doc.Meshes.RemoveAt(0));
        Check();
        session.Do("Add", () => session.Doc.Add(Box(new(-3), new(3))));
        Check();
        session.Do("Reorder", () => session.Doc.Meshes.Reverse());
        Check();
        session.Undo();
        Check();
        session.Reset(new Document(), null);
        Check();
        session.Reset(Fixture(), null);
        Check();
    }

    [Fact]
    public void UnhashedFlagsAndLiveEntityStateAreReadAtQueryTime()
    {
        var session = Session(Fixture());
        var cache = new PickCache(session);
        var view = View(new(7, 6, 8), Vector3.Zero);
        void Check()
        {
            foreach (var mode in Modes)
            {
                Assert.Equal(Picker.Pick(session.Doc, view, new(800, 450), mode), cache.Pick(view, new(800, 450), mode));
                Assert.Equal(Picker.Marquee(session.Doc, view, Vector2.Zero, view.Size, mode), cache.Marquee(view, Vector2.Zero, view.Size, mode));
            }
            foreach (bool selectable in new[] { false, true })
                Assert.Equal(Picker.RaycastFace(session.Doc, view.ScreenRay(new(800, 450)), true, selectable), cache.RaycastFace(view.ScreenRay(new(800, 450)), true, selectable));
            Assert.Same(Picker.PickEntity(session.Doc, view, new(800, 450)), cache.PickEntity(view, new(800, 450)));
        }
        Check();
        int count = cache.MeshBuildCount;
        foreach (var mesh in session.Doc.Meshes) mesh.Locked = true;
        Check();
        foreach (var mesh in session.Doc.Meshes) { mesh.Locked = false; mesh.Hidden = true; }
        Check();
        foreach (var mesh in session.Doc.Meshes) mesh.Hidden = false;
        foreach (var e in session.Doc.Entities) { e.Position = new(4, 4, 4); e.Rotation = new(30, 60, 90); e.ClassName = "custom"; }
        Check();
        foreach (var e in session.Doc.Entities) e.Locked = true;
        Check();
        foreach (var e in session.Doc.Entities) { e.Locked = false; e.Hidden = true; }
        Check();
        session.Touch();
        Check();
        Assert.Equal(count, cache.MeshBuildCount);
    }

    [Fact]
    public void EntityBoxesGlyphsOcclusionAndTiesMatch()
    {
        var doc = new Document();
        var entity = doc.Add(EntityClasses.Create("info_player_start", Vector3.Zero, 45));
        entity.Rotation = new(17, 45, 29);
        doc.Add(entity.Clone()); // Exact entity ties retain document order.
        doc.Add(EntityClasses.Create("custom", new(2, 0, 0)));
        var session = Session(doc);
        var cache = new PickCache(session);
        foreach (bool ortho in new[] { false, true })
        {
            var view = View(new(4, 4, 6), Vector3.Zero, ortho);
            foreach (var e in doc.Entities)
            {
                Assert.True(view.Project(EntityOps.Center(e), out var center));
                foreach (float scale in new[] { 0.5f, 1, 2 })
                    foreach (float offset in new[] { 0, 5, 10, 11, 12, 22 })
                        foreach (float? surface in new float?[] { null, 0, 4, 20 })
                        {
                            var mouse = center + Vector2.UnitX * offset;
                            Assert.Same(Picker.PickEntity(doc, view, mouse, scale, surface), cache.PickEntity(view, mouse, scale, surface));
                            Assert.Equal(Picker.Pick(doc, view, mouse, SelectMode.Object, scale), cache.Pick(view, mouse, SelectMode.Object, scale));
                        }
            }
            view.Project(EntityOps.Center(entity), out var projected);
            Assert.Same(entity, cache.PickEntity(view, projected));
        }
    }

    [Fact]
    public void ExactSilhouettesPixelRadiiAndMarqueeBoundariesMatch()
    {
        var doc = Fixture();
        var cache = new PickCache(Session(doc));
        var view = View(new(7, 6, 8), Vector3.Zero);
        foreach (var mesh in doc.Meshes)
        {
            var samples = mesh.Positions.Concat(Enumerable.Range(0, mesh.Faces.Count).Select(mesh.FaceCenter));
            foreach (var p in samples)
            {
                if (!view.Project(p, out var screen)) continue;
                foreach (var mode in Modes)
                {
                    foreach (float offset in new[] { 0, 7, 10, 14, 20 })
                    {
                        var mouse = screen + new Vector2(offset, 0);
                        Assert.Equal(Picker.Pick(doc, view, mouse, mode, 2, true), cache.Pick(view, mouse, mode, 2, true));
                    }
                    Assert.Equal(Picker.Marquee(doc, view, screen, screen, mode, true), cache.Marquee(view, screen, screen, mode, true));
                }
            }
        }
    }

    [Fact]
    public void VisibilityUsesEquivalentAcceleratedDepthTests()
    {
        var doc = Fixture();
        var cache = new PickCache(Session(doc));
        var view = View(new(7, 6, 8), Vector3.Zero);
        foreach (bool backfaces in new[] { false, true })
            foreach (var mode in Modes)
            {
                var picked = Picker.Marquee(doc, view, Vector2.Zero, view.Size, mode, backfaces);
                Assert.Equal(Visibility.FilterVisible(doc, view, picked, mode, backfaces),
                    Visibility.FilterVisible(doc, view, picked, mode, backfaces, cache));
            }
    }

    [Fact]
    public void WarmQueriesAndReusableMarqueesAllocateNothing()
    {
        var session = Session(Fixture());
        var cache = new PickCache(session);
        var view = View(new(7, 6, 8), Vector3.Zero);
        var results = new List<ElementRef>(1000);
        void Query()
        {
            foreach (var mode in Modes)
            {
                cache.Pick(view, new(800, 450), mode);
                cache.Marquee(view, Vector2.Zero, view.Size, mode, results);
            }
            cache.RaycastFace(view.ScreenRay(new(800, 450)));
            cache.PickEntity(view, new(800, 450));
        }
        for (int i = 0; i < 20; i++) Query();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 20; i++) Query();
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
