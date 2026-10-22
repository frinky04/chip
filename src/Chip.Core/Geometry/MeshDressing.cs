using System.Collections.Immutable;
using System.Text.Json.Nodes;

namespace Chip.Core.Geometry;

/// <summary>
/// How one mesh departs from the level's dressing rules (docs/dressing.md): per-rule parameter overrides (this railing's
/// balusters are 10 cm apart) and rules switched off for it (<c>*</c> switches every rule off, as Bake does). Immutable
/// with value equality, so undo and change tracking see edits; <c>default</c>-like <see cref="Empty"/> when unused.
/// </summary>
public sealed class MeshDressing : IEquatable<MeshDressing>
{
    public static readonly MeshDressing Empty = new(ImmutableSortedDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal),
        ImmutableSortedSet<string>.Empty.WithComparer(StringComparer.Ordinal));

    /// <summary>Every rule.</summary>
    public const string AllRules = "*";

    MeshDressing(ImmutableSortedDictionary<string, string> set, ImmutableSortedSet<string> skip)
    {
        Set = set;
        Skip = skip;
    }

    /// <summary>Rule id → overriding parameters as compact JSON object text (<c>{"balusterEvery":0.1}</c>).</summary>
    public ImmutableSortedDictionary<string, string> Set { get; }

    /// <summary>Rule ids switched off for this mesh.</summary>
    public ImmutableSortedSet<string> Skip { get; }

    public bool IsEmpty => Set.IsEmpty && Skip.IsEmpty;

    public bool Skips(string rule) => Skip.Contains(rule) || Skip.Contains(AllRules);

    /// <summary>The overriding JSON for <paramref name="rule"/>, or null.</summary>
    public string? OverridesFor(string rule) => Set.TryGetValue(rule, out var json) ? json : null;

    public static MeshDressing Of(IEnumerable<KeyValuePair<string, string>> set, IEnumerable<string> skip) =>
        new(Empty.Set.AddRange(set.Where(kv => kv.Value != "{}").Select(kv => KeyValuePair.Create(kv.Key, Compact(kv.Value)))), Empty.Skip.Union(skip));

    /// <summary>With <paramref name="param"/> of <paramref name="rule"/> set to <paramref name="value"/> (null removes it).</summary>
    public MeshDressing WithParam(string rule, string param, JsonNode? value)
    {
        var obj = Set.TryGetValue(rule, out var json) ? JsonNode.Parse(json)!.AsObject() : new JsonObject();
        if (value == null) obj.Remove(param);
        else obj[param] = value.DeepClone();
        var text = obj.ToJsonString();
        return new(obj.Count == 0 ? Set.Remove(rule) : Set.SetItem(rule, text), Skip);
    }

    /// <summary>With the rule's parameter overrides cleared.</summary>
    public MeshDressing WithoutOverrides(string rule) => new(Set.Remove(rule), Skip);

    /// <summary>With <paramref name="rule"/> switched off (<paramref name="skip"/>) or back on.</summary>
    public MeshDressing WithSkip(string rule, bool skip) => new(Set, skip ? Skip.Add(rule) : Skip.Remove(rule));

    /// <summary>With every mention of rule <paramref name="from"/> moved to <paramref name="to"/> (null drops them).</summary>
    public MeshDressing Renamed(string from, string? to)
    {
        var set = Set;
        if (set.TryGetValue(from, out var json))
        {
            set = set.Remove(from);
            if (to != null) set = set.SetItem(to, json);
        }
        var skip = Skip;
        if (skip.Contains(from))
        {
            skip = skip.Remove(from);
            if (to != null) skip = skip.Add(to);
        }
        return ReferenceEquals(set, Set) && ReferenceEquals(skip, Skip) ? this : new(set, skip);
    }

    /// <summary>Whether the mesh's overrides or switches mention <paramref name="rule"/>.</summary>
    public bool Mentions(string rule) => Set.ContainsKey(rule) || Skip.Contains(rule);

    static string Compact(string json) => JsonNode.Parse(json)?.ToJsonString() ?? "{}";

    public bool Equals(MeshDressing? other) =>
        other != null && Skip.SetEquals(other.Skip) && Set.Count == other.Set.Count &&
        Set.All(kv => other.Set.TryGetValue(kv.Key, out var v) && v == kv.Value);

    public override bool Equals(object? obj) => obj is MeshDressing d && Equals(d);

    public override int GetHashCode()
    {
        var h = new HashCode();
        foreach (var (k, v) in Set) { h.Add(k); h.Add(v); }
        foreach (var s in Skip) h.Add(s);
        return h.ToHashCode();
    }
}
