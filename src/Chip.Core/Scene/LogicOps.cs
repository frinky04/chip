using System.Text.Json;

namespace Chip.Core.Scene;

public enum Severity { Warning, Error }

/// <summary>One thing wrong with a level's logic. <see cref="Message"/> starts with the entity's name (its class and id when unnamed).</summary>
public sealed record LevelProblem(Severity Severity, int? Entity, string Message);

/// <summary>One entity pointing at another: through a reference property (<see cref="Via"/> names it) or connection <see cref="Connection"/>.</summary>
public sealed record EntityLink(Entity Source, Entity Target, string Via, int? Connection);

/// <summary>
/// Queries over a level's logic: what a connection's target resolves to and offers, the links between entities, and the
/// checks <c>--check-levels</c> runs (docs/logic.md, Checks).
/// </summary>
public static class LogicOps
{
    /// <summary>
    /// The entities a target names in the level: the entity, every tagged one, or the source for <c>self</c>. Empty for
    /// flags and for <c>activator</c> / <c>caller</c>, which only the running game knows.
    /// </summary>
    public static IReadOnlyList<Entity> TargetsOf(Document doc, Entity source, ConnectionTarget target) => target.Kind switch
    {
        TargetKind.Entity => doc.Entities.Where(e => e.Id == target.Entity).ToArray(),
        TargetKind.Tag => doc.Entities.Where(e => e.Tags.Contains(target.Name)).ToArray(),
        TargetKind.Special when target.Name == "self" => [source],
        _ => [],
    };

    /// <summary>
    /// The inputs a connection to <paramref name="target"/> can use: the target's class's; for a tag, those every tagged
    /// entity's class has with the same params; for a flag, <c>set</c> plus <c>toggle</c> (bool) or <c>add</c> (int).
    /// Empty when a target's class is unknown, and for targets only the game resolves.
    /// </summary>
    public static IReadOnlyList<InputSpec> InputsFor(Document doc, EntityCatalog catalog, GameProject? project, Entity source, ConnectionTarget target)
    {
        if (target.Kind == TargetKind.Flag)
            return project?.Flags.GetValueOrDefault(target.Name) is { } flag ? FlagInputs(flag) : [];
        var classes = TargetsOf(doc, source, target).Select(e => catalog.Find(e.ClassName)).ToArray();
        if (classes.Length == 0 || classes.Any(c => c == null)) return [];
        return classes[0]!.Inputs.Where(i => classes.All(c => c!.Inputs.Any(j => SameShape(i, j)))).ToArray();
    }

    static InputSpec[] FlagInputs(FlagSpec flag)
    {
        var type = flag.Type == FlagType.Bool ? PropType.Bool : PropType.Int;
        var set = new InputSpec { Name = "set", Params = [new() { Name = "value", Type = type, Default = flag.Default }] };
        return flag.Type == FlagType.Bool
            ? [set, new InputSpec { Name = "toggle" }]
            : [set, new InputSpec { Name = "add", Params = [new() { Name = "amount", Type = PropType.Int, Default = EntityValues.Of(1L) }] }];
    }

    /// <summary>Same name and params (names and types), so one connection can drive both.</summary>
    static bool SameShape(InputSpec a, InputSpec b) => a.Name == b.Name && a.Params.Count == b.Params.Count
        && a.Params.Zip(b.Params).All(p => p.First.Name == p.Second.Name && p.First.Type == p.Second.Type && p.First.ItemType == p.Second.ItemType);

    public static IReadOnlyList<EntityLink> ReferencesTo(Document doc, EntityCatalog catalog, int entityId) =>
        Links(doc, catalog).Where(l => l.Target.Id == entityId).ToArray();

    public static IReadOnlyList<EntityLink> ReferencesFrom(Document doc, EntityCatalog catalog, int entityId) =>
        Links(doc, catalog).Where(l => l.Source.Id == entityId).ToArray();

