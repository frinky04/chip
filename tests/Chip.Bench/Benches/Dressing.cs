using System.Numerics;
using Chip.Core.Diagnostics;
using Chip.Core.Dressing;
using Chip.Core.Geometry;
using Chip.Core.Scene;

namespace Chip.Bench.Benches;

/// <summary>
/// Dressing the stress level: every upright face tagged "wall", a baseboard along every wall standing on a floor and a
/// bevel on wall corners. Revisions come from a counter per mesh, like the editor's session (no rehashing).
/// </summary>
static class DressedStress
{
    public const string Rules = """
        { "chipDressing": 1, "rules": {
            "baseboard": { "kind": "sweep", "faces": { "tags": ["wall"] } },
            "corners": { "kind": "bevel", "faces": { "tags": ["wall"] }, "width": 0.02 } } }
        """;

    static Document? _doc;
    public static readonly Dictionary<int, int> Revisions = new();
    public static readonly MemoryFiles Files = new() { ["bench.dressing.json"] = Rules };

    public static Document Doc
    {
        get
        {
            if (_doc != null) return _doc;
            _doc = StressLevel.Generate();
            foreach (var m in _doc.Meshes)
            {
                Revisions[m.Id] = 1;
                for (int f = 0; f < m.Faces.Count; f++)
                    if (MathF.Abs(m.FaceNormal(f).Y) < 0.3f) m.Faces[f].Tags = TagSet.Of("wall");
            }
            return _doc;
        }
    }

    public static int Revision(PolyMesh m) => Revisions.TryGetValue(m.Id, out var r) ? r : 0;
}

/// <summary>Opening a level: everything dressed from scratch.</summary>
sealed class DressColdBench : IBench
{
    public string Name => "dress/full-cold";

    public void Setup()
    {
        var level = new Dresser(DressedStress.Files).Dress(DressedStress.Doc, "bench.dressing.json", DressedStress.Revision);
        Console.WriteLine($"  {Name}: {string.Join(", ", level.Stats.Select(s => $"{s.Rule} {s.Targets} {s.TargetNoun} -> {s.Outputs}"))}; {level.WarningCount} warnings");
    }

    public void Run() => new Dresser(DressedStress.Files).Dress(DressedStress.Doc, "bench.dressing.json", DressedStress.Revision);
}

/// <summary>A frame with nothing changed: the per-frame overhead while the level is open.</summary>
sealed class DressIdleBench : IBench
{
    readonly Dresser _dresser = new(DressedStress.Files);
    public string Name => "dress/idle-frame";
    public void Setup() => _dresser.Dress(DressedStress.Doc, "bench.dressing.json", DressedStress.Revision);
    public void Run() => _dresser.Dress(DressedStress.Doc, "bench.dressing.json", DressedStress.Revision, checkFiles: false);
}

/// <summary>A gizmo-drag frame: one mesh moved, everything else reused.</summary>
sealed class DressDragBench : IBench
{
    readonly Dresser _dresser = new(DressedStress.Files);
    PolyMesh _mesh = null!;
    float _step = 0.25f;
    public string Name => "dress/drag-one-mesh";

    public void Setup()
    {
        var doc = DressedStress.Doc;
        _mesh = doc.Meshes.Where(m => m.Faces.Any(f => f.Tags.Contains("wall"))).Skip(doc.Meshes.Count / 3).First();
        _dresser.Dress(doc, "bench.dressing.json", DressedStress.Revision);
    }

    public void Run()
    {
        for (int i = 0; i < _mesh.Positions.Count; i++) _mesh.Positions[i] += new Vector3(_step, 0, 0);
        _step = -_step;
        DressedStress.Revisions[_mesh.Id]++;
        _dresser.Dress(DressedStress.Doc, "bench.dressing.json", DressedStress.Revision, checkFiles: false);
    }
}
