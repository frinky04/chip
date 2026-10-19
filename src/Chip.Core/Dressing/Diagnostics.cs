namespace Chip.Core.Dressing;

public enum Severity { Warning, Error }

/// <summary>
/// One problem found while loading rules or dressing the level. Every diagnostic says where (file position and JSON path,
/// or the mesh it's about), what happened, and how to fix it, so a person or an agent can act on it without guessing.
/// <para>Codes: E1xx file and JSON, E2xx values, E3xx references (vars, imports, rules), W3xx–W5xx dressing results,
/// E9xx a rule failed while running.</para>
/// </summary>
public sealed record Diagnostic(Severity Severity, string Code, string Message, string? Fix = null,
    SourcePos? Pos = null, string? Path = null, int? MeshId = null, string? Rule = null)
{
    public bool IsError => Severity == Severity.Error;

    public override string ToString()
    {
        var head = $"{(IsError ? "ERROR" : "WARN ")} {Code}";
        if (Pos is { } p) head += $" {p}";
        if (Path != null) head += $"  {Path}";
        if (MeshId is { } m) head += $"  mesh {m}";
        var text = $"{head}\n      {Message}";
        if (Fix != null) text += $"\n      fix: {Fix}";
        return text;
    }
}

/// <summary>"Did you mean" for misspelt keys and names: the closest candidate within a small edit distance.</summary>
static class Suggest
{
    public static string? Closest(string typed, IEnumerable<string> candidates)
    {
        string? best = null;
        int bestDistance = int.MaxValue;
        foreach (var c in candidates)
        {
            int d = Distance(typed.ToLowerInvariant(), c.ToLowerInvariant());
            if (d < bestDistance) { bestDistance = d; best = c; }
        }
        return best != null && bestDistance <= Math.Max(2, typed.Length / 3) ? best : null;
    }

    static int Distance(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }
}