    /// <summary>Every reference property value (class defaults included) and every connection target that resolves to an entity.</summary>
    static IEnumerable<EntityLink> Links(Document doc, EntityCatalog catalog)
    {
        foreach (var e in doc.Entities)
        {
            foreach (var p in catalog.Find(e.ClassName)?.Props ?? [])
                if (IsReference(p))
                    foreach (int id in RefIds(EntityValues.Effective(e, p)))
                        if (doc.FindEntity(id) is { } t) yield return new(e, t, p.Name, null);
            for (int i = 0; i < e.Outputs.Count; i++)
                foreach (var t in TargetsOf(doc, e, e.Outputs[i].Target)) yield return new(e, t, e.Outputs[i].On, i);
        }
    }

    static bool IsReference(PropSpec p) => p.Type == PropType.Node || p.Type == PropType.Array && p.ItemType == PropType.Node;

    static IEnumerable<int> RefIds(JsonElement v) =>
        v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().SelectMany(RefIds) : EntityValues.RefId(v) is { } id ? [id] : [];

    // ---- Checks ----

    /// <summary>Everything <c>--check-levels</c> reports for one level, in entity order, then zero-delay loops.</summary>
    public static IReadOnlyList<LevelProblem> Check(Document doc, EntityCatalog catalog, GameProject? project)
    {
        var check = new Checker(doc, catalog, project);
        foreach (var e in doc.Entities) check.Entity(e);
        foreach (var e in doc.Entities)
            if (LoopsBack(doc, e, e, new HashSet<int>())) check.Add(e, "cycle of connections with zero delay", Severity.Warning);
        return check.Problems;
    }

    /// <summary>
    /// What <see cref="Check"/> reports about one connection of <paramref name="source"/> (empty when its class is
    /// unknown, which is the entity's problem): the editor marks the rows of its connection tables with it.
    /// </summary>
    public static IReadOnlyList<LevelProblem> CheckConnection(Document doc, EntityCatalog catalog, GameProject? project, Entity source, Connection c)
    {
        var check = new Checker(doc, catalog, project);
        if (catalog.Find(source.ClassName) is { } cls) check.Connection(source, cls, c);
        return check.Problems;
    }

    // Whether zero-delay connections from e lead back to start.
    static bool LoopsBack(Document doc, Entity start, Entity e, HashSet<int> visited)
    {
        if (!visited.Add(e.Id)) return false;
        foreach (var c in e.Outputs.Where(c => c.Delay == 0))
            foreach (var target in TargetsOf(doc, e, c.Target))
                if (target.Id == start.Id || LoopsBack(doc, start, target, visited)) return true;
        return false;
    }

    static bool IsPayload(JsonElement v) =>
        v.ValueKind == JsonValueKind.Object && v.TryGetProperty("payload", out var b) && b.ValueKind == JsonValueKind.True;

    /// <summary>The checks, collecting problems against one level, its classes and its project.</summary>
    sealed class Checker(Document doc, EntityCatalog catalog, GameProject? project)
    {
        public readonly List<LevelProblem> Problems = [];

