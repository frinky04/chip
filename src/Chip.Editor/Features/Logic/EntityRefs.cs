using Chip.Core.Scene;
using Chip.Editor.Framework;

namespace Chip.Editor.Features.Logic;

/// <summary>What a reference field accepts, in code and in words, and jumping to a target.</summary>
public static class EntityRefs
{
    /// <summary>Whether <paramref name="target"/> can fill the reference <paramref name="spec"/> (its class restriction; editor-only entities never).</summary>
    public static bool Accepts(PropSpec spec, Entity target)
    {
        var cls = EntityClasses.Get(target.ClassName);
        return !cls.EditorOnly && (spec.Class == null || spec.Class == target.ClassName);
    }

    /// <summary>"a door", "an entity" (or plural: "doors", "entities"), for hints and messages.</summary>
    public static string Wants(PropSpec spec, bool plural = false)
    {
        if (spec.Class == null) return plural ? "entities" : "an entity";
        var label = EntityClasses.Get(spec.Class).Label.ToLowerInvariant();
        return plural ? label + "s" : ("aeiou".Contains(label[0]) ? "an " : "a ") + label;
    }

    /// <summary>Selects <paramref name="e"/> alone, in object mode.</summary>
    public static void Select(EditorContext ctx, Entity e)
    {
        if (ctx.Sel.Mode != SelectMode.Object) ctx.SetMode(SelectMode.Object);
        ctx.Sel.Clear();
        ctx.Sel.Add(ElementRef.Object(e.Id));
        ctx.Status($"Selected {SceneOps.DisplayName(e)}");
    }
}
