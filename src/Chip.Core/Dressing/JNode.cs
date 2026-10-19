using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Chip.Core.Dressing;

/// <summary>Where a value came from: file, 1-based line and column.</summary>
public readonly record struct SourcePos(string File, int Line, int Col)
{
    public override string ToString() => $"{File}:{Line}:{Col}";
}

/// <summary>
/// A parsed JSON value that remembers its position, so rule errors can say exactly where they are. Objects keep key
/// order. Comments and trailing commas are allowed (rules files are hand- and agent-written).
/// </summary>
public abstract class JNode
{
    public SourcePos Pos { get; init; }

    /// <summary>Short name of the JSON type for error messages ("a number", "an object").</summary>
    public abstract string TypeName { get; }

    /// <summary>Parses <paramref name="text"/>; on bad JSON returns null and sets <paramref name="error"/> with its position.</summary>
    public static JNode? Parse(string text, string file, out (string Message, SourcePos Pos)? error)
    {
        error = null;
        var bytes = Encoding.UTF8.GetBytes(text);
        var lineStarts = new List<int> { 0 };
        for (int i = 0; i < bytes.Length; i++) if (bytes[i] == '\n') lineStarts.Add(i + 1);
        SourcePos At(long offset)
        {
            int line = lineStarts.BinarySearch((int)offset);
            if (line < 0) line = ~line - 1;
            int col = Encoding.UTF8.GetCharCount(bytes, lineStarts[line], (int)offset - lineStarts[line]) + 1;
            return new SourcePos(file, line + 1, col);
        }

        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        try
        {
            if (!reader.Read()) { error = ("the file is empty", new SourcePos(file, 1, 1)); return null; }
            var node = Read(ref reader, At);
            if (reader.Read()) { error = ("unexpected text after the end of the JSON", At(reader.TokenStartIndex)); return null; }
            return node;
        }
        catch (JsonException e)
        {
            error = (CleanMessage(e.Message), new SourcePos(file, (int)(e.LineNumber ?? 0) + 1, (int)(e.BytePositionInLine ?? 0) + 1));
            return null;
        }
    }

    static string CleanMessage(string m)
    {
        int cut = m.IndexOf(" LineNumber:", StringComparison.Ordinal);
        return (cut > 0 ? m[..cut] : m).TrimEnd('.', ' ');
    }

    static JNode Read(ref Utf8JsonReader r, Func<long, SourcePos> at)
    {
        var pos = at(r.TokenStartIndex);
        switch (r.TokenType)
        {
            case JsonTokenType.StartObject:
            {
                var obj = new JObject { Pos = pos };
                while (r.Read() && r.TokenType != JsonTokenType.EndObject)
                {
                    var keyPos = at(r.TokenStartIndex);
                    string key = r.GetString()!;
                    r.Read();
                    obj.Add(key, keyPos, Read(ref r, at));
                }
                return obj;
            }
            case JsonTokenType.StartArray:
            {
                var arr = new JArray { Pos = pos };
                while (r.Read() && r.TokenType != JsonTokenType.EndArray) arr.Items.Add(Read(ref r, at));
                return arr;
            }
            case JsonTokenType.String: return new JString(r.GetString()!) { Pos = pos };
            case JsonTokenType.Number: return new JNumber(r.GetDouble()) { Pos = pos };
            case JsonTokenType.True: return new JBool(true) { Pos = pos };
            case JsonTokenType.False: return new JBool(false) { Pos = pos };
            default: return new JNull { Pos = pos };
        }
    }
}

public sealed class JObject : JNode
{
    readonly List<(string Key, SourcePos KeyPos, JNode Value)> _entries = new();
    public override string TypeName => "an object";
    public IReadOnlyList<(string Key, SourcePos KeyPos, JNode Value)> Entries => _entries;

    /// <summary>Adds or replaces (a later duplicate key wins, as in most JSON readers).</summary>
    public void Add(string key, SourcePos keyPos, JNode value)
    {
        int i = _entries.FindIndex(e => e.Key == key);
        if (i >= 0) _entries[i] = (key, keyPos, value);
        else _entries.Add((key, keyPos, value));
    }

    public JNode? this[string key] => _entries.Find(e => e.Key == key).Value;
    public bool Has(string key) => _entries.Exists(e => e.Key == key);
    public SourcePos KeyPos(string key) => _entries.Find(e => e.Key == key) is { Value: not null } e ? e.KeyPos : Pos;
    public IEnumerable<string> Keys => _entries.Select(e => e.Key);

    /// <summary>A copy with <paramref name="patch"/>'s keys laid over this one's; nested objects merge, everything else is replaced.</summary>
    public JObject MergedWith(JObject patch)
    {
        var result = new JObject { Pos = Pos };
        foreach (var (k, kp, v) in _entries) result.Add(k, kp, v);
        foreach (var (k, kp, v) in patch._entries)
            result.Add(k, kp, result[k] is JObject a && v is JObject b ? a.MergedWith(b) : v);
        return result;
    }
}

public sealed class JArray : JNode
{
    public List<JNode> Items { get; } = new();
    public override string TypeName => "a list";
}

public sealed class JString(string value) : JNode
{
    public string Value { get; } = value;
    public override string TypeName => "text";
}

public sealed class JNumber(double value) : JNode
{
    public double Value { get; } = value;
    public override string TypeName => "a number";
    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}

public sealed class JBool(bool value) : JNode
{
    public bool Value { get; } = value;
    public override string TypeName => Value ? "true" : "false";
}

public sealed class JNull : JNode
{
    public override string TypeName => "null";
}
