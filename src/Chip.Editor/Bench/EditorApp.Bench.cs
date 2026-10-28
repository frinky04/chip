using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using Chip.Core.Diagnostics;
using Chip.Core.IO;
using Chip.Core.Scene;
using Chip.Editor.Bench;
using Chip.Editor.Features.Scene;
using Chip.Editor.UI;
using Chip.Editor.Viewport;
using ImGuiNET;
using Silk.NET.OpenGL;

namespace Chip.Editor;

public sealed partial class EditorApp
{
    FrameProbe? _bench;

    /// <summary>Hidden, deterministic full-editor benchmark. Script and GL submission are timed; GPU waits are separate.</summary>
    public int RunBenchmark(string source, string? output)
    {
        int frames = ReadCount("CHIP_BENCH_FRAMES", 60), warmup = ReadCount("CHIP_BENCH_WARMUP", 15);
        EntityOverlay.BenchmarkSkipOcclusion = Environment.GetEnvironmentVariable("CHIP_BENCH_SKIP_OCCLUSION") == "1";
        var doc = source == "stress" ? StressLevel.Generate() : DocumentSerializer.Load(source);
        Ctx.Session.Reset(doc, null);
        Ctx.FrameAll();
        GroupSelection.Of(Ctx).Enabled = false; // preserve the exact scripted selection
        var camera = Ctx.Camera.Capture();
        var probe = _bench = new FrameProbe();
        probe.SkipPicking = Environment.GetEnvironmentVariable("CHIP_BENCH_SKIP_PICKING") == "1";
        // Warm the GL driver and JIT before the first scenario, without paying hundreds of baseline occlusion sweeps.
        bool skipOcclusion = EntityOverlay.BenchmarkSkipOcclusion;
        EntityOverlay.BenchmarkSkipOcclusion = true;
        for (int i = 0; i < 240; i++) { probe.Start(); Frame(1 / 60f); _gl.Finish(); }
        EntityOverlay.BenchmarkSkipOcclusion = skipOcclusion;
        uint query = _gl.GenQuery();
        var results = new List<object>();
        string? filter = Environment.GetEnvironmentVariable("CHIP_BENCH_FILTER");
        string[] scenarios = ["idle", "orbit", "hover-object", "hover-face", "hover-edge", "hover-vertex", "drag-1", "drag-200", "soft-edges", "outliner", "inspector-500", "four-pane", "empty"];
        foreach (string name in scenarios)
        {
            if (filter != null && !name.StartsWith(filter, StringComparison.Ordinal)) continue;
            Ctx.Sel.Clear();
            Ctx.SetMode(SelectMode.Object);
            Ctx.Commands.Execute("view.layoutSingle");
            ViewportLayout.Of(Ctx).Activate(ViewportLayout.Of(Ctx).Panes[0]);
            Ctx.Camera.Apply(camera);
            Ctx.DockOpen = false; // the dock's rail only, except in the outliner scenario
            probe.Mouse = null;
            probe.Pick = name.StartsWith("hover-");
            if (name == "empty") Ctx.Session.Reset(new Document(), null);
            if (name is "hover-face" or "inspector-500") Ctx.SetMode(SelectMode.Face);
            if (name is "hover-edge" or "soft-edges") Ctx.SetMode(SelectMode.Edge);
            if (name == "hover-vertex") Ctx.SetMode(SelectMode.Vertex);
            if (name == "outliner") Ctx.ShowDock(Outliner.PanelId); // tree defaults to fully expanded
            if (name == "four-pane") Ctx.Commands.Execute("view.layoutFour");
            if (name == "inspector-500")
                foreach (var mesh in doc.Meshes)
                {
                    for (int f = 0; f < mesh.Faces.Count && Ctx.Sel.Count < 500; f++) Ctx.Sel.Add(ElementRef.Face(mesh.Id, f));
                    if (Ctx.Sel.Count == 500) break;
                }
            TransformGesture? gesture = null;
            if (name.StartsWith("drag-"))
            {
                foreach (var mesh in doc.Meshes.Take(name == "drag-1" ? 1 : 200)) Ctx.Sel.Add(ElementRef.Object(mesh.Id));
                gesture = TransformGesture.Start(Ctx.Session, "Benchmark move");
            }
            var cpu = new double[frames];
            var gpu = new double[frames];
            var allocations = new long[frames];
            var stages = FrameProbe.Stages.Select(_ => new double[frames]).ToArray();
            int rebuilds = 0;
            for (int frame = -warmup; frame < frames; frame++)
            {
                // Backend input is ignored by benchmark viewport routing. No native input or event loop is driven.
                ImGui.GetIO().MousePos = new Vector2(-10000);
                _gl.Finish(); // keep queue backpressure out of the next CPU sample
                long allocated = GC.GetTotalAllocatedBytes(true);
                long start = Stopwatch.GetTimestamp();
                probe.Start();
                if (name == "orbit") Ctx.Camera.Orbit(Ctx.Camera.FocusTarget ?? Vector3.Zero, 0.002f, 0);
                if (probe.Pick) probe.Mouse = new Vector2(0.1f + 0.8f * ((frame + warmup) % 60) / 59f, 0.5f);
                gesture?.Apply(Matrix4x4.CreateTranslation((frame + warmup + 1) * 0.005f, 0, 0), rigid: true);
                probe.Mark("script");
                _gl.BeginQuery(QueryTarget.TimeElapsed, query);
                Frame(1 / 60f);
                _gl.EndQuery(QueryTarget.TimeElapsed);
                double elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                long bytes = GC.GetTotalAllocatedBytes(true) - allocated;
                _gl.GetQueryObject(query, QueryObjectParameterName.Result, out ulong nanoseconds);
                if (frame < 0) continue;
                cpu[frame] = elapsed; gpu[frame] = nanoseconds / 1_000_000d; allocations[frame] = bytes;
                for (int s = 0; s < stages.Length; s++) stages[s][frame] = probe.Milliseconds[s];
                rebuilds += _renderer.LastRebuildCount;
            }
            gesture?.Cancel();
            // A window too narrow for the dock shows its rail instead, which would time an empty scenario.
            if (name == "outliner" && _dockDrawn != Outliner.PanelId)
                throw new InvalidOperationException("The outliner scenario didn't draw the Scene tab: the window is too narrow for the dock at this UI scale");
            var stageMedians = FrameProbe.Stages.Select((stage, i) => (stage, value: FrameStatistics.Summarize(stages[i]).MedianMs)).ToDictionary(p => p.stage, p => p.value);
            var times = FrameStatistics.Summarize(cpu);
            Console.WriteLine($"{name,-16} CPU {times.MedianMs:F3} / {times.P95Ms:F3} / {times.MaxMs:F3} ms; {allocations.Average():F0} B/frame; GPU {FrameStatistics.Summarize(gpu).MedianMs:F3} ms");
            results.Add(new { Scenario = name, Cpu = times, Gpu = FrameStatistics.Summarize(gpu), BytesPerFrame = allocations.Average(), StagesMs = stageMedians, MeshRebuildsPerFrame = (double)rebuilds / frames });
        }
        _gl.DeleteQuery(query);
        _bench = null;
        var report = new { Source = source, Meshes = doc.Meshes.Count, Faces = StressLevel.FaceCount(doc), Entities = doc.Entities.Count, Groups = doc.Groups.Count, Frames = frames, WarmupFrames = warmup, Backbuffer = new[] { _window.FramebufferSize.X, _window.FramebufferSize.Y }, UiScale = Theme.Scale, Runtime = Environment.Version.ToString(), Cpu = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER"), Graphics = _gl.GetStringS(StringName.Renderer), DriverWarmupFrames = 240, SkipPicking = probe.SkipPicking, SkipEntityOcclusion = EntityOverlay.BenchmarkSkipOcclusion, TimestampUtc = DateTime.UtcNow, Scenarios = results };
        string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        if (output != null) { Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!); File.WriteAllText(output, json); }
        else Console.WriteLine(json);
        return 0;
    }

    static int ReadCount(string name, int fallback) => int.TryParse(Environment.GetEnvironmentVariable(name), out int n) && n > 0 ? n : fallback;
}
