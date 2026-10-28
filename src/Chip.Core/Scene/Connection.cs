using System.Text.Json;
using System.Text.Json.Nodes;
using Chip.Core.Geometry;
namespace Chip.Core.Scene;

public sealed record InputSpec
{
    public required string Name { get; init; }
    public string Doc { get; init; } = "";
    public IReadOnlyList<string> Was { get; init; } = [];
    public IReadOnlyList<PropSpec> Params { get; init; } = [];
    public string Label => EntityClass.Humanize(Name);
}
public sealed record OutputSpec
{
    public required string Name { get; init; }
    public string Doc { get; init; } = "";
    public IReadOnlyList<string> Was { get; init; } = [];
    public PropSpec? Payload { get; init; }
    public bool Required { get; init; }
    public bool Timeline { get; init; }
    public string Label => EntityClass.Humanize(Name);
}
public enum TargetKind { Entity, Tag, Special, Flag }
public sealed record ConnectionTarget(TargetKind Kind, int Entity = 0, string Name = "");
public sealed record Guard(string Flag, JsonElement? Is = null, int? AtLeast = null);
public sealed class Connection
{
    public string On { get; set; } = "";
    public ConnectionTarget Target { get; set; } = new(TargetKind.Special, Name: "self");
    public string Do { get; set; } = "";
    public Dictionary<string, JsonElement> Args { get; set; } = new();
    public float Delay { get; set; }
    public bool Once { get; set; }
    public Guard? When { get; set; }
    /// <summary>Unrecognized target JSON is retained verbatim, so future target forms survive editing.</summary>
    public JsonElement? UnknownTarget { get; set; }
    public Connection Clone() => new() { On = On, Target = Target, Do = Do, Args = new(Args), Delay = Delay, Once = Once, When = When, UnknownTarget = UnknownTarget };
    public JsonObject ToJson(bool defaults = false)
    {
        var target = UnknownTarget is { } unknown ? JsonNode.Parse(unknown.GetRawText())! : new JsonObject { [Target.Kind.ToString().ToLowerInvariant()] = Target.Kind == TargetKind.Entity ? JsonValue.Create(Target.Entity) : JsonValue.Create(Target.Name) };
        var o = new JsonObject { ["on"] = On, ["target"] = target, ["do"] = Do };
        if (defaults || Args.Count > 0) o["args"] = JsonSerializer.SerializeToNode(Args);
        if (defaults || Delay != 0) o["delay"] = Delay;
        if (defaults || Once) o["once"] = Once;
        if (When is { } w) { var g = new JsonObject { ["flag"] = w.Flag }; if (w.Is is { } v) g["is"] = JsonNode.Parse(v.GetRawText()); if (w.AtLeast is { } n) g["atLeast"] = n; o["when"] = g; }
        return o;
    }
    public static Connection FromJson(JsonElement o)
    {
        var t = o.GetProperty("target");
        var props = t.ValueKind == JsonValueKind.Object ? t.EnumerateObject().ToArray() : [];
        bool known = props.Length == 1 && props[0].Name is "entity" or "tag" or "special" or "flag";
        known = known && (props[0].Name == "entity" ? props[0].Value.ValueKind == JsonValueKind.Number && props[0].Value.TryGetInt32(out _) : props[0].Value.ValueKind == JsonValueKind.String);
        var c = new Connection { On = o.GetProperty("on").GetString()!, Do = o.GetProperty("do").GetString()! };
        if (known) { var p = props[0]; c.Target = new(Enum.Parse<TargetKind>(p.Name, true), p.Name == "entity" ? p.Value.GetInt32() : 0, p.Name == "entity" ? "" : p.Value.GetString()!); }
        else c.UnknownTarget = t.Clone();
        if (o.TryGetProperty("args", out var args)) c.Args = args.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
        if (o.TryGetProperty("delay", out var delay)) c.Delay = delay.GetSingle();
        if (o.TryGetProperty("once", out var once)) c.Once = once.GetBoolean();
        if (o.TryGetProperty("when", out var w)) c.When = new(w.GetProperty("flag").GetString()!, w.TryGetProperty("is", out var v) ? v.Clone() : null, w.TryGetProperty("atLeast", out var n) ? n.GetInt32() : null);
        return c;
    }
}
public static class EntityTagOps
{
    public static int Set(Document doc, Selection sel, string tag, bool on) => Set(doc.Entities.Where(e => sel.Items.Any(i => i.Mesh == e.Id)), tag, on);
    public static int Set(IEnumerable<Entity> entities, string tag, bool on)
    {
        int count = 0;
        foreach (var e in entities) { var next = on ? e.Tags.Add(tag) : e.Tags.Remove(tag); if (next != e.Tags) { e.Tags = next; count++; } }
        return count;
    }
}
