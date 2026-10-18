using System.Numerics;
using Chip.Core.Scene;

namespace Chip.Bench.Benches;

sealed class ScopedUndoBench : IBench
{
    readonly Session _session = new();
    public string Name => "undo/scoped-move-one-mesh";
    public void Setup() => _session.Reset(Stress.Doc.Clone(), null);
    public void Run() => UndoMeasurement.Move(_session, scoped: true);
}

sealed class ScopedRetextureBench : IBench
{
    readonly Session _session = new();
    public string Name => "undo/scoped-retexture-one-mesh";
    public void Setup() => _session.Reset(Stress.Doc.Clone(), null);
    public void Run() => _session.Do("Texture", [_session.Doc.Meshes[0].Id], () =>
    {
        foreach (var face in _session.Doc.Meshes[0].Faces) face.Map.ShiftU += 1;
    });
}

sealed class ScopedMoveFewBench : IBench
{
    readonly Session _session = new();
    public string Name => "undo/scoped-move-three-meshes";
    public void Setup() => _session.Reset(Stress.Doc.Clone(), null);
    public void Run() => _session.Do("Move", _session.Doc.Meshes.Take(3).Select(m => m.Id), () =>
    {
        foreach (var mesh in _session.Doc.Meshes.Take(3))
            for (int i = 0; i < mesh.Positions.Count; i++) mesh.Positions[i] += Vector3.UnitX * 0.25f;
        _session.Touch();
    });
}

sealed class ReferenceUndoBench : IBench
{
    readonly Session _session = new() { FullSnapshotUndo = true };
    public string Name => "undo/reference-move-one-mesh";
    public void Setup() => _session.Reset(Stress.Doc.Clone(), null);
    public void Run() => UndoMeasurement.Move(_session, scoped: false);
}

sealed class ScopedUndoRedoBench : IBench
{
    readonly Session _session = new();
    public string Name => "undo/scoped-undo-redo";
    public void Setup() { _session.Reset(Stress.Doc.Clone(), null); UndoMeasurement.Move(_session, scoped: true); }
    public void Run() { _session.Undo(); _session.Redo(); }
}

sealed class ReferenceUndoRedoBench : IBench
{
    readonly Session _session = new() { FullSnapshotUndo = true };
    public string Name => "undo/reference-undo-redo";
    public void Setup() { _session.Reset(Stress.Doc.Clone(), null); UndoMeasurement.Move(_session, scoped: false); }
    public void Run() { _session.Undo(); _session.Redo(); }
}

/// <summary>Force full collections around 256 steps; exclude the live level and shared fixture from retained history.</summary>
sealed class RetainedUndoBench : IBench
{
    public string Name => "undo/retained-256";
    public void Setup()
    {
        Measure(full: false, scoped: true);
        Measure(full: false, scoped: false);
        Measure(full: true, scoped: false);
    }
    public void Run() { } // The harness times Run; retained size is a one-time measurement in Setup.

    static void Measure(bool full, bool scoped)
    {
        var session = new Session { FullSnapshotUndo = full };
        session.Reset(Stress.Doc.Clone(), null);
        long before = GC.GetTotalMemory(forceFullCollection: true);
        for (int i = 0; i < 256; i++) UndoMeasurement.Move(session, scoped);
        long held = GC.GetTotalMemory(forceFullCollection: true) - before;
        Console.WriteLine($"  retained/256 {(full ? "reference" : scoped ? "scoped" : "fallback"),-9}: {held:N0} B ({held / 256:N0} B/step)");
        GC.KeepAlive(session);
    }
}

static class UndoMeasurement
{
    public static void Move(Session session, bool scoped)
    {
        if (scoped) session.Begin("Move", [session.Doc.Meshes[0].Id]);
        else session.Begin("Move");
        var mesh = session.Doc.Meshes[0];
        for (int i = 0; i < mesh.Positions.Count; i++) mesh.Positions[i] += Vector3.UnitX * 0.25f;
        session.Touch();
        session.End();
    }
}
