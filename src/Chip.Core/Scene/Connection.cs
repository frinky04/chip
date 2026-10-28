using System.Text.Json;
using System.Text.Json.Nodes;

namespace Chip.Core.Scene;

/// <summary>A command an entity class takes (manifest <c>inputs</c>): what a connection makes its target do.</summary>
public sealed record InputSpec
{
    public required string Name { get; init; }
    public string Doc { get; init; } = "";
    /// <summary>Old names that levels may still store (<see cref="LevelMigration"/>).</summary>
    public IReadOnlyList<string> Was { get; init; } = [];
    /// <summary>The arguments, each shaped like a property.</summary>
    public IReadOnlyList<PropSpec> Params { get; init; } = [];
    public string Label => EntityClass.Humanize(Name);
}

/// <summary>An event an entity class raises (manifest <c>outputs</c>), which fires the entity's connections on it.</summary>
public sealed record OutputSpec
{
    public required string Name { get; init; }
    public string Doc { get; init; } = "";
    public IReadOnlyList<string> Was { get; init; } = [];
    /// <summary>The value the event carries (named <c>payload</c>), which an argument can take; null for none.</summary>
    public PropSpec? Payload { get; init; }
    /// <summary>The game expects it connected: <see cref="LogicOps.Check"/> warns when it isn't.</summary>
    public bool Required { get; init; }
    /// <summary>Its connections are beats in time (a sequence's cues), shown by delay on a time axis.</summary>
    public bool Timeline { get; init; }
    public string Label => EntityClass.Humanize(Name);
}

public enum TargetKind { Entity, Tag, Special, Flag }

/// <summary>
/// What a connection acts on: an entity by id, every entity with a tag (<see cref="Name"/>), a special
/// (<c>self</c>, <c>activator</c>, <c>caller</c>) or a project flag.
/// </summary>
public sealed record ConnectionTarget(TargetKind Kind, int Entity = 0, string Name = "");

/// <summary>A connection's condition, checked when its output fires: the flag equals <see cref="Is"/>, or reaches <see cref="AtLeast"/>.</summary>
public sealed record Guard(string Flag, JsonElement? Is = null, int? AtLeast = null);

/// <summary>
/// One wire from an output of the entity holding it to an input of its target (docs/logic.md): <c>on</c> → target.
/// <c>do</c>(args) after <see cref="Delay"/> seconds, optionally once and under a <see cref="When"/> guard. Stored in the
/// level's <c>outputs</c> with defaults left out, and exported as <c>chip_io</c> with every field written.
/// </summary>
public sealed class Connection
{
    public string On { get; set; } = "";
    public ConnectionTarget Target { get; set; } = new(TargetKind.Special, Name: "self");
    public string Do { get; set; } = "";
    /// <summary>Argument values by param name, typed like properties; <c>{ "payload": true }</c> passes the output's payload.</summary>
    public Dictionary<string, JsonElement> Args { get; set; } = new();
    public float Delay { get; set; }
    public bool Once { get; set; }
    public Guard? When { get; set; }
    /// <summary>A target shape this Chip doesn't know, kept verbatim so a newer level survives editing here.</summary>
    public JsonElement? UnknownTarget { get; set; }

    public Connection Clone() => new()
    {
        On = On, Target = Target, Do = Do, Args = new(Args), Delay = Delay, Once = Once, When = When, UnknownTarget = UnknownTarget,
    };

    /// <summary>The connection as JSON: defaults left out (levels), or every field written (<paramref name="defaults"/>, the export).</summary>
    public JsonObject ToJson(bool defaults = false)
    {
        var o = new JsonObject { ["on"] = On, ["target"] = TargetJson(), ["do"] = Do };
        if (defaults || Args.Count > 0) o["args"] = JsonSerializer.SerializeToNode(Args);
        if (defaults || Delay != 0) o["delay"] = Delay;
        if (defaults || Once) o["once"] = Once;
        if (When is { } w)
        {
            var guard = new JsonObject { ["flag"] = w.Flag };
            if (w.Is is { } value) guard["is"] = JsonNode.Parse(value.GetRawText());
            if (w.AtLeast is { } n) guard["atLeast"] = n;
            o["when"] = guard;
        }
        return o;
    }

    JsonNode TargetJson()
    {
        if (UnknownTarget is { } unknown) return JsonNode.Parse(unknown.GetRawText())!;
        string key = Target.Kind.ToString().ToLowerInvariant();
        return new JsonObject { [key] = Target.Kind == TargetKind.Entity ? JsonValue.Create(Target.Entity) : JsonValue.Create(Target.Name) };
    }

    /// <summary>Reads a stored connection. A target that isn't exactly one known form is kept as <see cref="UnknownTarget"/>.</summary>
    public static Connection FromJson(JsonElement o)
    {
        var c = new Connection { On = o.GetProperty("on").GetString()!, Do = o.GetProperty("do").GetString()! };
        var t = o.GetProperty("target");
        if (KnownTarget(t) is { } target) c.Target = target;
        else c.UnknownTarget = t.Clone();
        if (o.TryGetProperty("args", out var args)) c.Args = args.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
        if (o.TryGetProperty("delay", out var delay)) c.Delay = delay.GetSingle();
        if (o.TryGetProperty("once", out var once)) c.Once = once.GetBoolean();
        if (o.TryGetProperty("when", out var w))
            c.When = new(w.GetProperty("flag").GetString()!,
                w.TryGetProperty("is", out var value) ? value.Clone() : null,
                w.TryGetProperty("atLeast", out var n) ? n.GetInt32() : null);
        return c;
    }

    /// <summary><c>{ "entity": 7 }</c>, <c>{ "tag": "x" }</c>, <c>{ "special": "self" }</c> or <c>{ "flag": "x" }</c>; null for anything else.</summary>
    static ConnectionTarget? KnownTarget(JsonElement t)
    {
        if (t.ValueKind != JsonValueKind.Object || t.EnumerateObject().ToArray() is not [var p]) return null;
        return p.Name switch
        {
            "entity" when p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt32(out int id) => new(TargetKind.Entity, id),
            "tag" or "special" or "flag" when p.Value.ValueKind == JsonValueKind.String =>
                new(Enum.Parse<TargetKind>(p.Name, ignoreCase: true), Name: p.Value.GetString()!),
            _ => null,
        };
    }
}
