using System.Numerics;
using Chip.Core.Diagnostics;
using Chip.Core.Scene;
using Chip.Core.Texturing;
using Chip.Editor.Inspector;
using Chip.Editor.Bench;
using Chip.Editor.Features.Scene;
using Chip.Editor.Rendering;

namespace Chip.Editor.Tests;

public class FrameCacheTests
{
    static Session MakeSession()
    {
        var session = new Session();
        session.Reset(StressLevel.Generate(2, entities: 1), null);
        return session;
    }

    [Fact]
    public void SelectionCacheInvalidatesOnEditUndoRedoCancelAndReset()
    {
        var session = MakeSession();
        var cache = new FrameCache<object>();
        void Store() => cache.Store(session, new object());
        void Edit() => session.Doc.Meshes[0].Positions[0] += Vector3.One;
        Store();
        Assert.True(cache.IsCurrent(session));
        session.Do("Move", Edit);
        Assert.False(cache.IsCurrent(session));
        Store();
        session.Undo();
        Assert.False(cache.IsCurrent(session));
        Store();
        session.Redo();
        Assert.False(cache.IsCurrent(session));
        Store();
        session.Begin("Cancel"); Edit(); session.Touch();
        Assert.False(cache.IsCurrent(session));
        Store(); session.Cancel();
        Assert.False(cache.IsCurrent(session));
        Store(); session.Reset(new Document(), null);
        Assert.False(cache.IsCurrent(session));
    }

    [Fact]
    public void SelectionCacheComparesExactItemsModeAndActiveElement()
    {
        var session = MakeSession();
        var cache = new FrameCache<int>();
        var first = ElementRef.Object(session.Doc.Meshes[0].Id);
        var second = ElementRef.Object(session.Doc.Meshes[1].Id);
        session.Sel.Add(first); cache.Store(session, 12);
        session.Sel.Items.Remove(first); session.Sel.Items.Add(second);
        Assert.False(cache.IsCurrent(session)); // count did not change
        cache.Store(session, 13);
        session.Sel.Active = second;
        Assert.False(cache.IsCurrent(session));
        cache.Store(session, 14);
        session.Sel.ConvertTo(SelectMode.Face, session.Doc);
        Assert.False(cache.IsCurrent(session));
        cache.Store(session, 15);
        session.Sel.Items.Clear();
        Assert.False(cache.IsCurrent(session));
    }

    [Fact]
    public void DocumentOnlyCacheDoesNotInvalidateForSelection()
    {
        var session = MakeSession();
        var cache = new FrameCache<int>(selection: false);
        cache.Store(session, 3);
        session.Sel.Add(ElementRef.Object(session.Doc.Meshes[0].Id));
        Assert.True(cache.IsCurrent(session));
        Assert.False(cache.IsCurrent(MakeSession()));
    }

    [Fact]
    public void EntityVisibilityInvalidatesForEyeBackfacesAndSceneButNotSelection()
    {
        var session = MakeSession();
        var cache = new EntityVisibilityFrameCache();
        var entity = session.Doc.Entities[0];
        int calls = 0;
        bool Compute() { calls++; return true; }
        bool Get(Vector3 eye, bool backfaces = false) => cache.Get(session, eye, backfaces, entity, compute: Compute);
        Assert.True(Get(Vector3.Zero)); Get(Vector3.Zero);
        Assert.Equal(1, calls);
        session.Sel.Add(ElementRef.Object(session.Doc.Meshes[0].Id)); Get(Vector3.Zero);
        Assert.Equal(1, calls);
        Get(Vector3.One); Get(Vector3.One, true);
        Assert.Equal(3, calls);
        session.Do("Move", () => session.Doc.Meshes[0].Positions[0] += Vector3.One);
        Get(Vector3.One, true); Assert.Equal(4, calls);
        session.Undo(); Get(Vector3.One, true); Assert.Equal(5, calls);
        session.Redo(); Get(Vector3.One, true); Assert.Equal(6, calls);
        session.Begin("Move"); session.Touch(); Get(Vector3.One, true);
        Assert.Equal(6, calls); // mid-gesture: last answers stand until the gesture ends
        Get(Vector3.Zero, true); Assert.Equal(7, calls); // ...but a new eye still recomputes
        session.Cancel(); Get(Vector3.One, true); Assert.Equal(8, calls);
    }

    [Fact]
    public void TextureSummaryKeepsActiveFaceOrderAndMixedFieldsAcrossUndo()
    {
        var session = MakeSession();
        session.Sel.ConvertTo(SelectMode.Face, session.Doc);
        int id = session.Doc.Meshes[0].Id;
        session.Sel.Add(ElementRef.Face(id, 0));
        session.Sel.Add(ElementRef.Face(id, 1));
        var ctx = new Chip.Editor.Framework.EditorContext(session, new MaterialLibrary());
        var cache = new FrameCache<TextureFrameStats>();
        TextureFrameStats Read()
        {
            if (!cache.IsCurrent(session)) cache.Store(session, TextureFrameStats.Build(Chip.Editor.Features.Texturing.TextureOps.Faces(ctx)));
            return cache.Value;
        }
        var initial = Read();
        Assert.Equal(1, initial.Faces[0].Index);
        session.Do("UV", () =>
        {
            var face = session.Doc.Meshes[0].Faces[1];
            face.Map = face.Map with { ShiftU = 1234, Rotation = 17 };
            face.UVs = new Vector2[face.Verts.Length];
        });
        var edited = Read();
        Assert.True(edited.MixedShiftU); Assert.True(edited.MixedRotation);
        Assert.Equal(1, edited.ExplicitCount); Assert.Equal(1234, edited.First.ShiftU);
        session.Undo();
        Assert.Equal(initial.First, Read().First); Assert.Equal(initial.ExplicitCount, Read().ExplicitCount);
        session.Redo(); Assert.Equal(edited.First, Read().First);
        session.Sel.Items.Remove(ElementRef.Face(id, 0));
        Assert.False(Read().MixedShiftU); Assert.Single(Read().Faces);
        session.Sel.Active = ElementRef.Face(id, 0);
        session.Sel.Items.Add(ElementRef.Face(id, 0));
        Assert.Equal(0, Read().Faces[0].Index);
    }

    [Fact]
    public void StatisticsUseMedianAndNearestRankP95WithoutChangingSamples()
    {
        double[] samples = [4, 1, 3, 2];
        Assert.Equal(new FrameDistribution(2.5, 4, 4), FrameStatistics.Summarize(samples));
        Assert.Equal(new double[] { 4, 1, 3, 2 }, samples);
        Assert.Throws<ArgumentException>(() => FrameStatistics.Summarize([]));
    }
}
