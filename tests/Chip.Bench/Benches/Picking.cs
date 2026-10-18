using System.Numerics;
using Chip.Core.Picking;
using Chip.Core.Scene;

namespace Chip.Bench.Benches;

abstract class PickingBench(bool accelerated, string operation, SelectMode mode = SelectMode.Face) : IBench
{
    readonly Session _session = new();
    PickCache _cache = null!;
    ViewInfo _view;
    readonly Vector2 _mouse = new(800, 450);
    float _move = 0.25f;

    public string Name => $"pick/{(accelerated ? "cache" : "brute")}-{operation}-{mode.ToString().ToLowerInvariant()}";

    public void Setup()
    {
        _session.Reset(operation == "after-edit" ? Stress.Doc.Clone() : Stress.Doc, null);
        _cache = new PickCache(_session);
        _view = Stress.View;
        _cache.Pick(_view, _mouse, mode);
        if (operation == "after-edit") _session.Begin("Move");
    }

    public void Run()
    {
        if (operation == "after-edit")
        {
            var mesh = _session.Doc.Meshes[0];
            for (int i = 0; i < mesh.Positions.Count; i++) mesh.Positions[i] += Vector3.UnitX * _move;
            _move = -_move;
            _session.Touch(); // The timed work is one drag frame, including the revision sync.
        }
        switch (operation)
        {
            case "raycast":
                if (accelerated) _cache.RaycastFace(_view.ScreenRay(_mouse));
                else Picker.RaycastFace(_session.Doc, _view.ScreenRay(_mouse));
                break;
            case "marquee":
                if (accelerated) _cache.Marquee(_view, new(400, 200), new(1200, 700), mode);
                else Picker.Marquee(_session.Doc, _view, new(400, 200), new(1200, 700), mode);
                break;
            case "entity":
                if (accelerated) _cache.PickEntity(_view, _mouse);
                else Picker.PickEntity(_session.Doc, _view, _mouse);
                break;
            default:
                if (accelerated) _cache.Pick(_view, _mouse, mode);
                else Picker.Pick(_session.Doc, _view, _mouse, mode);
                break;
        }
    }
}

sealed class CachedHoverFace() : PickingBench(true, "hover", SelectMode.Face);
sealed class CachedHoverEdge() : PickingBench(true, "hover", SelectMode.Edge);
sealed class CachedHoverVertex() : PickingBench(true, "hover", SelectMode.Vertex);
sealed class CachedHoverObject() : PickingBench(true, "hover", SelectMode.Object);
sealed class BruteHoverVertex() : PickingBench(false, "hover", SelectMode.Vertex);
sealed class BruteHoverObject() : PickingBench(false, "hover", SelectMode.Object);
sealed class CachedEntity() : PickingBench(true, "entity", SelectMode.Object);
sealed class BruteEntity() : PickingBench(false, "entity", SelectMode.Object);
sealed class CachedRaycast() : PickingBench(true, "raycast");
sealed class CachedMarqueeFace() : PickingBench(true, "marquee", SelectMode.Face);
sealed class CachedMarqueeEdge() : PickingBench(true, "marquee", SelectMode.Edge);
sealed class CachedMarqueeVertex() : PickingBench(true, "marquee", SelectMode.Vertex);
sealed class CachedMarqueeObject() : PickingBench(true, "marquee", SelectMode.Object);
sealed class BruteMarqueeEdge() : PickingBench(false, "marquee", SelectMode.Edge);
sealed class BruteMarqueeVertex() : PickingBench(false, "marquee", SelectMode.Vertex);
sealed class BruteMarqueeObject() : PickingBench(false, "marquee", SelectMode.Object);
sealed class CachedEdit() : PickingBench(true, "after-edit");
sealed class BruteEdit() : PickingBench(false, "after-edit");
