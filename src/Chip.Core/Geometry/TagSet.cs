using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Chip.Core.Geometry;

/// <summary>
/// An immutable, sorted set of tag names on a face or mesh (<c>wall</c>, <c>floor</c>, <c>railing</c>). Dressing rules select
/// by them, so they travel with the surface through every mesh op the way the material does. <c>default</c> is the empty
/// set and allocates nothing.
/// <para>
/// Names are lowercase letters, digits, <c>-</c> and <c>_</c>, starting with a letter or digit, at most
/// <see cref="MaxLength"/> characters. <c>skip:&lt;rule&gt;</c> is the one reserved form: it opts a face out of that
/// dressing rule. Use <see cref="Normalize"/> on typed input; everything else rejects invalid names.
/// </para>
/// </summary>
[CollectionBuilder(typeof(TagSet), nameof(Create))] // ["wall", "floor"] builds a set (Add returns a new set, so the default pattern can't)
public readonly struct TagSet : IEquatable<TagSet>, IReadOnlyCollection<string>
{
    public static TagSet Create(ReadOnlySpan<string> tags) => Of(tags.ToArray());

    public const int MaxLength = 64;
    public const string SkipPrefix = "skip:";

    readonly string[]? _tags; // sorted (ordinal), distinct, interned; null when empty

    TagSet(string[] tags) => _tags = tags.Length == 0 ? null : tags;

    public static TagSet Empty => default;

    public int Count => _tags?.Length ?? 0;
    public bool IsEmpty => _tags == null;

    public bool Contains(string tag) => _tags != null && Array.BinarySearch(_tags, tag, StringComparer.Ordinal) >= 0;

    /// <summary>The set plus <paramref name="tag"/> (this set if it is already there).</summary>
    public TagSet Add(string tag)
    {
        Require(tag);
        if (Contains(tag)) return this;
        var list = new List<string>(Count + 1);
        if (_tags != null) list.AddRange(_tags);
        list.Add(string.Intern(tag));
        list.Sort(StringComparer.Ordinal);
        return new TagSet(list.ToArray());
    }

    /// <summary>The set without <paramref name="tag"/> (this set if it isn't there).</summary>
    public TagSet Remove(string tag) =>
        Contains(tag) ? new TagSet(_tags!.Where(t => t != tag).ToArray()) : this;

    /// <summary>A set of valid names (duplicates collapse). Throws <see cref="ArgumentException"/> naming the first bad one.</summary>
    public static TagSet Of(IEnumerable<string> tags)
    {
        var distinct = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var t in tags)
        {
            Require(t);
            distinct.Add(string.Intern(t));
        }
        return new TagSet(distinct.ToArray());
    }

    public static TagSet Of(params string[] tags) => Of((IEnumerable<string>)tags);

    /// <summary>True for a valid tag name; otherwise <paramref name="why"/> says what's wrong, in words a user can act on.</summary>
    public static bool IsValid(string? tag, [NotNullWhen(false)] out string? why)
    {
        why = null;
        if (string.IsNullOrEmpty(tag)) { why = "a tag can't be empty"; return false; }
        if (tag.Length > MaxLength) { why = $"a tag can be at most {MaxLength} characters"; return false; }
        var name = tag.StartsWith(SkipPrefix, StringComparison.Ordinal) ? tag[SkipPrefix.Length..] : tag;
        if (name.Length == 0) { why = $"\"{SkipPrefix}\" needs a rule id after it"; return false; }
        if (!IsLowerAlnum(name[0])) { why = "a tag starts with a lowercase letter or digit"; return false; }
        foreach (var c in name)
            if (!IsLowerAlnum(c) && c != '-' && c != '_')
            {
                why = c == ':' ? $"only \"{SkipPrefix}\" may contain a colon" : $"'{c}' isn't allowed: use lowercase letters, digits, - and _";
                return false;
            }
        return true;
    }

    /// <summary>
    /// Typed input as a tag: trimmed, lowercased, inner spaces become <c>-</c> ("Wall Trim" → "wall-trim").
    /// Returns null when it still isn't valid (see <see cref="IsValid"/> for why).
    /// </summary>
    public static string? Normalize(string? input)
    {
        if (input == null) return null;
        var parts = input.Trim().ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var tag = string.Join('-', parts);
        return IsValid(tag, out _) ? tag : null;
    }

    static bool IsLowerAlnum(char c) => c is >= 'a' and <= 'z' or >= '0' and <= '9';

    static void Require(string tag)
    {
        if (!IsValid(tag, out var why)) throw new ArgumentException($"Invalid tag \"{tag}\": {why}", nameof(tag));
    }

    public bool Equals(TagSet other) =>
        ReferenceEquals(_tags, other._tags) || (_tags ?? []).AsSpan().SequenceEqual(other._tags ?? []);

    public override bool Equals(object? obj) => obj is TagSet t && Equals(t);

    public override int GetHashCode()
    {
        var h = new HashCode();
        if (_tags != null) foreach (var t in _tags) h.Add(t, StringComparer.Ordinal);
        return h.ToHashCode();
    }

    public static bool operator ==(TagSet a, TagSet b) => a.Equals(b);
    public static bool operator !=(TagSet a, TagSet b) => !a.Equals(b);

    public IEnumerator<string> GetEnumerator() => ((IEnumerable<string>)(_tags ?? [])).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public override string ToString() => string.Join(", ", (IEnumerable<string>)(_tags ?? []));
}
