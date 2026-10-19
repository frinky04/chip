using System.Diagnostics;
using System.Numerics;
using Chip.Core.Geometry;
using Chip.Core.Scene;

namespace Chip.Core.Dressing;

/// <summary>
/// Turns a blockout into the dressed level, following a rules file (see docs/dressing.md). Never mutates the document
/// and never throws on rule content: problems become diagnostics and the affected meshes stay as they are.
/// <para>
/// Work is split into jobs per (rule, mesh) and cached between calls: a job reruns only when its rule, its mesh, or the
/// meshes it touches change, so editing one wall rebuilds that wall's results and its neighbours'. Keep one dresser per
/// open level and call it from one thread.
/// </para>
/// </summary>
public sealed class Dresser(IDressingFiles files)
{
    RuleSet _rules = RuleSet.Empty;
    string? _rulesPath;
    long[] _stamps = [];
    int _nextId = -1, _nextRevision;
    readonly Dictionary<(string Rule, int Source), int> _ids = new();
    readonly Dictionary<(string Rule, int Mesh), ShapeJob> _shape = new();
    readonly Dictionary<(string Rule, int Mesh), SegmentJob> _segments = new();
    readonly Dictionary<(string Rule, int Mesh), OutputJob> _outputs = new();
    readonly Dictionary<int, (int Revision, Vector3 Min, Vector3 Max)> _bounds = new();
    (ulong Key, List<string> Tags) _tagSummary = (0, []);

    // Jobs keep their problem with their result, so a cached job keeps reporting it until its inputs change.
    sealed record ShapeJob(ulong Key, PolyMesh? Result, int Matched, int Edges, Diagnostic? Problem, int Revision);
    sealed record SegmentJob(ulong Key, List<SweepKind.Segment> Segments, int Matched, ulong Hash, Diagnostic? Problem);
    sealed record OutputJob(ulong Key, PolyMesh? Mesh, int Revision, Diagnostic? Problem);

    /// <summary>The rules last loaded.</summary>
    public RuleSet Rules => _rules;

    /// <summary>How many jobs the last <see cref="Dress"/> ran (rather than reused). For tests and the frame benchmark.</summary>
    public int LastRebuilds { get; private set; }

    /// <summary>
    /// Dresses <paramref name="doc"/> with the rules at <paramref name="rulesPath"/> (null: no rules, the level passes
    /// through). Rules reload when the path changes or, when <paramref name="checkFiles"/>, when any rules file changed
    /// on disk. <paramref name="revision"/> is the per-mesh change token (the editor passes Session.MeshRevision); without
    /// it meshes are content-hashed.
    /// </summary>
    public DressedLevel Dress(Document doc, string? rulesPath, Func<PolyMesh, int>? revision = null, bool checkFiles = true)
    {
        if (rulesPath != _rulesPath || (checkFiles && StampsChanged())) Reload(rulesPath);
        Func<PolyMesh, int> rev = revision ?? (m => unchecked((int)MeshHash.Of(m)));
        LastRebuilds = 0;

        var run = new Run(doc, doc.Meshes.Where(Dressable).ToList(), rev);
        run.Diagnostics.AddRange(_rules.Diagnostics);
        foreach (var rule in _rules.Rules)
        {
            var clock = Stopwatch.StartNew();
            if (!rule.Enabled)
            {
                run.Stats.Add(new RuleStats(rule.Id, rule.Kind.Name, rule.Kind.Stage, false, 0, "", 0, 0, 0));
                continue;
            }
            switch (rule.Kind)
            {
                case BevelKind: RunBevel(rule, run, clock); break;
                case SweepKind: RunSweep(rule, run, clock); break;
            }
        }
        foreach (var id in run.Conflicts) run.Replaced.Remove(id);

        var meshes = new List<PolyMesh>(doc.Meshes.Count + run.Added.Count);
        var info = new Dictionary<PolyMesh, (MeshOrigin, int)>(ReferenceEqualityComparer.Instance);
        foreach (var m in doc.Meshes)
        {
            if (run.Replaced.TryGetValue(m.Id, out var r))
            {
                meshes.Add(r.Mesh);
                info[r.Mesh] = (new MeshOrigin(m.Id, r.Rule, DressRole.Modified), r.Revision);
            }
            else
            {
                meshes.Add(m);
                info[m] = (new MeshOrigin(m.Id, null, DressRole.Passthrough), 0);
            }
        }
        foreach (var (mesh, rule, source, made) in run.Added)
        {
            meshes.Add(mesh);
            info[mesh] = (new MeshOrigin(source, rule, DressRole.Added), made);
        }
        Prune(doc);
        return new DressedLevel(doc, _rules, meshes, info, rev, run.Diagnostics, run.Stats);
    }

