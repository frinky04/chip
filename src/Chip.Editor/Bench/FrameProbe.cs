using System.Diagnostics;
using System.Numerics;
using Chip.Editor.Framework;
using Chip.Editor.UI;

namespace Chip.Editor.Bench;

/// <summary>Benchmark-only viewport input and non-allocating stage sampling. Never drives native input.</summary>
internal sealed class FrameProbe
{
    public static readonly string[] Stages = ["script", "backend", "shell", "input", "sync", "render", "hud", "panels", "imgui", "tool", "selection", "entity", "selectionSets", "material", "smoothing", "texture", "hotspot", "collision", "export"];
    public readonly double[] Milliseconds = new double[Stages.Length];
    long _last;
    public Vector2? Mouse;
    public bool Pick;
    public bool SkipPicking;
    public void Start() { Array.Clear(Milliseconds); _last = Stopwatch.GetTimestamp(); }
    public void Mark(string stage)
    {
        long now = Stopwatch.GetTimestamp();
        Milliseconds[Array.IndexOf(Stages, stage)] += Stopwatch.GetElapsedTime(_last, now).TotalMilliseconds;
        _last = now;
    }
    public void RecordSection(string id, long start)
    {
        int i = Array.IndexOf(Stages, id);
        if (i >= 0) Milliseconds[i] += Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }
    public void UpdateViewport(EditorContext ctx) => ctx.Hover = Pick && !SkipPicking ? ctx.PickAtMouse(Theme.Scale) : null;
}
