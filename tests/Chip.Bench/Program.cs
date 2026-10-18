using System.Diagnostics;
using System.Reflection;

namespace Chip.Bench;

/// <summary>
/// One measured operation. <see cref="Setup"/> runs once (untimed); <see cref="Run"/> is timed repeatedly.
/// Add a class in <c>Benches/</c>; it is discovered by reflection.
/// </summary>
public interface IBench
{
    /// <summary>"area/what", e.g. "pick/hover-ray". Filters match on prefix.</summary>
    string Name { get; }
    void Setup();
    void Run();
}

/// <summary>
/// Release-mode micro benchmarks on the Phase 2 stress level (<see cref="Chip.Core.Diagnostics.StressLevel"/>).
/// Usage: <c>dotnet run -c Release --project tests/Chip.Bench [-- name-prefix ...]</c>. Prints the median, the best,
/// and managed bytes allocated per run.
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        var benches = Assembly.GetExecutingAssembly().GetTypes()
            .Where(t => typeof(IBench).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface)
            .Select(t => (IBench)Activator.CreateInstance(t)!)
            .Where(b => args.Length == 0 || args.Any(a => b.Name.StartsWith(a, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(b => b.Name, StringComparer.Ordinal)
            .ToList();
        if (benches.Count == 0) { Console.Error.WriteLine("No benchmark matches."); return 1; }

        var level = Benches.Stress.Doc;
        Console.WriteLine($"Stress level: {level.Meshes.Count} meshes, {Chip.Core.Diagnostics.StressLevel.FaceCount(level)} faces, {level.Entities.Count} entities");
        Console.WriteLine($"{"benchmark",-34} {"median",10} {"best",10} {"alloc/run",12}  runs");
        foreach (var b in benches)
        {
            b.Setup();
            for (int i = 0; i < 3; i++) b.Run(); // warm-up and JIT

            var times = new List<double>();
            long allocated = 0;
            var budget = Stopwatch.StartNew();
            while (times.Count < 5 || (times.Count < 200 && budget.Elapsed.TotalSeconds < 2))
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                long t0 = Stopwatch.GetTimestamp();
                b.Run();
                times.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
                allocated += GC.GetAllocatedBytesForCurrentThread() - before;
            }
            times.Sort();
            Console.WriteLine($"{b.Name,-34} {Ms(times[times.Count / 2]),10} {Ms(times[0]),10} {Bytes(allocated / times.Count),12}  {times.Count}");
        }
        return 0;
    }

    static string Ms(double ms) => ms < 1 ? $"{ms * 1000:0} us" : $"{ms:0.00} ms";

    static string Bytes(long b) => b switch
    {
        < 1024 => $"{b} B",
        < 1024 * 1024 => $"{b / 1024.0:0.0} KB",
        _ => $"{b / (1024.0 * 1024):0.0} MB",
    };
}