    /// <summary>One dressing pass: the live meshes and what the rules produced so far.</summary>
    sealed class Run(Document doc, List<PolyMesh> live, Func<PolyMesh, int> rev)
    {
        public readonly Document Doc = doc;
        public readonly List<PolyMesh> Live = live;
        public readonly Dictionary<int, int> Revisions = live.ToDictionary(m => m.Id, rev);
        public readonly List<Diagnostic> Diagnostics = new();
        public readonly List<RuleStats> Stats = new();
        public readonly Dictionary<int, (PolyMesh Mesh, string Rule, int Revision)> Replaced = new();
        public readonly Dictionary<int, string> ClaimedBy = new();
        public readonly HashSet<int> Conflicts = new();
        public readonly List<(PolyMesh Mesh, string Rule, int Source, int Revision)> Added = new();
        public BoundsGrid? Grid;
    }

    /// <summary>Meshes dressing reads: visible and rendered (collision-only meshes are neither dressed nor stood on).</summary>
    static bool Dressable(PolyMesh m) => !m.Hidden && m.Collision is not (CollisionMode.Only or CollisionMode.ConvexOnly) && m.Faces.Count > 0;

    void RunBevel(Rule rule, Run run, Stopwatch clock)
    {
        var live = run.Live;
        var jobs = new ShapeJob[live.Count];
        var dirty = new List<int>();
        for (int i = 0; i < live.Count; i++)
        {
            ulong key = Key(rule.Fingerprint, (ulong)run.Revisions[live[i].Id]);
            if (_shape.TryGetValue((rule.Id, live[i].Id), out var job) && job.Key == key) jobs[i] = job;
            else { dirty.Add(i); jobs[i] = new ShapeJob(key, null, 0, 0, null, 0); }
        }
        Parallel.ForEach(dirty, i =>
        {
            var m = live[i];
            try
            {
                var (result, matched, edges, problem) = BevelKind.Apply(m, rule);
                if (result != null) Generated(result, rule.Id, m.Id);
                jobs[i] = jobs[i] with { Result = result, Matched = matched, Edges = edges, Problem = problem };
            }
            catch (Exception e) { jobs[i] = jobs[i] with { Problem = Failed(rule, m, e) }; }
        });
        foreach (var i in dirty) _shape[(rule.Id, live[i].Id)] = jobs[i] = jobs[i] with { Revision = ++_nextRevision };
        LastRebuilds += dirty.Count;

        int matchedFaces = 0, targets = 0, outputs = 0, skipped = 0;
        for (int i = 0; i < live.Count; i++)
        {
            var job = jobs[i];
            int id = live[i].Id;
            matchedFaces += job.Matched;
            if (job.Edges > 0) targets++;
            if (job.Problem != null) { run.Diagnostics.Add(job.Problem); skipped++; }
            if (job.Result == null) continue;
            outputs++;
            if (run.ClaimedBy.TryGetValue(id, out var other))
            {
                if (run.Conflicts.Add(id))
                    run.Diagnostics.Add(new Diagnostic(Severity.Error, "E330", $"rules \"{other}\" and \"{rule.Id}\" both reshape \"{live[i].Name}\"; neither applies",
                        $"a mesh takes one shape rule: narrow one rule's faces, or tag the faces \"{rule.SkipTag}\"", MeshId: id, Rule: rule.Id));
                continue;
            }
            run.ClaimedBy[id] = rule.Id;
            run.Replaced[id] = (job.Result, rule.Id, job.Revision);
        }
        if (targets == 0 && skipped == 0)
            run.Diagnostics.Add(NothingMatched(rule, run, matchedFaces == 0 ? null :
                $"{matchedFaces} faces match, but no edge between two of them is {rule.Text("edges")} at {rule.Number("minDeg"):0}° or more"));
        run.Stats.Add(new RuleStats(rule.Id, rule.Kind.Name, Stage.Shape, true, targets, "mesh", outputs, skipped, clock.Elapsed.TotalMilliseconds));
    }