        public void Add(Entity e, string message, Severity severity = Severity.Error) =>
            Problems.Add(new(severity, e.Id, $"{(e.Name.Length > 0 ? e.Name : $"{e.ClassName} #{e.Id}")}: {message}"));

        public void Entity(Entity e)
        {
            if (catalog.Find(e.ClassName) is not { } cls) { Add(e, $"unknown class {e.ClassName}"); return; }
            foreach (var key in e.Properties.Keys)
                if (cls.Prop(key) == null) Add(e, $"unused property {key}", Severity.Warning);
            foreach (var p in cls.Props)
                if (e.Properties.TryGetValue(p.Name, out var value)) Value(e, p, value);
                else if (!EntityValues.IsNull(p.Default)) Value(e, p, p.Default);
            foreach (var o in cls.Outputs.Where(o => o.Required && !e.Outputs.Any(c => c.On == o.Name)))
                Add(e, $"required output {o.Name} has no connection", Severity.Warning);
            foreach (var c in e.Outputs) Connection(e, cls, c);
        }

        // A value its type can read, references to existing entities the property accepts, assets that exist.
        void Value(Entity e, PropSpec p, JsonElement v)
        {
            if (EntityValues.Coerce(p, v, out var error) is not { } typed) { Add(e, error ?? $"{p.Name} has an invalid value"); return; }
            if (p.Type == PropType.Array && typed.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in typed.EnumerateArray()) Value(e, p.Item, item);
                return;
            }
            if (p.Type == PropType.Node)
                foreach (int id in RefIds(typed))
                {
                    if (doc.FindEntity(id) is not { } t) Add(e, $"{p.Name} points at missing entity #{id}");
                    else if (catalog.Find(t.ClassName) is { } targetClass && !p.Accepts(targetClass)) Add(e, $"{p.Name} does not accept {t.ClassName} #{id}");
                }
            if (p.Type == PropType.Resource && p.Kind != null && EntityValues.Text(typed) is { Length: > 0 } asset
                && project?.Kinds.GetValueOrDefault(p.Kind) is { } kind && !kind.Exists(asset))
                Add(e, $"{p.Name} asset {asset} does not exist");
        }

        public void Connection(Entity e, EntityClass cls, Connection c)
        {
            var output = cls.Output(c.On);
            if (output == null) Add(e, $"{c.On} is not an output");
            if (c.UnknownTarget != null) { Add(e, "unknown connection target"); return; }
            var t = c.Target;
            if (t.Kind == TargetKind.Entity && doc.FindEntity(t.Entity) == null) Add(e, $"connection points at missing entity #{t.Entity}");
            if (t.Kind == TargetKind.Tag && TargetsOf(doc, e, t).Count == 0) Add(e, $"no entity carries tag {t.Name}", Severity.Warning);
            if (t.Kind == TargetKind.Flag && project?.Flags.ContainsKey(t.Name) != true) Add(e, $"undeclared flag {t.Name}");
            if (c.When is { } w) Guard(e, w);
            // The game alone knows who the activator and caller are, so their inputs can't be checked here.
            if (t is { Kind: TargetKind.Special, Name: "activator" or "caller" }) return;

            var input = InputsFor(doc, catalog, project, e, t).FirstOrDefault(i => i.Name == c.Do);
            if (input == null)
            {
                // An empty tag is already reported; every input would look missing.
                if (t.Kind != TargetKind.Tag || TargetsOf(doc, e, t).Count > 0) Add(e, $"{c.Do} is not an input of the target");
                return;
            }
            foreach (var (key, v) in c.Args)
            {
                var p = input.Params.FirstOrDefault(p => p.Name == key);
                if (p == null) Add(e, $"{c.Do} has no parameter {key}");
                else if (IsPayload(v)) Payload(e, output, p, key);
                else Value(e, p, v);
            }
        }

        void Guard(Entity e, Guard w)
        {
            if (project?.Flags.GetValueOrDefault(w.Flag) is not { } flag) Add(e, $"guard names undeclared flag {w.Flag}");
            else if (w.AtLeast != null && flag.Type != FlagType.Int) Add(e, $"atLeast needs an int flag, but {w.Flag} is bool");
            else if (w.Is is { } v) Value(e, new() { Name = w.Flag, Type = flag.Type == FlagType.Bool ? PropType.Bool : PropType.Int }, v);
        }

        // The payload's type must be the param's; for references, every class the payload may be must suit the param.
        void Payload(Entity e, OutputSpec? output, PropSpec p, string key)
        {
            if (output?.Payload is not { } payload || payload.Type != p.Type || payload.ItemType != p.ItemType)
                Add(e, $"payload type does not match {key}");
            else if (IsReference(p) && catalog.Classes.Where(payload.Accepts).Any(t => !p.Accepts(t)))
                Add(e, $"payload reference type does not match {key}");
        }
    }
}
