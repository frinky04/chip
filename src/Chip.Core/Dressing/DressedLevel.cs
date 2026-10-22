using Chip.Core.Geometry;
using Chip.Core.Scene;

namespace Chip.Core.Dressing;

/// <summary>
/// Meshes with change tokens: what the renderer (and later picking) draws from. Two adapters: the blockout
/// (<see cref="Blockout"/>) and the dressed level (<see cref="DressedLevel"/>).
/// </summary>
public interface IMeshSet
{
    IReadOnlyList<PolyMesh> Meshes { get; }

    /// <summary>Changes whenever that mesh's content does and never repeats for a mesh id; -1 if it isn't in the set.</summary>
    int Revision(PolyMesh mesh);
}

/// <summary>The blockout as an <see cref="IMeshSet"/>: the document's meshes with the session's revisions.</summary>
public sealed class Blockout(Session session) : IMeshSet
{
    public IReadOnlyList<PolyMesh> Meshes => session.Doc.Meshes;
    public int Revision(PolyMesh mesh) => session.MeshRevision(mesh);
}

/// <summary>
/// Passthrough: the blockout mesh as is. Modified: a changed copy replaces it (bevel). Proxy: something built from it
/// replaces it (railing, stairs). Added: extra geometry a dress rule made from it.
/// </summary>
public enum DressRole { Passthrough, Modified, Proxy, Added }

/// <summary>Which blockout mesh an output came from, and the rule that made it (null for an unchanged blockout mesh).</summary>
public readonly record struct MeshOrigin(int SourceId, string? Rule, DressRole Role);

/// <summary>What one rule did in a dressing pass. <see cref="TargetNoun"/> says what <see cref="Targets"/> counts.</summary>
public sealed record RuleStats(string Rule, string Kind, Stage Stage, bool Enabled, int Targets, string TargetNoun, int Outputs, int Skipped, double Ms);

/// <summary>
/// The level as shipped: the blockout with shape rules applied (modified copies replace their sources) followed by
/// everything dress rules added. Blockout meshes pass through as the same objects with their own revisions; generated
/// meshes have negative ids, stable for each (rule, source) while the <see cref="Dresser"/> lives, and revisions that change
/// only when they're rebuilt, so per-mesh caches keyed on id and revision just work. Generated meshes are locked and
/// never collide; collision for export comes from the blockout (<see cref="ForExport"/>).
/// </summary>
public sealed class DressedLevel : IMeshSet
{
    readonly Dictionary<PolyMesh, (MeshOrigin Origin, int Revision)> _info;
    readonly Func<PolyMesh, int> _blockoutRevision;
    readonly Document _doc;
    string? _contentHash;

    internal DressedLevel(Document doc, RuleSet rules, List<PolyMesh> meshes, Dictionary<PolyMesh, (MeshOrigin, int)> info,
        Func<PolyMesh, int> blockoutRevision, List<Diagnostic> diagnostics, List<RuleStats> stats)
    {
        _doc = doc; Rules = rules; Meshes = meshes; _info = info; _blockoutRevision = blockoutRevision; Diagnostics = diagnostics; Stats = stats;
    }

    public RuleSet Rules { get; }
    public IReadOnlyList<PolyMesh> Meshes { get; }
    /// <summary>Rule file problems first, then what came up while dressing.</summary>
    public IReadOnlyList<Diagnostic> Diagnostics { get; }
    public IReadOnlyList<RuleStats> Stats { get; }
    public int ErrorCount => Diagnostics.Count(d => d.IsError);
    public int WarningCount => Diagnostics.Count(d => !d.IsError);

    public int Revision(PolyMesh mesh) =>
        _info.TryGetValue(mesh, out var i) ? (i.Origin.Role == DressRole.Passthrough ? _blockoutRevision(mesh) : i.Revision) : -1;

    public MeshOrigin Origin(PolyMesh mesh) => _info.TryGetValue(mesh, out var i) ? i.Origin : new MeshOrigin(mesh.Id, null, DressRole.Passthrough);

    /// <summary>Rules that changed or added to blockout mesh <paramref name="meshId"/>, in rule order.</summary>
    public IReadOnlyList<(string Rule, DressRole Role)> RulesFor(int meshId) =>
        _info.Values.Where(i => i.Origin.SourceId == meshId && i.Origin.Rule != null)
            .Select(i => (i.Origin.Rule!, i.Origin.Role)).Distinct()
            .OrderBy(r => Rules.Rules.ToList().FindIndex(x => x.Id == r.Item1)).ToList();