    void RunSweep(Rule rule, Run run, Stopwatch clock)
    {
        var live = run.Live;
        bool contact = rule.Text("edges") == "contact";
        if (contact) run.Grid ??= new BoundsGrid(live.Select(m => (m, Bounds(m, run.Revisions[m.Id]))));
        var segs = new SegmentJob[live.Count];
        var neighbours = new List<PolyMesh>[live.Count];
        var dirty = new List<int>();
        for (int i = 0; i < live.Count; i++)
        {
            var m = live[i];
            ulong key = Key(rule.Fingerprint, (ulong)run.Revisions[m.Id]);
            if (contact)
            {
                neighbours[i] = run.Grid!.Touching(m);
                foreach (var n in neighbours[i]) key = Key(key, ((ulong)(uint)n.Id << 32) | (uint)run.Revisions[n.Id]);
            }
            if (_segments.TryGetValue((rule.Id, m.Id), out var job) && job.Key == key) segs[i] = job;
            else { dirty.Add(i); segs[i] = new SegmentJob(key, [], 0, 0, null); }
        }
        Parallel.ForEach(dirty, i =>
        {
            try
            {
                var (list, matched) = SweepKind.Segments(live[i], rule, contact ? neighbours[i] : [live[i]]);
                segs[i] = segs[i] with { Segments = list, Matched = matched, Hash = HashSegments(list) };
            }
            catch (Exception e) { segs[i] = segs[i] with { Problem = Failed(rule, live[i], e) }; }
        });
        foreach (var i in dirty) _segments[(rule.Id, live[i].Id)] = segs[i];
        LastRebuilds += dirty.Count;

        // Join segment ends across all meshes, then rebuild a mesh's sweep only if its segments or their joints changed.
        var all = new List<(int Mesh, int Index, SweepKind.Segment Seg)>();
        for (int i = 0; i < live.Count; i++)
            for (int s = 0; s < segs[i].Segments.Count; s++) all.Add((i, s, segs[i].Segments[s]));
        var joints = Join(all, out var openEnds);

        int matchedFaces = 0, segmentCount = 0, before = run.Added.Count;
        var build = new List<(int Index, ulong Key, (SweepKind.Joint?, SweepKind.Joint?)[] Joints)>();
        int cursor = 0;
        for (int i = 0; i < live.Count; i++)
        {
            matchedFaces += segs[i].Matched;
            if (segs[i].Problem != null) run.Diagnostics.Add(segs[i].Problem!);
            int n = segs[i].Segments.Count;
            segmentCount += n;
            if (n == 0) continue;
            var mine = new (SweepKind.Joint?, SweepKind.Joint?)[n];
            ulong key = Key(rule.Fingerprint, segs[i].Hash);
            for (int s = 0; s < n; s++)
            {
                mine[s] = joints[cursor + s];
                key = Key(Key(key, HashJoint(mine[s].Item1)), HashJoint(mine[s].Item2));
            }
            cursor += n;
            if (_outputs.TryGetValue((rule.Id, live[i].Id), out var job) && job.Key == key)
            {
                if (job.Mesh != null) run.Added.Add((job.Mesh, rule.Id, live[i].Id, job.Revision));
                if (job.Problem != null) run.Diagnostics.Add(job.Problem);
            }
            else build.Add((i, key, mine));
        }
        var built = new OutputJob[build.Count];
        Parallel.For(0, build.Count, b =>
        {
            var (i, key, js) = build[b];
            try
            {
                var mesh = SweepKind.Build(rule, segs[i].Segments, js, $"{rule.Id}_{live[i].Name}");
                Generated(mesh, rule.Id, live[i].Id);
                built[b] = new OutputJob(key, mesh, 0, null);
            }
            catch (Exception e) { built[b] = new OutputJob(key, null, 0, Failed(rule, live[i], e)); }
        });
        for (int b = 0; b < build.Count; b++)
        {
            int i = build[b].Index;
            var job = _outputs[(rule.Id, live[i].Id)] = built[b] with { Revision = ++_nextRevision };
            if (job.Mesh != null) run.Added.Add((job.Mesh, rule.Id, live[i].Id, job.Revision));
            if (job.Problem != null) run.Diagnostics.Add(job.Problem);
        }
        LastRebuilds += build.Count;

        if (segmentCount == 0)
            run.Diagnostics.Add(NothingMatched(rule, run, matchedFaces == 0 ? null : rule.Text("edges") switch
            {
                "contact" => $"{matchedFaces} faces match, but none has an edge lying on a face matching \"onto\" ({Describe(rule.Get<FaceSelector>("onto"))})",
                var k => $"{matchedFaces} faces match, but none has a {k} edge",
            }));
        foreach (var gap in Gaps(openEnds, all, live).Take(10)) run.Diagnostics.Add(gap with { Rule = rule.Id, Path = $"rules.{rule.Id}" });
        run.Stats.Add(new RuleStats(rule.Id, rule.Kind.Name, Stage.Dress, true, segmentCount, "edge", run.Added.Count - before, 0, clock.Elapsed.TotalMilliseconds));
    }

