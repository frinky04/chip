using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
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
    int _nextId = -1;
    /// <summary>Shared by every dresser: ids restart per dresser, so revisions must never repeat across them (GPU caches key on both).</summary>
    static int _nextRevision;
    static int NextRevision() => Interlocked.Increment(ref _nextRevision);
    readonly Dictionary<(string Rule, int Source), int> _ids = new();
    readonly Dictionary<(string Rule, int Mesh), ShapeJob> _shape = new();
    readonly Dictionary<(string Rule, int Mesh), SegmentJob> _segments = new();
    readonly Dictionary<(string Rule, int Mesh), OutputJob> _outputs = new();
    readonly Dictionary<int, (int Revision, Vector3 Min, Vector3 Max)> _bounds = new();
    (ulong Key, List<string> Tags) _tagSummary = (0, []);
    readonly PropLibrary _props = new(files);
    long _propStamp;
    /// <summary>Rules with one mesh's overrides laid over them, by (rule, rule fingerprint, override JSON).</summary>
    readonly Dictionary<(string Rule, ulong Fingerprint, string Json), (Rule Rule, List<Diagnostic> Problems)> _overridden = new();

    // Jobs keep their problem with their result, so a cached job keeps reporting it until its inputs change.
    sealed record ShapeJob(ulong Key, PolyMesh? Result, int Matched, int Edges, Diagnostic? Problem, int Revision);
    sealed record SegmentJob(ulong Key, List<SweepKind.Segment> Segments, int Matched, ulong Hash, Diagnostic? Problem);
    sealed record OutputJob(ulong Key, PolyMesh? Mesh, int Revision, Diagnostic? Problem)
    {
        /// <summary>Scatter: how many faces matched (for stats and the zero-match warning).</summary>
        public int Faces { get; init; }
    }

    /// <summary>The rules last loaded.</summary>
    public RuleSet Rules => _rules;

    /// <summary>Whether <see cref="Dress"/> would load different rules or props: another path, or a rules or prop file changed.</summary>
    public bool RulesChanged(string? rulesPath) => rulesPath != _rulesPath || StampsChanged() || PropStamp() != _propStamp;

    /// <summary>Every prop file the rules name, stamped (an edited .glb re-dresses).</summary>
    long PropStamp() => _props.Stamp(_rules.Rules.SelectMany(r => r.Values.Values).SelectMany(v => v switch
    {
        PropSpec p => [p],
        PropSpec[] ps => ps,
        _ => Array.Empty<PropSpec>(),
    }));

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
        if (checkFiles || _last == null) _propStamp = PropStamp();
        Func<PolyMesh, int> rev = revision ?? (m => unchecked((int)MeshHash.Of(m)));
        LastRebuilds = 0;

        // Nothing changed since the last pass (same rules and props, same mesh objects at the same revisions): same result.
        ulong state = Key(Key(Key(1, (ulong)RuntimeHelpers.GetHashCode(_rules)), (ulong)RuntimeHelpers.GetHashCode(doc)), (ulong)_propStamp);
        foreach (var m in doc.Meshes)
            state = Key(Key(state, ((ulong)(uint)m.Id << 32) | (uint)rev(m)),
                ((ulong)(uint)RuntimeHelpers.GetHashCode(m) << 2) | (m.Hidden ? 1UL : 0) | ((ulong)m.Collision << 1));
        if (_last is { } last && _lastState == state) return last;

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
                case BevelKind or ProxyKind: RunShape(rule, run, clock); break;
                case SweepKind or RepeatKind: RunEdges(rule, run, clock); break;
                case ScatterKind: RunScatter(rule, run, clock); break;
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
                info[r.Mesh] = (new MeshOrigin(m.Id, r.Rule, r.Role), r.Revision);
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
        _lastState = state;
        return _last = new DressedLevel(doc, _rules, meshes, info, rev, run.Diagnostics, run.Stats) { PropTextures = _props.Textures() };
    }

    DressedLevel? _last;
    ulong _lastState, _prunedFor;

    /// <summary>One dressing pass: the live meshes and what the rules produced so far.</summary>
    sealed class Run(Document doc, List<PolyMesh> live, Func<PolyMesh, int> rev)
    {
        public readonly Document Doc = doc;
        public readonly List<PolyMesh> Live = live;
        public readonly Dictionary<int, int> Revisions = live.ToDictionary(m => m.Id, rev);
        public readonly List<Diagnostic> Diagnostics = new();
        public readonly List<RuleStats> Stats = new();
        public readonly Dictionary<int, (PolyMesh Mesh, string Rule, int Revision, DressRole Role)> Replaced = new();
        public readonly Dictionary<int, string> ClaimedBy = new();
        public readonly HashSet<int> Conflicts = new();
        public readonly List<(PolyMesh Mesh, string Rule, int Source, int Revision)> Added = new();
        public readonly Dictionary<int, PolyMesh> ById = live.ToDictionary(m => m.Id);
        public bool NeighboursCurrent;
    }

    readonly NeighbourIndex _neighbours = new();

    /// <summary>
    /// Which meshes touch which (bounds overlap, within the contact tolerance), kept between passes: after an edit only the
    /// changed meshes and those near their old or new bounds are re-queried, so a drag doesn't rescan the level.
    /// </summary>
    sealed class NeighbourIndex
    {
        readonly Dictionary<int, (int Revision, Vector3 Min, Vector3 Max)> _seen = new();
        readonly Dictionary<int, int[]> _lists = new();

        public int[] Of(int id) => _lists.TryGetValue(id, out var l) ? l : [id];

        public void Update(Run run, Func<PolyMesh, int, (Vector3 Min, Vector3 Max)> bounds)
        {
            var live = run.Live;
            var boxes = new List<(Vector3 Min, Vector3 Max)>();
            var changed = new List<PolyMesh>();
            foreach (var m in live)
            {
                int rev = run.Revisions[m.Id];
                bool known = _seen.TryGetValue(m.Id, out var old);
                if (known && old.Revision == rev) continue;
                if (known) boxes.Add((old.Min, old.Max));
                var (min, max) = bounds(m, rev);
                boxes.Add((min, max));
                changed.Add(m);
                _seen[m.Id] = (rev, min, max);
            }
            foreach (var id in _seen.Keys.Where(id => !run.ById.ContainsKey(id)).ToList())
            {
                boxes.Add((_seen[id].Min, _seen[id].Max));
                _seen.Remove(id);
                _lists.Remove(id);
            }
            if (boxes.Count == 0) return;

            if (_lists.Count == 0 || changed.Count > live.Count / 8)
            {
                var grid = new BoundsGrid(live.Select(m => (m, (_seen[m.Id].Min, _seen[m.Id].Max))));
                foreach (var m in live) _lists[m.Id] = grid.Touching(m).Select(n => n.Id).ToArray();
                return;
            }
            var tol = new Vector3(SweepKind.Tolerance);
            bool Overlap((Vector3 Min, Vector3 Max) a, (Vector3 Min, Vector3 Max) b) =>
                a.Min.X <= b.Max.X + tol.X && a.Max.X + tol.X >= b.Min.X && a.Min.Y <= b.Max.Y + tol.Y && a.Max.Y + tol.Y >= b.Min.Y &&
                a.Min.Z <= b.Max.Z + tol.Z && a.Max.Z + tol.Z >= b.Min.Z;
            foreach (var m in live)
            {
                var box = (_seen[m.Id].Min, _seen[m.Id].Max);
                if (!changed.Contains(m) && !boxes.Any(b => Overlap(box, b))) continue;
                var list = new List<int>();
                foreach (var o in live)
                    if (o == m || Overlap(box, (_seen[o.Id].Min, _seen[o.Id].Max))) list.Add(o.Id);
                list.Sort();
                _lists[m.Id] = list.ToArray();
            }
        }
    }

    /// <summary>Meshes dressing reads: visible and rendered (collision-only meshes are neither dressed nor stood on).</summary>
    static bool Dressable(PolyMesh m) => !m.Hidden && m.Collision is not (CollisionMode.Only or CollisionMode.ConvexOnly) && m.Faces.Count > 0;

    /// <summary>
    /// <paramref name="rule"/> as it applies to <paramref name="m"/>: with the mesh's overrides laid over it (cached), and
    /// any problems in those overrides added to the pass. Null when the mesh switched the rule off.
    /// </summary>
    Rule? Effective(Rule rule, PolyMesh m, Run run)
    {
        if (m.Dressing.Skips(rule.Id)) return null;
        if (m.Dressing.OverridesFor(rule.Id) is not { } json) return rule;
        var key = (rule.Id, rule.Fingerprint, json);
        (Rule Rule, List<Diagnostic> Problems) entry;
        lock (_overridden)
            if (!_overridden.TryGetValue(key, out entry))
            {
                var problems = new List<Diagnostic>();
                entry = _overridden[key] = (_rules.WithOverrides(rule, json, m.Id, m.Name, problems), problems);
            }
        lock (run.Diagnostics) run.Diagnostics.AddRange(entry.Problems.Select(p => p with { MeshId = m.Id }));
        return entry.Rule;
    }

    /// <summary>Shape rules (bevel, proxies): each mesh either keeps its blockout form or is replaced by what the rule builds.</summary>
    void RunShape(Rule rule, Run run, Stopwatch clock)
    {
        var live = run.Live;
        var jobs = new ShapeJob[live.Count];
        var effective = new Rule?[live.Count];
        var dirty = new List<int>();
        for (int i = 0; i < live.Count; i++)
        {
            if ((effective[i] = Effective(rule, live[i], run)) is not { } eff) { jobs[i] = new ShapeJob(0, null, 0, 0, null, 0); continue; }
            ulong key = Key(eff.Fingerprint, (ulong)run.Revisions[live[i].Id]);
            if (_shape.TryGetValue((rule.Id, live[i].Id), out var job) && job.Key == key) jobs[i] = job;
            else { dirty.Add(i); jobs[i] = new ShapeJob(key, null, 0, 0, null, 0); }
        }
        Parallel.ForEach(dirty, i =>
        {
            var m = live[i];
            var eff = effective[i]!;
            try
            {
                if (rule.Kind is ProxyKind proxy)
                {
                    if (!eff.Get<MeshSelector>("meshes").Matches(m)) return;
                    var (built, why) = proxy.Build(m, eff);
                    if (built != null) Generated(built, rule.Id, m.Id);
                    jobs[i] = jobs[i] with
                    {
                        Result = built, Matched = 1, Edges = 1,
                        Problem = why == null ? null : new Diagnostic(Severity.Warning, "W320", $"{rule.Id}: {why}; left as a box",
                            "make the box bigger, or remove its tag", MeshId: m.Id, Rule: rule.Id),
                    };
                    return;
                }
                var (result, matched, edges, problem) = BevelKind.Apply(m, eff);
                if (result != null) Generated(result, rule.Id, m.Id);
                jobs[i] = jobs[i] with { Result = result, Matched = matched, Edges = edges, Problem = problem };
            }
            catch (Exception e) { jobs[i] = jobs[i] with { Problem = Failed(rule, m, e) }; }
        });
        foreach (var i in dirty) _shape[(rule.Id, live[i].Id)] = jobs[i] = jobs[i] with { Revision = NextRevision() };
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
                        $"a mesh takes one shape rule: narrow one rule's selector, or switch one off for this mesh in the inspector", MeshId: id, Rule: rule.Id));
                continue;
            }
            run.ClaimedBy[id] = rule.Id;
            run.Replaced[id] = (job.Result, rule.Id, job.Revision, rule.Kind is ProxyKind ? DressRole.Proxy : DressRole.Modified);
        }
        if (targets == 0 && skipped == 0)
            run.Diagnostics.Add(rule.Kind is ProxyKind ? NoMeshMatched(rule, run) : NothingMatched(rule, run, matchedFaces == 0 ? null :
                $"{matchedFaces} faces match, but no edge between two of them is {rule.Text("edges")} at {rule.Number("minDeg"):0}° or more"));
        run.Stats.Add(new RuleStats(rule.Id, rule.Kind.Name, Stage.Shape, true, targets, "mesh", outputs, skipped, clock.Elapsed.TotalMilliseconds));
    }

    /// <summary>Edge rules (sweep, repeat): segments per mesh from the edges they follow, then a mesh built along them.</summary>
    void RunEdges(Rule rule, Run run, Stopwatch clock)
    {
        var live = run.Live;
        bool contact = rule.Text("edges") == "contact";
        bool sweep = rule.Kind is SweepKind;
        if (contact && !run.NeighboursCurrent)
        {
            _neighbours.Update(run, Bounds);
            run.NeighboursCurrent = true;
        }
        var segs = new SegmentJob[live.Count];
        var effective = new Rule?[live.Count];
        var dirty = new List<int>();
        for (int i = 0; i < live.Count; i++)
        {
            var m = live[i];
            if ((effective[i] = Effective(rule, m, run)) is not { } eff) { segs[i] = new SegmentJob(0, [], 0, 0, null); continue; }
            ulong key = Key(eff.Fingerprint, (ulong)run.Revisions[m.Id]);
            if (contact)
                foreach (var n in _neighbours.Of(m.Id)) key = Key(key, ((ulong)(uint)n << 32) | (uint)run.Revisions[n]);
            if (_segments.TryGetValue((rule.Id, m.Id), out var job) && job.Key == key) segs[i] = job;
            else { dirty.Add(i); segs[i] = new SegmentJob(key, [], 0, 0, null); }
        }
        Parallel.ForEach(dirty, i =>
        {
            try
            {
                var near = contact ? _neighbours.Of(live[i].Id).Select(id => run.ById[id]).ToList() : [live[i]];
                var (list, matched) = SweepKind.Segments(live[i], effective[i]!, near);
                segs[i] = segs[i] with { Segments = list, Matched = matched, Hash = HashSegments(list) };
            }
            catch (Exception e) { segs[i] = segs[i] with { Problem = Failed(rule, live[i], e) }; }
        });
        foreach (var i in dirty) _segments[(rule.Id, live[i].Id)] = segs[i];
        LastRebuilds += dirty.Count;

        // Sweeps join segment ends across meshes (incrementally: only near segments that changed), then rebuild a mesh's
        // sweep only if its segments or their joints changed. Repeats don't join; they rebuild when segments or props change.
        JoinIndex? join = null;
        if (sweep)
        {
            if (!_joins.TryGetValue(rule.Id, out join) || join.Fingerprint != rule.Fingerprint) _joins[rule.Id] = join = new JoinIndex(rule.Fingerprint);
            var changes = new Dictionary<int, List<SweepKind.Segment>>();
            for (int i = 0; i < live.Count; i++)
                if (!join.Holds(live[i].Id, segs[i].Segments)) changes[live[i].Id] = segs[i].Segments;
            foreach (var id in join.Meshes) if (!run.ById.ContainsKey(id)) changes[id] = [];
            join.Update(changes);
        }
        long propStamp = sweep ? 0 : _props.Stamp([rule.Get<PropSpec>("prop")]);

        int matchedFaces = 0, segmentCount = 0, before = run.Added.Count;
        var build = new List<(int Index, ulong Key, (SweepKind.Joint?, SweepKind.Joint?)[] Joints)>();
        var results = new OutputJob?[live.Count]; // reused and rebuilt alike, emitted in blockout order (stable exports)
        for (int i = 0; i < live.Count; i++)
        {
            matchedFaces += segs[i].Matched;
            if (segs[i].Problem != null) run.Diagnostics.Add(segs[i].Problem!);
            int n = segs[i].Segments.Count;
            segmentCount += n;
            if (n == 0) continue;
            var (mine, jointHash) = sweep ? join!.JointsOf(live[i].Id) : (new (SweepKind.Joint?, SweepKind.Joint?)[n], (ulong)propStamp);
            ulong key = Key(Key(effective[i]!.Fingerprint, segs[i].Hash), jointHash);
            if (_outputs.TryGetValue((rule.Id, live[i].Id), out var job) && job.Key == key) results[i] = job;
            else build.Add((i, key, mine));
        }
        var built = new OutputJob[build.Count];
        Parallel.For(0, build.Count, b =>
        {
            var (i, key, js) = build[b];
            var eff = effective[i]!;
            try
            {
                string name = $"{rule.Id}_{live[i].Name}";
                string? why = null;
                var mesh = sweep ? SweepKind.Build(eff, segs[i].Segments, js, name) : RepeatKind.Build(eff, segs[i].Segments, _props, name, out why);
                if (mesh.Faces.Count > 0) Generated(mesh, rule.Id, live[i].Id);
                built[b] = new OutputJob(key, mesh.Faces.Count > 0 ? mesh : null, 0, why == null ? null : PropProblem(rule, why));
            }
            catch (Exception e) { built[b] = new OutputJob(key, null, 0, Failed(rule, live[i], e)); }
        });
        for (int b = 0; b < build.Count; b++)
            results[build[b].Index] = _outputs[(rule.Id, live[build[b].Index].Id)] = built[b] with { Revision = NextRevision() };
        LastRebuilds += build.Count;
        var reported = new HashSet<string>();
        for (int i = 0; i < live.Count; i++)
        {
            if (results[i] is not { } job) continue;
            if (job.Mesh != null) run.Added.Add((job.Mesh, rule.Id, live[i].Id, job.Revision));
            if (job.Problem != null && reported.Add(job.Problem.Message)) run.Diagnostics.Add(job.Problem); // a missing prop once, not per mesh
        }

        if (segmentCount == 0)
            run.Diagnostics.Add(NothingMatched(rule, run, matchedFaces == 0 ? null : rule.Text("edges") switch
            {
                "contact" => $"{matchedFaces} faces match, but none has an edge lying on a face matching \"onto\" ({Describe(rule.Get<FaceSelector>("onto"))})",
                var k => $"{matchedFaces} faces match, but none has a {k} edge",
            }));
        if (join != null)
        {
            if (join.Changed || !_gaps.ContainsKey(rule.Id))
                _gaps[rule.Id] = Gaps(join.OpenEnds().OrderBy(e => e.Mesh).ThenBy(e => e.P.X).ThenBy(e => e.P.Y).ThenBy(e => e.P.Z).ToList(), run)
                    .Take(10).Select(g => g with { Rule = rule.Id, Path = $"rules.{rule.Id}" }).ToList();
            run.Diagnostics.AddRange(_gaps[rule.Id]);
        }
        run.Stats.Add(new RuleStats(rule.Id, rule.Kind.Name, Stage.Dress, true, segmentCount, "edge", run.Added.Count - before, 0, clock.Elapsed.TotalMilliseconds));
    }

    /// <summary>Scatter: per mesh, props over its matching faces, rebuilt when the mesh, the rule or the props change.</summary>
    void RunScatter(Rule rule, Run run, Stopwatch clock)
    {
        var live = run.Live;
        long propStamp = _props.Stamp(rule.Get<PropSpec[]>("props"));
        var results = new OutputJob?[live.Count];
        var faces = new int[live.Count];
        var effective = new Rule?[live.Count];
        var dirty = new List<int>();
        for (int i = 0; i < live.Count; i++)
        {
            if ((effective[i] = Effective(rule, live[i], run)) is not { } eff) continue;
            ulong key = Key(Key(eff.Fingerprint, (ulong)run.Revisions[live[i].Id]), (ulong)propStamp);
            if (_outputs.TryGetValue((rule.Id, live[i].Id), out var job) && job.Key == key) { results[i] = job; faces[i] = job.Faces; }
            else dirty.Add(i);
        }
        Parallel.ForEach(dirty, i =>
        {
            var m = live[i];
            ulong key = Key(Key(effective[i]!.Fingerprint, (ulong)run.Revisions[m.Id]), (ulong)propStamp);
            try
            {
                var mesh = ScatterKind.Build(m, effective[i]!, _props, $"{rule.Id}_{m.Name}", out faces[i], out var why);
                if (mesh.Faces.Count > 0) Generated(mesh, rule.Id, m.Id);
                results[i] = new OutputJob(key, mesh.Faces.Count > 0 ? mesh : null, 0, why == null ? null : PropProblem(rule, why)) { Faces = faces[i] };
            }
            catch (Exception e) { results[i] = new OutputJob(key, null, 0, Failed(rule, m, e)); }
        });
        foreach (var i in dirty) results[i] = _outputs[(rule.Id, live[i].Id)] = results[i]! with { Revision = NextRevision() };
        LastRebuilds += dirty.Count;

        int before = run.Added.Count;
        var reported = new HashSet<string>();
        for (int i = 0; i < live.Count; i++)
        {
            if (results[i] is not { } job) continue;
            if (job.Mesh != null) run.Added.Add((job.Mesh, rule.Id, live[i].Id, job.Revision));
            if (job.Problem != null && reported.Add(job.Problem.Message)) run.Diagnostics.Add(job.Problem);
        }
        int matched = faces.Sum();
        if (matched == 0) run.Diagnostics.Add(NothingMatched(rule, run, null));
        run.Stats.Add(new RuleStats(rule.Id, rule.Kind.Name, Stage.Dress, true, matched, "face", run.Added.Count - before, 0, clock.Elapsed.TotalMilliseconds));
    }

    static Diagnostic PropProblem(Rule rule, string why) =>
        new(Severity.Warning, "W610", why, "check the path: props are relative to the rules file that names them", rule.Pos, $"rules.{rule.Id}", Rule: rule.Id);

    /// <summary>A proxy rule whose mesh selector took no mesh: say which mesh tags exist.</summary>
    Diagnostic NoMeshMatched(Rule rule, Run run)
    {
        var wanted = rule.Get<MeshSelector>("meshes").Tags;
        var tags = run.Live.SelectMany(m => m.Tags).GroupBy(t => t).OrderByDescending(g => g.Count()).Take(8).Select(g => $"{g.Key} ×{g.Count()}").ToList();
        string fix = wanted.Length > 0 && Suggest.Closest(wanted[0], tags.Select(t => t[..t.IndexOf(' ')])) is { } g && g != wanted[0]
            ? $"did you mean the mesh tag \"{g}\"?"
            : $"tag the meshes in object mode (Ctrl+T){(tags.Count > 0 ? $"; mesh tags in the level: {string.Join(", ", tags)}" : "; no mesh has a tag yet")}";
        return new Diagnostic(Severity.Warning, "W402", $"{rule.Id} matched nothing: no mesh has the tags {string.Join(" + ", wanted)}", fix, rule.Pos, $"rules.{rule.Id}", Rule: rule.Id);
    }

    readonly Dictionary<string, JoinIndex> _joins = new();
    readonly Dictionary<string, List<Diagnostic>> _gaps = new();

    /// <summary>
    /// Where sweep segments of one rule meet, kept between passes. Segment ends sit in a hash of tolerance-sized cells;
    /// when a mesh's segments change, only joints within reach of its old and new ends are recomputed. Two ends join
    /// when they are the only two within tolerance of each other, don't fold back, and agree on which way is up.
    /// </summary>
    sealed class JoinIndex(ulong fingerprint)
    {
        public readonly ulong Fingerprint = fingerprint;
        readonly Dictionary<int, List<SweepKind.Segment>> _segs = new();
        readonly Dictionary<(int, int, int), List<(int Mesh, int Seg, bool Start)>> _cells = new();
        readonly Dictionary<int, ((SweepKind.Joint?, SweepKind.Joint?)[] Joints, ulong Hash)> _joints = new();

        /// <summary>Whether the last <see cref="Update"/> changed anything (gap warnings need recounting).</summary>
        public bool Changed { get; private set; }
        public IEnumerable<int> Meshes => _segs.Keys.ToList();

        public bool Holds(int mesh, List<SweepKind.Segment> segs) =>
            _segs.TryGetValue(mesh, out var held) ? ReferenceEquals(held, segs) : segs.Count == 0;

        public ((SweepKind.Joint?, SweepKind.Joint?)[] Joints, ulong Hash) JointsOf(int mesh) => _joints[mesh];

        static (int, int, int) Cell(Vector3 p) =>
            ((int)MathF.Floor(p.X / SweepKind.Tolerance), (int)MathF.Floor(p.Y / SweepKind.Tolerance), (int)MathF.Floor(p.Z / SweepKind.Tolerance));

        static Vector3 End(SweepKind.Segment s, bool start) => start ? s.A : s.B;

        /// <summary>Replaces each listed mesh's segments (an empty list removes it) and recomputes joints near the changes.</summary>
        public void Update(Dictionary<int, List<SweepKind.Segment>> changes)
        {
            Changed = changes.Count > 0;
            if (!Changed) return;
            var touched = new HashSet<(int, int, int)>();
            foreach (var (mesh, list) in changes)
            {
                if (_segs.TryGetValue(mesh, out var old))
                    for (int s = 0; s < old.Count; s++)
                        foreach (var start in (bool[])[true, false])
                        {
                            var c = Cell(End(old[s], start));
                            touched.Add(c);
                            if (_cells.TryGetValue(c, out var l)) { l.Remove((mesh, s, start)); if (l.Count == 0) _cells.Remove(c); }
                        }
                if (list.Count == 0) { _segs.Remove(mesh); _joints.Remove(mesh); continue; }
                _segs[mesh] = list;
                for (int s = 0; s < list.Count; s++)
                    foreach (var start in (bool[])[true, false])
                    {
                        var c = Cell(End(list[s], start));
                        touched.Add(c);
                        (_cells.TryGetValue(c, out var l) ? l : _cells[c] = new()).Add((mesh, s, start));
                    }
            }
            var redo = changes.Keys.Where(_segs.ContainsKey).ToHashSet();
            foreach (var (cx, cy, cz) in touched)
                for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                for (int dz = -1; dz <= 1; dz++)
                    if (_cells.TryGetValue((cx + dx, cy + dy, cz + dz), out var l))
                        foreach (var e in l) redo.Add(e.Mesh);
            foreach (var mesh in redo) _joints[mesh] = Compute(mesh);
        }

        ((SweepKind.Joint?, SweepKind.Joint?)[], ulong) Compute(int mesh)
        {
            var segs = _segs[mesh];
            var result = new (SweepKind.Joint?, SweepKind.Joint?)[segs.Count];
            ulong hash = 17;
            for (int s = 0; s < segs.Count; s++)
            {
                foreach (var start in (bool[])[true, false])
                {
                    var joint = JointAt(mesh, s, start);
                    if (start) result[s].Item1 = joint; else result[s].Item2 = joint;
                    hash = Key(hash, HashJoint(joint));
                }
            }
            return (result, hash);
        }

        SweepKind.Joint? JointAt(int mesh, int seg, bool start)
        {
            var a = _segs[mesh][seg];
            var p = End(a, start);
            var (cx, cy, cz) = Cell(p);
            (int Mesh, int Seg, bool Start)? other = null;
            for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
            for (int dz = -1; dz <= 1; dz++)
                if (_cells.TryGetValue((cx + dx, cy + dy, cz + dz), out var l))
                    foreach (var e in l)
                    {
                        if (e == (mesh, seg, start) || Vector3.DistanceSquared(End(_segs[e.Mesh][e.Seg], e.Start), p) > SweepKind.Tolerance * SweepKind.Tolerance) continue;
                        if (other != null) return null; // three or more ends meet: a branch, leave it open
                        other = e;
                    }
            if (other is not { } o || (o.Mesh == mesh && o.Seg == seg)) return null;
            var b = _segs[o.Mesh][o.Seg];
            var awayA = start ? a.Dir : -a.Dir;
            var awayB = o.Start ? b.Dir : -b.Dir;
            if (Vector3.Dot(awayA, awayB) >= 0.95f || Vector3.Dot(a.Up, b.Up) <= 0.5f) return null;
            return new SweepKind.Joint(awayB, b.Out, b.Up);
        }

        /// <summary>Ends that join nothing, with the mesh they belong to.</summary>
        public IEnumerable<(int Mesh, Vector3 P)> OpenEnds()
        {
            foreach (var (mesh, (joints, _)) in _joints)
                for (int s = 0; s < joints.Length; s++)
                {
                    if (joints[s].Item1 == null) yield return (mesh, _segs[mesh][s].A);
                    if (joints[s].Item2 == null) yield return (mesh, _segs[mesh][s].B);
                }
        }
    }

    /// <summary>Open ends that nearly meet another (within 5 cm, but further than the weld tolerance): likely blockout gaps.</summary>
    static IEnumerable<Diagnostic> Gaps(List<(int Mesh, Vector3 P)> points, Run run)
    {
        const float near = 0.05f;
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
                    var mi = run.ById[points[i].Mesh];
                    var mj = run.ById[points[j].Mesh];
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
        ulong key = (ulong)RuntimeHelpers.GetHashCode(_rules);
        foreach (var m in doc.Meshes) key = Key(key, (ulong)(uint)m.Id);
        if (key == _prunedFor) return; // same meshes and rules: nothing to drop
        _prunedFor = key;
        var ids = doc.Meshes.Select(m => m.Id).ToHashSet();
        var rules = _rules.Rules.Select(r => r.Id).ToHashSet();
        foreach (var k in _shape.Keys.Where(k => !ids.Contains(k.Mesh) || !rules.Contains(k.Rule)).ToList()) _shape.Remove(k);
        foreach (var k in _segments.Keys.Where(k => !ids.Contains(k.Mesh) || !rules.Contains(k.Rule)).ToList()) _segments.Remove(k);
        foreach (var k in _outputs.Keys.Where(k => !ids.Contains(k.Mesh) || !rules.Contains(k.Rule)).ToList()) _outputs.Remove(k);
        foreach (var k in _bounds.Keys.Where(k => !ids.Contains(k)).ToList()) _bounds.Remove(k);
        foreach (var k in _joins.Keys.Where(k => !rules.Contains(k)).ToList()) { _joins.Remove(k); _gaps.Remove(k); }
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
}
