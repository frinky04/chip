using System.Runtime.CompilerServices;
using System.Text.Json;
using Chip.Core.Scene;
using Chip.Editor.Framework;

namespace Chip.Editor.Features.Logic;

/// <summary>
/// One connection as a table row: connection <see cref="Index"/> of <see cref="Source"/>, with the text of each column
/// and what's wrong with it. Built once per edit (<see cref="IoRows"/>), so tables of a whole level stay cheap to draw.
/// </summary>
public sealed record IoRow(Entity Source, int Index, Connection Connection, string SourceText, string Output, string Target,
    string Input, string Value, IReadOnlyList<Entity> Targets, IReadOnlyList<LevelProblem> Problems)
{
    public bool HasError => Problems.Any(p => p.Severity == Severity.Error);
    public bool HasWarning => Problems.Count > 0 && !HasError;

    /// <summary>The problems, one per line, without the entity name each message starts with.</summary>
    public string ProblemText
    {
        get
        {
            string lead = (Source.Name.Length > 0 ? Source.Name : $"{Source.ClassName} #{Source.Id}") + ": ";
            return string.Join("\n", Problems.Select(p => p.Message.StartsWith(lead, StringComparison.Ordinal) ? p.Message[lead.Length..] : p.Message));
        }
    }

    /// <summary>"0.5 s", or empty for no delay.</summary>
    public string Delay => Connection.Delay == 0 ? "" : $"{Connection.Delay:0.##} s";

    /// <summary>"once", "if power_on", "once, if count ≥ 3": the connection's limits in a few words.</summary>
    public string Limits
    {
        get
        {
            var parts = new List<string>();
            if (Connection.Once) parts.Add("once");
            if (Connection.When is { } w)
                parts.Add(w.AtLeast is { } n ? $"if {w.Flag} ≥ {n}"
                    : w.Is is { ValueKind: JsonValueKind.False } ? $"if not {w.Flag}"
                    : w.Is is { ValueKind: JsonValueKind.True } ? $"if {w.Flag}"
                    : $"if {w.Flag} = {(w.Is is { } v ? EntityValues.Text(v) : "?")}");
            return string.Join(", ", parts);
        }
    }

    /// <summary>Whether <paramref name="filter"/> appears in any column (case-insensitive); empty matches everything.</summary>
    public bool Matches(string filter) => filter.Length == 0
        || new[] { SourceText, Output, Target, Input, Value, Limits }.Any(s => s.Contains(filter, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Every connection in the level as rows, and each entity's outgoing and incoming ones, rebuilt when the session changes.</summary>
public static class IoRows
{
    sealed record Index(IReadOnlyList<IoRow> All, ILookup<int, IoRow> From, ILookup<int, IoRow> To, EntityCatalog Catalog);

    static readonly ConditionalWeakTable<EditorContext, Rendering.FrameCache<Index>> Cache = new();

    public static IReadOnlyList<IoRow> All(EditorContext ctx) => Get(ctx).All;

    /// <summary>The entity's own connections, in their order.</summary>
    public static IReadOnlyList<IoRow> Outgoing(EditorContext ctx, int entity) => Get(ctx).From[entity].ToList();

    /// <summary>Connections from anywhere whose target is the entity now: by id, through one of its tags, or <c>self</c>.</summary>
    public static IReadOnlyList<IoRow> Incoming(EditorContext ctx, int entity) => Get(ctx).To[entity].ToList();

    public static IoRow? Find(EditorContext ctx, int entity, int index) => Get(ctx).From[entity].FirstOrDefault(r => r.Index == index);

    static Index Get(EditorContext ctx)
    {
        var cache = Cache.GetValue(ctx, _ => new Rendering.FrameCache<Index>(selection: false));
        // The catalog reloads when the game's manifest changes: labels and problems follow it as well as the level's edits.
        if (cache.IsCurrent(ctx.Session) && cache.Value.Catalog == EntityClasses.Current) return cache.Value;
        var rows = new List<IoRow>();
        foreach (var e in ctx.Doc.Entities)
            for (int i = 0; i < e.Outputs.Count; i++) rows.Add(Row(ctx, e, i));
        var to = rows.SelectMany(r => r.Targets.Select(t => (t.Id, Row: r))).ToLookup(p => p.Id, p => p.Row);
        var index = new Index(rows, rows.ToLookup(r => r.Source.Id), to, EntityClasses.Current);
        cache.Store(ctx.Session, index);
        return index;
    }

    static IoRow Row(EditorContext ctx, Entity e, int i)
    {
        var c = e.Outputs[i];
        var cls = EntityClasses.Get(e.ClassName);
        var inputs = ConnectionEditor.Inputs(ctx, e, c.Target);
        var input = inputs.FirstOrDefault(x => x.Name == c.Do);
        var problems = LogicOps.CheckConnection(ctx.Doc, EntityClasses.Current, EntityClasses.Project, e, c);
        return new IoRow(e, i, c,
            SourceText: SceneOps.DisplayName(e),
            Output: cls.Output(c.On)?.Label ?? EntityClass.Humanize(c.On),
            Target: TargetCell(ctx.Doc, c),
            Input: input?.Label ?? (c.Do.Length > 0 ? EntityClass.Humanize(c.Do) : "—"),
            Value: ValueText(ctx.Doc, c, input),
            Targets: LogicOps.TargetsOf(ctx.Doc, e, c.Target),
            Problems: problems);
    }

    /// <summary>The target as short as a table cell needs: a name, <c>#tag</c>, <c>flag:name</c>, or <c>self</c> / <c>activator</c> / <c>caller</c>.</summary>
    static string TargetCell(Document doc, Connection c)
    {
        if (c.UnknownTarget != null) return "unknown";
        var t = c.Target;
        return t.Kind switch
        {
            TargetKind.Entity => doc.FindEntity(t.Entity) is { } o ? SceneOps.DisplayName(o) : $"missing #{t.Entity}",
            TargetKind.Tag => "#" + t.Name,
            TargetKind.Flag => "flag:" + t.Name,
            _ => t.Name,
        };
    }

    /// <summary>The arguments in a few words: one value alone, several as <c>name: value</c>; a payload binding as "← value".</summary>
    static string ValueText(Document doc, Connection c, InputSpec? input)
    {
        var args = c.Args.Select(a => (Name: input?.Params.FirstOrDefault(p => p.Name == a.Key)?.Label ?? a.Key, Text: ArgText(doc, a.Value))).ToList();
        if (args.Count == 0) return "";
        if (args.Count == 1 && input?.Params.Count == 1) return args[0].Text;
        return string.Join(", ", args.Select(a => $"{a.Name}: {a.Text}"));
    }

    static string ArgText(Document doc, JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.Object when v.TryGetProperty("payload", out var b) && b.ValueKind == JsonValueKind.True => "← value",
        JsonValueKind.Object when EntityValues.RefId(v) is { } id => doc.FindEntity(id) is { } e ? SceneOps.DisplayName(e) : $"missing #{id}",
        JsonValueKind.String when v.GetString() is { Length: 0 } => "\"\"",
        JsonValueKind.Array => v.GetArrayLength() == 0 ? "none" : string.Join(", ", v.EnumerateArray().Select(item => ArgText(doc, item))),
        _ => EntityValues.Text(v),
    };
}