    /// <summary>
    /// For every segment end, the segment it joins at that point (if exactly one other end meets it there with a
    /// compatible frame). Ends that join nothing come back in <paramref name="openEnds"/>.
    /// </summary>
    static (SweepKind.Joint?, SweepKind.Joint?)[] Join(List<(int Mesh, int Index, SweepKind.Segment Seg)> all, out List<(int Seg, bool Start)> openEnds)
    {
        var result = new (SweepKind.Joint?, SweepKind.Joint?)[all.Count];
        var weld = new PointWeld(SweepKind.Tolerance);
        var nodes = new Dictionary<int, List<(int Seg, bool Start)>>();
        for (int i = 0; i < all.Count; i++)
            foreach (var start in (bool[])[true, false])
            {
                int node = weld.Id(start ? all[i].Seg.A : all[i].Seg.B);
                (nodes.TryGetValue(node, out var l) ? l : nodes[node] = new()).Add((i, start));
            }
        openEnds = new();
        foreach (var ends in nodes.Values)
        {
            if (ends.Count == 2)
            {
                var (i, si) = ends[0];
                var (j, sj) = ends[1];
                var a = all[i].Seg;
                var b = all[j].Seg;
                var awayA = si ? a.Dir : -a.Dir;
                var awayB = sj ? b.Dir : -b.Dir;
                if (i != j && Vector3.Dot(awayA, awayB) < 0.95f && Vector3.Dot(a.Up, b.Up) > 0.5f)
                {
                    var jb = new SweepKind.Joint(awayB, b.Out, b.Up);
                    var ja = new SweepKind.Joint(awayA, a.Out, a.Up);
                    if (si) result[i].Item1 = jb; else result[i].Item2 = jb;
                    if (sj) result[j].Item1 = ja; else result[j].Item2 = ja;
                    continue;
                }
            }
            openEnds.AddRange(ends);
        }
        return result;
    }

