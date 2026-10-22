using System.Globalization;
using System.Text.RegularExpressions;

namespace Chip.Editor.Features.Dimensions;

/// <summary>What a typed number means: a distance (metres), an angle (degrees) or a scale factor.</summary>
public enum EntryUnit { Length, Angle, Factor }

/// <summary>
/// Parses a value typed during a gizmo drag. One number, or up to three separated by spaces, commas or semicolons (one
/// per gizmo axis). Lengths are metres unless suffixed cm or mm ("150cm", "1.5 m"); angles are degrees ("90", "90°");
/// scale is a factor or a percentage ("1.5", "150%", "x2"). The decimal mark is a point.
/// </summary>
public static partial class NumericEntry
{
    [GeneratedRegex(@"^(?<pre>x|×)?(?<n>[+\-−]?(\d+\.?\d*|\.\d+))(?<unit>mm|cm|m|°|deg|%|x|×)?$", RegexOptions.IgnoreCase)]
    private static partial Regex Token();

    public static float[]? Parse(string text, EntryUnit unit)
    {
        // A unit typed after a space belongs to the number before it: "150 cm".
        var raw = Regex.Split(text.Trim(), @"[\s,;]+").Where(t => t.Length > 0).ToList();
        var tokens = new List<string>();
        foreach (var t in raw)
        {
            if (tokens.Count > 0 && Regex.IsMatch(t, @"^(mm|cm|m|°|deg|%)$", RegexOptions.IgnoreCase)) tokens[^1] += t;
            else tokens.Add(t);
        }
        if (tokens.Count is 0 or > 3) return null;

        var values = new float[tokens.Count];
        for (int i = 0; i < tokens.Count; i++)
        {
            var m = Token().Match(tokens[i]);
            if (!m.Success) return null;
            double n = double.Parse(m.Groups["n"].Value.Replace('−', '-'), CultureInfo.InvariantCulture);
            if (m.Groups["pre"].Success && m.Groups["unit"].Success) return null; // "x2%"
            string suffix = (m.Groups["pre"].Success ? "x" : m.Groups["unit"].Value).ToLowerInvariant().Replace('×', 'x');
            double? v = (unit, suffix) switch
            {
                (EntryUnit.Length, "" or "m") => n,
                (EntryUnit.Length, "cm") => n / 100,
                (EntryUnit.Length, "mm") => n / 1000,
                (EntryUnit.Angle, "" or "°" or "deg") => n,
                (EntryUnit.Factor, "" or "x") => n,
                (EntryUnit.Factor, "%") => n / 100,
                _ => null,
            };
            if (v is not { } value || !double.IsFinite(value)) return null;
            values[i] = (float)value;
        }
        return values;
    }
}
