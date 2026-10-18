using Chip.Editor.Framework;

namespace Chip.Editor.Features.Shell;

/// <summary>
/// Fuzzy ranking for the command palette. Each query word must appear, in order, as a subsequence of the command's
/// label, "menu label", or id. Matches at the start of the text, at word starts (space, dot, camelCase) and runs of
/// consecutive letters score higher; gaps and long names score lower. Recent commands break ties, and lead when the
/// query is empty.
/// </summary>
public static class PaletteSearch
{
    public static List<Command> Rank(string query, IEnumerable<Command> commands, IReadOnlyList<string> recent)
    {
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        int RecentIndex(Command c) { int i = IndexOf(recent, c.Id); return i < 0 ? int.MaxValue : i; }

        if (words.Length == 0)
            return commands
                .OrderBy(RecentIndex)
                .ThenBy(c => MenuIndex(c.Menu))
                .ThenBy(c => c.Label, StringComparer.OrdinalIgnoreCase)
                .ToList();

        return commands
            .Select(c => (Command: c, Score: Score(words, query.Trim(), c)))
            .Where(x => x.Score != null)
            .OrderByDescending(x => x.Score!.Value + (RecentIndex(x.Command) < int.MaxValue ? 3 : 0))
            .ThenBy(x => RecentIndex(x.Command))
            .ThenBy(x => x.Command.Label, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Command)
            .ToList();
    }

    static int IndexOf(IReadOnlyList<string> list, string id)
    {
        for (int i = 0; i < list.Count; i++) if (list[i] == id) return i;
        return -1;
    }

    static int MenuIndex(string? menu) =>
        menu == null ? CommandRegistry.MenuOrder.Length + 1 : Array.IndexOf(CommandRegistry.MenuOrder, menu) is var i && i >= 0 ? i : CommandRegistry.MenuOrder.Length;

    /// <summary>Total score, or null if some word matches no field.</summary>
    public static int? Score(string[] words, string query, Command c)
    {
        string label = c.Label;
        string withMenu = c.Menu != null ? $"{c.Menu} {label}" : label;
        int total = 0;
        foreach (var w in words)
        {
            int? best = Best(Match(w, label), Match(w, withMenu) - 4, Match(w, c.Id) - 8);
            if (best == null) return null;
            total += best.Value;
        }
        if (label.Equals(query, StringComparison.OrdinalIgnoreCase)) total += 50;
        else if (label.StartsWith(query, StringComparison.OrdinalIgnoreCase)) total += 25;
        return total;
    }

    static int? Best(params int?[] scores)
    {
        int? best = null;
        foreach (var s in scores) if (s != null && (best == null || s > best)) best = s;
        return best;
    }

    /// <summary>
    /// Best subsequence alignment of <paramref name="word"/> in <paramref name="text"/> (case-insensitive), by dynamic
    /// programming so a later word-start match can beat an earlier mid-word one. Null if it isn't a subsequence.
    /// </summary>
    public static int? Match(string word, string text)
    {
        int n = word.Length, m = text.Length;
        if (n == 0) return 0;
        if (n > m) return null;
        const int None = int.MinValue / 2;
        // best[j] = best score with word[..i+1] matched and word[i] at text[j].
        var prev = new int[m];
        var cur = new int[m];
        for (int i = 0; i < n; i++)
        {
            char wc = char.ToLowerInvariant(word[i]);
            int far = None; // max prev[k] for k <= j - 1 - MaxGap: those all pay the capped gap penalty
            for (int j = 0; j < m; j++)
            {
                if (j - 1 - MaxGap >= 0) far = Math.Max(far, prev[j - 1 - MaxGap]);
                cur[j] = None;
                if (char.ToLowerInvariant(text[j]) != wc) continue;
                int bonus = 10 + Boundary(text, j);
                if (i == 0) { cur[j] = bonus - Math.Min(j, 6); continue; }
                int best = far > None ? far - MaxGap : None;
                for (int k = Math.Max(0, j - MaxGap); k < j; k++)
                {
                    if (prev[k] <= None) continue;
                    int gap = j - 1 - k;
                    best = Math.Max(best, prev[k] + (gap == 0 ? 8 : -gap));
                }
                if (best > None) cur[j] = best + bonus;
            }
            (prev, cur) = (cur, prev);
        }
        int result = prev.Max();
        return result <= None ? null : result - m / 4;
    }

    const int MaxGap = 5;

    static int Boundary(string text, int j)
    {
        if (j == 0) return 15;
        char p = text[j - 1], c = text[j];
        if (p is ' ' or '.' or '-' or '_' or '/' or '(') return 10;
        if (char.IsLower(p) && char.IsUpper(c)) return 8;
        return 0;
    }
}