    /// <summary>Open ends that nearly meet another (within 5 cm, but further than the weld tolerance): likely blockout gaps.</summary>
    static IEnumerable<Diagnostic> Gaps(List<(int Seg, bool Start)> openEnds, List<(int Mesh, int Index, SweepKind.Segment Seg)> all, List<PolyMesh> live)
    {
        const float near = 0.05f;
        var points = openEnds.Select(e => (e.Seg, P: e.Start ? all[e.Seg].Seg.A : all[e.Seg].Seg.B)).ToList();
        var cells = new Dictionary<(int, int, int), List<int>>();
        (int, int, int) Cell(Vector3 p) => ((int)MathF.Floor(p.X / near), (int)MathF.Floor(p.Y / near), (int)MathF.Floor(p.Z / near));
        for (int i = 0; i < points.Count; i++) (cells.TryGetValue(Cell(points[i].P), out var l) ? l : cells[Cell(points[i].P)] = new()).Add(i);
        for (int i = 0; i < points.Count; i++)
        {
            var (cx, cy, cz) = Cell(points[i].P);
            for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
            for (int dz = -1; dz <= 1; dz++)
            {
                if (!cells.TryGetValue((cx + dx, cy + dy, cz + dz), out var list)) continue;
                foreach (var j in list)
                {
                    if (j <= i) continue;
                    float d = Vector3.Distance(points[i].P, points[j].P);
                    if (d <= SweepKind.Tolerance || d > near) continue;
                    var p = points[i].P;
                    var mi = live[all[points[i].Seg].Mesh];
                    var mj = live[all[points[j].Seg].Mesh];
                    yield return new Diagnostic(Severity.Warning, "W520",
                        $"trim gap of {d * 1000:0.#} mm at ({p.X:0.###}, {p.Y:0.###}, {p.Z:0.###}) between \"{mi.Name}\" and \"{mj.Name}\"",
                        "move the meshes so they meet (snap to grid), or weld the vertices", MeshId: mi.Id);
                }
            }
        }
    }

    Diagnostic NothingMatched(Rule rule, Run run, string? why)
    {
        if (why != null)
            return new Diagnostic(Severity.Warning, "W402", $"{rule.Id} matched nothing: {why}", "adjust the rule's edges, minDeg or onto", rule.Pos, $"rules.{rule.Id}", Rule: rule.Id);
        var tags = TagSummary(run);
        var wanted = rule.Get<FaceSelector>("faces").Tags;
        string fix = wanted.Length > 0 && Suggest.Closest(wanted[0], tags.Select(t => t[..t.IndexOf(' ')])) is { } g && g != wanted[0]
            ? $"did you mean the tag \"{g}\"?"
            : $"tag the faces (Ctrl+T in Chip){(tags.Count > 0 ? $"; tags in the level: {string.Join(", ", tags)}" : "; the level has no tags yet")}";
        return new Diagnostic(Severity.Warning, "W402", $"{rule.Id} matched nothing: no face matches {Describe(rule.Get<FaceSelector>("faces"))}", fix, rule.Pos, $"rules.{rule.Id}", Rule: rule.Id);
    }

    /// <summary>The level's most used face tags ("wall ×212"), recounted only when some mesh changed.</summary>
    List<string> TagSummary(Run run)
    {
        ulong key = 1;
        foreach (var m in run.Live) key = Key(key, ((ulong)(uint)m.Id << 32) | (uint)run.Revisions[m.Id]);
        if (_tagSummary.Key == key) return _tagSummary.Tags;
        var tags = run.Live.SelectMany(m => m.Faces).SelectMany(f => f.Tags).GroupBy(t => t).OrderByDescending(g => g.Count()).Take(8)
            .Select(g => $"{g.Key} ×{g.Count()}").ToList();
        _tagSummary = (key, tags);
        return tags;
    }

    static string Describe(FaceSelector s)
    {
        var parts = new List<string>();
        if (s.Tags.Length > 0) parts.Add($"tags {string.Join(" + ", s.Tags)}");
        if (s.NotTags.Length > 0) parts.Add($"not {string.Join(" / ", s.NotTags)}");
        if (s.Materials.Length > 0) parts.Add($"materials {string.Join(" / ", s.Materials)}");
        if (s.Facing != null) parts.Add($"facing {s.Facing}");
        return parts.Count == 0 ? "any face" : string.Join(", ", parts);
    }

    static Diagnostic Failed(Rule rule, PolyMesh m, Exception e) =>
        new(Severity.Error, "E901", $"{rule.Id} failed on \"{m.Name}\": {e.Message}", $"tag its faces \"{rule.SkipTag}\" to skip it, and report the bug",
            MeshId: m.Id, Rule: rule.Id);