    /// <summary>Blockout meshes a proxy rule replaced, with the rule and its kind (the blockout view marks them).</summary>
    public IReadOnlyList<(int SourceId, string Rule, string Kind)> Proxies =>
        _proxies ??= _info.Values.Where(i => i.Origin.Role == DressRole.Proxy)
            .Select(i => (i.Origin.SourceId, i.Origin.Rule!, Rules.Rules.First(r => r.Id == i.Origin.Rule).Kind.Name)).ToList();
    List<(int, string, string)>? _proxies;

    /// <summary>
    /// The enabled rules whose selector takes <paramref name="mesh"/> (its faces, or the mesh for proxies), with whether the
    /// mesh switched each off. For the inspector: what can be overridden here. Computed on demand.
    /// </summary>
    public IReadOnlyList<(Rule Rule, bool Skipped)> Applicable(PolyMesh mesh)
    {
        var result = new List<(Rule, bool)>();
        foreach (var rule in Rules.Rules.Where(r => r.Enabled))
        {
            bool takes = rule.Find<MeshSelector>("meshes") is { } ms ? ms.Matches(mesh)
                : rule.Find<FaceSelector>("faces") is { } fs && Enumerable.Range(0, mesh.Faces.Count).Any(f => fs.Matches(mesh, f, mesh.FaceNormal(f)));
            if (takes) result.Add((rule, mesh.Dressing.Skips(rule.Id)));
        }
        return result;
    }

    /// <summary>
    /// What rule <paramref name="ruleId"/> takes from the blockout: (mesh id, face index) for face rules, (mesh id, -1) for
    /// whole meshes (proxies). Visible meshes only, at most <paramref name="limit"/> entries. Computed on demand and cached.
    /// </summary>
    public IReadOnlyList<(int Mesh, int Face)> Targets(string ruleId, int limit = 200_000)
    {
        _targets ??= new();
        if (_targets.TryGetValue(ruleId, out var hit)) return hit;
        var result = new List<(int, int)>();
        if (Rules.Rules.FirstOrDefault(r => r.Id == ruleId) is { } rule)
        {
            string skip = rule.SkipTag;
            foreach (var m in _doc.Meshes)
            {
                if (m.Hidden || m.Dressing.Skips(rule.Id)) continue;
                if (rule.Find<MeshSelector>("meshes") is { } ms) { if (ms.Matches(m)) result.Add((m.Id, -1)); }
                else if (rule.Find<FaceSelector>("faces") is { } fs)
                    for (int f = 0; f < m.Faces.Count && result.Count < limit; f++)
                        if (!m.Faces[f].Tags.Contains(skip) && fs.Matches(m, f, m.FaceNormal(f))) result.Add((m.Id, f));
                if (result.Count >= limit) break;
            }
        }
        return _targets[ruleId] = result;
    }
    Dictionary<string, IReadOnlyList<(int, int)>>? _targets;

    /// <summary>
    /// For one face (or, with <paramref name="face"/> -1, the mesh itself), why each rule does or doesn't take it, in rule
    /// order: null when it does, else a short reason with a hint ("needs the tag wall (it has walls)"). Rules that pick
    /// meshes answer for the mesh. For the inspector's "Why isn't this dressed?".
    /// </summary>
    public IReadOnlyList<(Rule Rule, string? Reason)> Explain(PolyMesh mesh, int face)
    {
        var result = new List<(Rule, string?)>();
        foreach (var rule in Rules.Rules)
        {
            string? reason;
            if (!rule.Enabled) reason = "switched off in the rules";
            else if (mesh.Dressing.Skips(rule.Id)) reason = "switched off for this mesh (inspector)";
            else if (rule.Find<MeshSelector>("meshes") is { } ms) reason = Missing(ms.Tags, ms.NotTags, mesh.Tags, "mesh tag");
            else if (rule.Find<FaceSelector>("faces") is { } fs)
            {
                var faces = face >= 0 && face < mesh.Faces.Count ? [face] : Enumerable.Range(0, mesh.Faces.Count);
                reason = null;
                foreach (var f in faces)
                {
                    reason = mesh.Faces[f].Tags.Contains(rule.SkipTag) ? $"the face opts out ({rule.SkipTag})" : WhyNot(fs, mesh, f);
                    if (reason == null) break;
                }
            }
            else reason = "it picks nothing";
            result.Add((rule, reason));
        }
        return result;
    }

