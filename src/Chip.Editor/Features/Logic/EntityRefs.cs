using Chip.Core.Scene;
using Chip.Editor.Framework;

namespace Chip.Editor.Features.Logic;

/// <summary>What a reference field accepts, in code and in words, and jumping to a target.</summary>
public static class EntityRefs
{
    /// <summary>Whether <paramref name="target"/> can fill the reference <paramref name="spec"/> (its classes and trait; editor-only entities never).</summary>
    public static bool Accepts(PropSpec spec, Entity target)
    {
        var cls = EntityClasses.Get(target.ClassName);
        return !cls.EditorOnly && spec.Accepts(cls);
    }

    /// <summary>"a door", "a door or a window", "something lockable" (or plural: "doors", "lockable entities"), for hints and messages.</summary>
    public static string Wants(PropSpec spec, bool plural = false)
    {
        var parts = spec.Classes.Select(c => Article(EntityClasses.Get(c).Label.ToLowerInvariant(), plural)).ToList();
        if (spec.Trait != null) parts.Add(plural ? $"{Humanize(spec.Trait)} entities" : $"something {Humanize(spec.Trait)}");
        return parts.Count == 0 ? plural ? "entities" : "an entity" : string.Join(" or ", parts);
    }

    static string Article(string label, bool plural) => plural ? label + "s" : ("aeiou".Contains(label[0]) ? "an " : "a ") + label;

    static string Humanize(string name) => name.Replace('_', ' ');

    /// <summary>Selects <paramref name="e"/> alone, in object mode.</summary>
    public static void Select(EditorContext ctx, Entity e)
    {
        if (ctx.Sel.Mode != SelectMode.Object) ctx.SetMode(SelectMode.Object);
        ctx.Sel.Clear();
        ctx.Sel.Add(ElementRef.Object(e.Id));
        ctx.Status($"Selected {SceneOps.DisplayName(e)}");
    }
}