    /// <summary>Gives a generated mesh its stable negative id and marks it as not editable or colliding.</summary>
    void Generated(PolyMesh mesh, string rule, int source)
    {
        lock (_ids)
        {
            if (!_ids.TryGetValue((rule, source), out var id)) _ids[(rule, source)] = id = _nextId--;
            mesh.Id = id;
        }
        mesh.Locked = true;
        mesh.Collision = CollisionMode.None;
        mesh.Tags = default;
    }

    (Vector3 Min, Vector3 Max) Bounds(PolyMesh m, int revision)
    {
        if (_bounds.TryGetValue(m.Id, out var b) && b.Revision == revision) return (b.Min, b.Max);
        var (min, max) = m.Bounds();
        _bounds[m.Id] = (revision, min, max);
        return (min, max);
    }

    void Reload(string? path)
    {
        _rulesPath = path;
        _rules = path == null ? RuleSet.Empty : RuleSet.Load(files, path);
        _stamps = (path == null ? [] : _rules.Files.DefaultIfEmpty(path)).Select(files.Stamp).ToArray();
    }

    bool StampsChanged()
    {
        if (_rulesPath == null) return false;
        var paths = _rules.Files.DefaultIfEmpty(_rulesPath).ToList();
        if (paths.Count != _stamps.Length) return true;
        for (int i = 0; i < paths.Count; i++) if (files.Stamp(paths[i]) != _stamps[i]) return true;
        return false;
    }

    /// <summary>Drops cached jobs for meshes and rules that no longer exist.</summary>
    void Prune(Document doc)
    {
        var ids = doc.Meshes.Select(m => m.Id).ToHashSet();
        var rules = _rules.Rules.Select(r => r.Id).ToHashSet();
        foreach (var k in _shape.Keys.Where(k => !ids.Contains(k.Mesh) || !rules.Contains(k.Rule)).ToList()) _shape.Remove(k);
        foreach (var k in _segments.Keys.Where(k => !ids.Contains(k.Mesh) || !rules.Contains(k.Rule)).ToList()) _segments.Remove(k);
        foreach (var k in _outputs.Keys.Where(k => !ids.Contains(k.Mesh) || !rules.Contains(k.Rule)).ToList()) _outputs.Remove(k);
        foreach (var k in _bounds.Keys.Where(k => !ids.Contains(k)).ToList()) _bounds.Remove(k);
    }

    static ulong Key(ulong h, ulong x)
    {
        h ^= x + 0x9E3779B97F4A7C15UL + (h << 6) + (h >> 2);
        return h * 1099511628211UL;
    }

    static ulong HashVec(ulong h, Vector3 v) =>
        Key(Key(Key(h, BitConverter.SingleToUInt32Bits(v.X)), BitConverter.SingleToUInt32Bits(v.Y)), BitConverter.SingleToUInt32Bits(v.Z));

    static ulong HashSegments(List<SweepKind.Segment> segs)
    {
        ulong h = 14695981039346656037UL;
        foreach (var s in segs)
        {
            h = HashVec(HashVec(HashVec(HashVec(h, s.A), s.B), s.Out), s.Up);
            foreach (var c in s.Material) h = Key(h, c);
            var map = s.Map;
            h = HashVec(HashVec(h, map.UAxis), map.VAxis);
            foreach (var f in (float[])[map.ScaleU, map.ScaleV, map.ShiftU, map.ShiftV, map.Rotation]) h = Key(h, BitConverter.SingleToUInt32Bits(f));
        }
        return h;
    }

    static ulong HashJoint(SweepKind.Joint? j) =>
        j is { } x ? HashVec(HashVec(HashVec(1, x.Away), x.Out), x.Up) : 0;

