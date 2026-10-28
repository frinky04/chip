using System.Runtime.CompilerServices;
using System.Text.Json;
using Chip.Core.Scene;
using Chip.Editor.Framework;

namespace Chip.Editor.Features.Logic;

/// <summary>One entity pointing at another: through a reference property (<see cref="Via"/> is its label) or a connection ("opened → turn off").</summary>
public readonly record struct Link(int From, int To, string Via, bool Connection);

/// <summary>
/// Every link between the level's entities, rebuilt when the session changes: what each entity points at and what points
/// at it. The inspector lists them and the overlay draws them for the selection.
/// </summary>
public static class EntityLinks
{
    sealed record Index(Dictionary<int, List<Link>> Out, Dictionary<int, List<Link>> In);

    static readonly ConditionalWeakTable<EditorContext, Rendering.FrameCache<Index>> Cache = new();

    public static IReadOnlyList<Link> From(EditorContext ctx, int entity) => Get(ctx).Out.GetValueOrDefault(entity) ?? [];
    public static IReadOnlyList<Link> To(EditorContext ctx, int entity) => Get(ctx).In.GetValueOrDefault(entity) ?? [];

    static Index Get(EditorContext ctx)
    {
        var cache = Cache.GetValue(ctx, _ => new Rendering.FrameCache<Index>(selection: false));
        if (cache.IsCurrent(ctx.Session)) return cache.Value;
        var index = new Index(new(), new());
        foreach (var e in ctx.Doc.Entities)
            foreach (var link in Of(ctx.Doc, e))
            {
                Add(index.Out, link.From, link);
                Add(index.In, link.To, link);
            }
        cache.Store(ctx.Session, index);
        return index;
    }

    static void Add(Dictionary<int, List<Link>> map, int key, Link link)
    {
        if (!map.TryGetValue(key, out var list)) map[key] = list = new();
        list.Add(link);
    }

    /// <summary>The links leaving <paramref name="e"/> to entities that exist, in property then connection order.</summary>
    static IEnumerable<Link> Of(Document doc, Entity e)
    {
        var cls = EntityClasses.Get(e.ClassName);
        foreach (var (key, value) in e.Properties)
        {
            var spec = cls.Prop(key);
            if (spec is not { Type: PropType.Node } && spec is not { Type: PropType.Array, ItemType: PropType.Node }) continue;
            string via = spec.Label;
            if (value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in value.EnumerateArray())
                    if (EntityValues.RefId(item) is { } id && id != e.Id && doc.FindEntity(id) != null) yield return new(e.Id, id, via, false);
            }
            else if (EntityValues.RefId(value) is { } id && id != e.Id && doc.FindEntity(id) != null) yield return new(e.Id, id, via, false);
        }
        foreach (var c in e.Outputs)
        {
            string via = $"{Humanize(c.On)} → {Humanize(c.Do)}";
            foreach (var target in LogicOps.TargetsOf(doc, e, c.Target))
                if (target.Id != e.Id) yield return new(e.Id, target.Id, via, true);
        }
    }

    static string Humanize(string name) => EntityClass.Humanize(name).ToLowerInvariant();
}
