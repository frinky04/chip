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

public enum DressRole { Passthrough, Modified, Added }

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
            if (o.Role == DressRole.Modified && source != null && Collides(source))
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