    /// <summary>Mesh bounds in a uniform grid, for "which meshes touch this one" (very large meshes are checked directly).</summary>
    sealed class BoundsGrid
    {
        const float Cell = 4f;
        const int MaxCells = 512;
        readonly Dictionary<(int, int, int), List<(PolyMesh Mesh, Vector3 Min, Vector3 Max)>> _cells = new();
        readonly List<(PolyMesh Mesh, Vector3 Min, Vector3 Max)> _big = new();
        readonly Dictionary<PolyMesh, (Vector3 Min, Vector3 Max)> _bounds = new(ReferenceEqualityComparer.Instance);

        public BoundsGrid(IEnumerable<(PolyMesh Mesh, (Vector3 Min, Vector3 Max) Box)> meshes)
        {
            foreach (var (m, (min0, max0)) in meshes)
            {
                var min = min0 - new Vector3(SweepKind.Tolerance);
                var max = max0 + new Vector3(SweepKind.Tolerance);
                _bounds[m] = (min, max);
                var (a, b) = (CellOf(min), CellOf(max));
                long count = (long)(b.Item1 - a.Item1 + 1) * (b.Item2 - a.Item2 + 1) * (b.Item3 - a.Item3 + 1);
                if (count > MaxCells) { _big.Add((m, min, max)); continue; }
                for (int x = a.Item1; x <= b.Item1; x++)
                for (int y = a.Item2; y <= b.Item2; y++)
                for (int z = a.Item3; z <= b.Item3; z++)
                    (_cells.TryGetValue((x, y, z), out var l) ? l : _cells[(x, y, z)] = new()).Add((m, min, max));
            }
        }

        static (int, int, int) CellOf(Vector3 p) => ((int)MathF.Floor(p.X / Cell), (int)MathF.Floor(p.Y / Cell), (int)MathF.Floor(p.Z / Cell));

        /// <summary>Meshes whose bounds overlap <paramref name="m"/>'s (itself included), ordered by id.</summary>
        public List<PolyMesh> Touching(PolyMesh m)
        {
            var (min, max) = _bounds[m];
            var found = new HashSet<PolyMesh>(ReferenceEqualityComparer.Instance);
            bool Overlap(Vector3 bmin, Vector3 bmax) => bmin.X <= max.X && bmax.X >= min.X && bmin.Y <= max.Y && bmax.Y >= min.Y && bmin.Z <= max.Z && bmax.Z >= min.Z;
            foreach (var b in _big) if (Overlap(b.Min, b.Max)) found.Add(b.Mesh);
            var (a, c) = (CellOf(min), CellOf(max));
            long count = (long)(c.Item1 - a.Item1 + 1) * (c.Item2 - a.Item2 + 1) * (c.Item3 - a.Item3 + 1);
            if (count > MaxCells)
            {
                foreach (var (other, (bmin, bmax)) in _bounds) if (Overlap(bmin, bmax)) found.Add(other);
            }
            else
                for (int x = a.Item1; x <= c.Item1; x++)
                for (int y = a.Item2; y <= c.Item2; y++)
                for (int z = a.Item3; z <= c.Item3; z++)
                    if (_cells.TryGetValue((x, y, z), out var l))
                        foreach (var b in l) if (Overlap(b.Min, b.Max)) found.Add(b.Mesh);
            found.Add(m);
            return found.OrderBy(x => x.Id).ToList();
        }
    }

    /// <summary>Gives points within <c>tolerance</c> of each other the same id.</summary>
    sealed class PointWeld(float tolerance)
    {
        readonly Dictionary<(int, int, int), List<(Vector3 P, int Id)>> _cells = new();
        int _next;

        public int Id(Vector3 p)
        {
            var c = ((int)MathF.Floor(p.X / tolerance), (int)MathF.Floor(p.Y / tolerance), (int)MathF.Floor(p.Z / tolerance));
            for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
            for (int dz = -1; dz <= 1; dz++)
                if (_cells.TryGetValue((c.Item1 + dx, c.Item2 + dy, c.Item3 + dz), out var list))
                    foreach (var (q, id) in list)
                        if (Vector3.DistanceSquared(p, q) <= tolerance * tolerance) return id;
            (_cells.TryGetValue(c, out var own) ? own : _cells[c] = new()).Add((p, _next));
            return _next++;
        }
    }
}