    static string? WhyNot(FaceSelector s, PolyMesh m, int f)
    {
        var face = m.Faces[f];
        if (Missing(s.Tags, s.NotTags, face.Tags, "tag") is { } tags) return tags;
        if (s.Materials.Length > 0 && !s.Materials.Any(g => FaceSelector.Glob(g, face.Material)))
            return $"needs material {string.Join(" or ", s.Materials)} (it has {face.Material})";
        if (s.Facing != null && !s.Matches(m, f, m.FaceNormal(f)))
        {
            float y = m.FaceNormal(f).Y;
            string facing = y > 0.7f ? "up" : y < -0.7f ? "down" : MathF.Abs(y) < 0.3f ? "upright" : "at a slant";
            return $"needs faces facing {s.Facing} (it faces {facing})";
        }
        return null;
    }

    static string? Missing(string[] need, string[] avoid, TagSet have, string noun)
    {
        foreach (var t in need)
            if (!have.Contains(t))
            {
                var near = Suggest.Closest(t, have.Where(h => !h.StartsWith(TagSet.SkipPrefix, StringComparison.Ordinal)));
                return $"needs the {noun} {t}" + (near != null ? $" (it has {near})" : "");
            }
        foreach (var t in avoid)
            if (have.Contains(t)) return $"it has the {noun} {t}, which the rule leaves out";
        return null;
    }

    /// <summary>A stable hash of everything dressing generated: equal across runs and machines for the same level and rules.</summary>
    public string ContentHash => _contentHash ??= ComputeContentHash();

    string ComputeContentHash()
    {
        ulong h = 14695981039346656037UL;
        void Add(ulong x) { h ^= x; h *= 1099511628211UL; }
        void AddText(string s) { foreach (var c in s) Add(c); Add(0xFFFF); }
        foreach (var m in Meshes)
        {
            var o = Origin(m);
            if (o.Role == DressRole.Passthrough) continue;
            AddText(o.Rule!);
            Add((ulong)o.SourceId);
            foreach (var p in m.Positions) { Add(BitConverter.SingleToUInt32Bits(p.X)); Add(BitConverter.SingleToUInt32Bits(p.Y)); Add(BitConverter.SingleToUInt32Bits(p.Z)); }
            foreach (var f in m.Faces) { foreach (var v in f.Verts) Add((ulong)v); AddText(f.Material); }
        }
        return h.ToString("x16")[..8];
    }

    /// <summary>
    /// A document to export: the dressed meshes, plus a collision-only stand-in for each blockout mesh a shape rule replaced
    /// (so collision stays the simple blockout shape). Generated meshes get fresh positive ids and names "rule_source".
    /// Entities, groups and export settings are shared with the source document, not copied.
    /// </summary>
    public Document ForExport()
    {
        var doc = new Document
        {
            ExportScale = _doc.ExportScale, ExportPath = _doc.ExportPath, ExportCollision = _doc.ExportCollision, Export = _doc.Export,
            TexelDensity = _doc.TexelDensity, Entities = _doc.Entities, Groups = _doc.Groups, NextId = _doc.NextId,
        };
        foreach (var m in Meshes)
        {
            var o = Origin(m);
            if (o.Role == DressRole.Passthrough) { doc.Meshes.Add(m); continue; }
            var source = _doc.Find(o.SourceId);
            var copy = m.Clone();
            copy.Id = doc.NextId++;
            copy.Name = $"{o.Rule}_{source?.Name ?? "mesh"}";
            copy.Collision = CollisionMode.None;
            copy.Locked = false;
            doc.Meshes.Add(copy);
            if (o.Role is DressRole.Modified or DressRole.Proxy && source != null && Collides(source))
            {
                var standIn = source.Clone();
                standIn.Id = doc.NextId++;
                standIn.Name = $"{source.Name}_collision";
                standIn.Collision = CollisionMode.Only;
                doc.Meshes.Add(standIn);
            }
        }
        return doc;
    }

    bool Collides(PolyMesh m) => m.Collision switch
    {
        CollisionMode.None => false,
        CollisionMode.Default => _doc.ExportCollision,
        _ => true,
    };
}
