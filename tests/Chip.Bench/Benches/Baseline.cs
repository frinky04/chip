using System.Numerics;
using Chip.Core.Diagnostics;
using Chip.Core.Geometry;
using Chip.Core.Picking;
using Chip.Core.Scene;

namespace Chip.Bench.Benches;

/// <summary>Shared fixture: the stress level and a camera looking across it, built once per process.</summary>
static class Stress
{
    static Document? _doc;
    public static Document Doc => _doc ??= StressLevel.Generate();

    /// <summary>A 1600×900 perspective view from a corner of the level, looking diagonally across it.</summary>
    public static ViewInfo View
    {
        get
        {
            var eye = new Vector3(-10, 25, -10);
            var target = new Vector3(120, 0, 120);
            var view = Matrix4x4.CreateLookAt(eye, target, Vector3.UnitY);
            var proj = Matrix4x4.CreatePerspectiveFieldOfView(75f * MathF.PI / 180f, 16 / 9f, 0.02f, 2000f);
            return new ViewInfo(view * proj, new Vector2(1600, 900), eye);
        }
    }
}

/// <summary>Before numbers for the performance pass. Streams add their own benches next to these.</summary>
sealed class GenerateBench : IBench
{
    public string Name => "level/generate";
    public void Setup() { }
    public void Run() => StressLevel.Generate();
}

sealed class HashAllBench : IBench
{
    public string Name => "level/hash-all-meshes";
    public void Setup() => _ = Stress.Doc;
    public void Run() { foreach (var m in Stress.Doc.Meshes) MeshHash.Of(m); }
}

sealed class RaycastBench : IBench
{
    public string Name => "pick/raycast-face";
    public void Setup() => _ = Stress.Doc;
    public void Run() => Picker.RaycastFace(Stress.Doc, Stress.View.ScreenRay(new Vector2(800, 450)));
}

sealed class HoverPickBench : IBench
{
    public string Name => "pick/hover-face";
    public void Setup() => _ = Stress.Doc;
    public void Run() => Picker.Pick(Stress.Doc, Stress.View, new Vector2(800, 450), SelectMode.Face);
}

sealed class HoverPickEdgeBench : IBench
{
    public string Name => "pick/hover-edge";
    public void Setup() => _ = Stress.Doc;
    public void Run() => Picker.Pick(Stress.Doc, Stress.View, new Vector2(800, 450), SelectMode.Edge);
}

sealed class MarqueeBench : IBench
{
    public string Name => "pick/marquee-faces";
    public void Setup() => _ = Stress.Doc;
    public void Run() => Picker.Marquee(Stress.Doc, Stress.View, new Vector2(400, 200), new Vector2(1200, 700), SelectMode.Face);
}

sealed class UndoStepBench : IBench
{
    readonly Session _s = new();
    public string Name => "undo/move-one-mesh";
    public void Setup() => _s.Reset(Stress.Doc.Clone(), null);
    public void Run()
    {
        // One small gesture on a big document: the common case snapshot undo pays for in full.
        _s.Begin("Move");
        var m = _s.Doc.Meshes[0];
        for (int i = 0; i < m.Positions.Count; i++) m.Positions[i] += Vector3.UnitX * 0.25f;
        _s.Touch();
        _s.End();
    }
}

sealed class UndoRedoBench : IBench
{
    readonly Session _s = new();
    public string Name => "undo/undo-redo";
    public void Setup()
    {
        _s.Reset(Stress.Doc.Clone(), null);
        _s.Do("Move", () => _s.Doc.Meshes[0].Positions[0] += Vector3.UnitY);
    }
    public void Run() { _s.Undo(); _s.Redo(); }
}

sealed class RevisionSyncBench : IBench
{
    readonly Session _s = new();
    public string Name => "level/revision-sync";
    public void Setup() => _s.Reset(Stress.Doc.Clone(), null);
    public void Run()
    {
        _s.Touch(); // what every drag frame does
        _s.MeshRevision(_s.Doc.Meshes[0]);
    }
}
