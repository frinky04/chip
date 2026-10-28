using System.Text.Json;
using Chip.Core.IO;

namespace Chip.Core.Scene;

public enum Severity { Warning, Error }
public sealed record LevelProblem(Severity Severity, int? Entity, string Message);
public sealed record EntityLink(Entity Source, Entity Target, string Via, int? Connection);

public static class LogicOps
{
    public static IReadOnlyList<Entity> TargetsOf(Document doc, Entity source, ConnectionTarget target) => target.Kind switch
    {
        TargetKind.Entity => doc.Entities.Where(e => e.Id == target.Entity).ToArray(),
        TargetKind.Tag => doc.Entities.Where(e => e.Tags.Contains(target.Name)).ToArray(),
        TargetKind.Special when target.Name == "self" => [source],
        _ => [],
    };
    public static IReadOnlyList<InputSpec> InputsFor(Document doc, EntityCatalog catalog, GameProject? project, Entity source, ConnectionTarget target)
    {
        if (target.Kind == TargetKind.Flag)
        {
            if (project?.Flags.GetValueOrDefault(target.Name) is not { } flag) return [];
            var type = flag.Type == FlagType.Bool ? PropType.Bool : PropType.Int;
            return [new() { Name = "set", Params = [new() { Name = "value", Type = type, Default = flag.Default }] },
                flag.Type == FlagType.Bool ? new() { Name = "toggle" } : new InputSpec { Name = "add", Params = [new() { Name = "amount", Type = PropType.Int, Default = EntityValues.Of(1L) }] }];
        }
        var classes = TargetsOf(doc, source, target).Select(e => catalog.Find(e.ClassName)).ToArray();
        if (classes.Length == 0 || classes.Any(c => c == null)) return [];
        return classes[0]!.Inputs.Where(i => classes.All(c => c!.Inputs.Any(j => Compatible(i, j)))).ToArray();
    }
    static bool Compatible(InputSpec a, InputSpec b) => a.Name == b.Name && a.Params.Count == b.Params.Count && a.Params.Zip(b.Params).All(p => p.First.Name == p.Second.Name && p.First.Type == p.Second.Type && p.First.ItemType == p.Second.ItemType);
    public static IReadOnlyList<EntityLink> ReferencesTo(Document doc, EntityCatalog catalog, int entityId) => Links(doc, catalog).Where(l => l.Target.Id == entityId).ToArray();
    public static IReadOnlyList<EntityLink> ReferencesFrom(Document doc, EntityCatalog catalog, int entityId) => Links(doc, catalog).Where(l => l.Source.Id == entityId).ToArray();
    static IEnumerable<EntityLink> Links(Document doc, EntityCatalog catalog)
    {
        foreach (var e in doc.Entities)
        {
            foreach (var p in catalog.Find(e.ClassName)?.Props ?? [])
                if (p.Type == PropType.Node || p.Type == PropType.Array && p.ItemType == PropType.Node)
                    foreach (int id in RefIds(EntityValues.Effective(e, p)))
                        if (doc.Entities.FirstOrDefault(t => t.Id == id) is { } t) yield return new(e, t, p.Name, null);
            for (int i = 0; i < e.Outputs.Count; i++) foreach (var t in TargetsOf(doc, e, e.Outputs[i].Target)) yield return new(e, t, e.Outputs[i].On, i);
        }
    }
    static IEnumerable<int> RefIds(JsonElement v) => v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().SelectMany(RefIds) : EntityValues.RefId(v) is { } id ? [id] : [];
    public static IReadOnlyList<LevelProblem> Check(Document doc, EntityCatalog catalog, GameProject? project)
    {
        var problems = new List<LevelProblem>();
        void Add(Entity e, string message, Severity severity = Severity.Error) => problems.Add(new(severity, e.Id, $"{(e.Name.Length > 0 ? e.Name : $"{e.ClassName} #{e.Id}")}: {message}"));
        void Value(Entity e, PropSpec p, JsonElement v)
        {
            if (EntityValues.Coerce(p, v, out var error) is not { } typed) { Add(e, error ?? $"{p.Name} has an invalid value"); return; }
            v = typed;
            if (p.Type == PropType.Array && v.ValueKind == JsonValueKind.Array) { foreach (var item in v.EnumerateArray()) Value(e, p.Item, item); return; }
            if (p.Type == PropType.Node)
                foreach (int id in RefIds(v))
                    if (doc.Entities.FirstOrDefault(t => t.Id == id) is not { } t) Add(e, $"{p.Name} points at missing entity #{id}");
                    else if (catalog.Find(t.ClassName) is { } cls && !p.Accepts(cls)) Add(e, $"{p.Name} does not accept {t.ClassName} #{id}");
            if (p.Type == PropType.Resource && p.Kind != null && EntityValues.Text(v) is { Length: > 0 } asset && project?.Kinds.GetValueOrDefault(p.Kind) is { } kind && !kind.Exists(asset)) Add(e, $"{p.Name} asset {asset} does not exist");
        }
        foreach (var e in doc.Entities)
        {
            if (catalog.Find(e.ClassName) is not { } cls) { Add(e, $"unknown class {e.ClassName}"); continue; }
            foreach (var key in e.Properties.Keys) if (cls.Prop(key) == null) Add(e, $"unused property {key}", Severity.Warning);
            foreach (var p in cls.Props)
                if (e.Properties.TryGetValue(p.Name, out var value)) Value(e, p, value);
                else if (!EntityValues.IsNull(p.Default)) Value(e, p, p.Default);
            foreach (var o in cls.Outputs.Where(o => o.Required && !e.Outputs.Any(c => c.On == o.Name))) Add(e, $"required output {o.Name} has no connection", Severity.Warning);
            foreach (var c in e.Outputs)
            {
                var o = cls.Output(c.On);
                if (o == null) Add(e, $"{c.On} is not an output");
                if (c.UnknownTarget != null) { Add(e, "unknown connection target"); continue; }
                if (c.Target.Kind == TargetKind.Entity && !doc.Entities.Any(t => t.Id == c.Target.Entity)) Add(e, $"connection points at missing entity #{c.Target.Entity}");
                if (c.Target.Kind == TargetKind.Tag && TargetsOf(doc, e, c.Target).Count == 0) Add(e, $"no entity carries tag {c.Target.Name}", Severity.Warning);
                if (c.Target.Kind == TargetKind.Flag && project?.Flags.ContainsKey(c.Target.Name) != true) Add(e, $"undeclared flag {c.Target.Name}");
                if (c.When is { } w)
                {
                    if (project?.Flags.GetValueOrDefault(w.Flag) is not { } flag) Add(e, $"guard names undeclared flag {w.Flag}");
                    else if (w.AtLeast != null && flag.Type != FlagType.Int) Add(e, $"atLeast needs an int flag, but {w.Flag} is bool");
                    else if (w.Is is { } v) Value(e, new() { Name = w.Flag, Type = flag.Type == FlagType.Bool ? PropType.Bool : PropType.Int }, v);
                }
                bool runtime = c.Target.Kind == TargetKind.Special && c.Target.Name is "activator" or "caller";
                if (runtime) continue;
                var input = InputsFor(doc, catalog, project, e, c.Target).FirstOrDefault(i => i.Name == c.Do);
                if (input == null) { if (c.Target.Kind != TargetKind.Tag || TargetsOf(doc, e, c.Target).Count > 0) Add(e, $"{c.Do} is not an input of the target"); continue; }
                foreach (var (key, v) in c.Args)
                {
                    var p = input.Params.FirstOrDefault(p => p.Name == key);
                    if (p == null) { Add(e, $"{c.Do} has no parameter {key}"); continue; }
                    if (v.ValueKind == JsonValueKind.Object && v.TryGetProperty("payload", out var binding) && binding.ValueKind == JsonValueKind.True)
                    {
                        if (o?.Payload == null || o.Payload.Type != p.Type || o.Payload.ItemType != p.ItemType) Add(e, $"payload type does not match {key}");
                        else if (p.Type == PropType.Node || p.Type == PropType.Array && p.ItemType == PropType.Node)
                        {
                            var possible = catalog.Classes.Where(o.Payload.Accepts).ToArray();
                            if (possible.Any(t => !p.Accepts(t))) Add(e, $"payload reference type does not match {key}");
                        }
                    }
                    else Value(e, p, v);
                }
            }
        }
        foreach (var e in doc.Entities)
            if (Cycle(e, e, new HashSet<int>())) Add(e, "cycle of connections with zero delay", Severity.Warning);
        return problems;
        bool Cycle(Entity start, Entity e, HashSet<int> visited)
        {
            if (!visited.Add(e.Id)) return false;
            foreach (var c in e.Outputs.Where(c => c.Delay == 0))
                foreach (var target in TargetsOf(doc, e, c.Target)) if (target.Id == start.Id || Cycle(start, target, visited)) return true;
            return false;
        }
    }
}
