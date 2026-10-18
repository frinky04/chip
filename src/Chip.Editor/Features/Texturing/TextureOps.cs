using System.Runtime.CompilerServices;
using Chip.Core.Scene;
using Chip.Core.Texturing;
using Chip.Editor.Framework;

namespace Chip.Editor.Features.Texturing;

/// <summary>Texturing settings shared by the texture commands, the inspector section and the fast texture tool.</summary>
public sealed class TextureOptions
{
    static readonly ConditionalWeakTable<EditorContext, TextureOptions> ByContext = new();

    /// <summary>The options of one editor (kept beside the context, so headless test contexts don't share state).</summary>
    public static TextureOptions For(EditorContext ctx) => ByContext.GetValue(ctx, _ => new TextureOptions());

    public FitMode Fit { get; set; } = FitMode.Both;
    public bool KeepRatio { get; set; }
    public bool TreatAsOne { get; set; }
    public QuadSpacing Spacing { get; set; } = QuadSpacing.Length;
    /// <summary>Last unwrap run, so changing Follow Quads spacing can re-run it.</summary>
    public UnwrapMode? LastUnwrap { get; set; }

    public FitOptions FitOptions => new(Fit, KeepRatio, TreatAsOne);
}

/// <summary>Selection plumbing for texture edits: which faces, their texture sizes, and one undo step per edit.</summary>
public static class TextureOps
{
    public static TextureSize SizeOf(EditorContext ctx) => name =>
    {
        var info = ctx.Materials.Get(name);
        return (info.Width, info.Height);
    };

    public static bool HasFaces(EditorContext ctx) => !ctx.Sel.IsEmpty && ctx.Sel.Mode is SelectMode.Face or SelectMode.Object;

    /// <summary>Faces the selection addresses (selected faces, or all faces of selected objects), the active face first.</summary>
    public static List<FaceRef> Faces(EditorContext ctx)
    {
        var result = new List<FaceRef>();
        foreach (var (mesh, faces) in ctx.Sel.FaceSets(ctx.Doc))
            result.AddRange(faces.Where(f => f < mesh.Faces.Count).Select(f => new FaceRef(mesh, f)));
        if (ActiveFace(ctx) is { } active && result.Remove(active)) result.Insert(0, active);
        return result;
    }

    public static FaceRef? ActiveFace(EditorContext ctx)
    {
        if (ctx.Sel.Mode != SelectMode.Face || ctx.Sel.Active is not { } a || ctx.Doc.Find(a.Mesh) is not { } mesh || a.A >= mesh.Faces.Count) return null;
        return new FaceRef(mesh, a.A);
    }

    /// <summary>Runs one undoable edit over the addressed faces, or says how to get some.</summary>
    public static bool Edit(EditorContext ctx, string label, Action<List<FaceRef>> edit)
    {
        var faces = Faces(ctx);
        if (faces.Count == 0)
        {
            ctx.Status($"Select faces or objects to {label.ToLowerInvariant()}");
            return false;
        }
        ctx.Session.Do(label, faces.Select(f => f.Mesh.Id), () => edit(faces));
        return true;
    }

    public static string Name(UnwrapMode mode) => mode switch
    {
        UnwrapMode.UseExisting => "Use existing",
        UnwrapMode.Conforming => "Conforming",
        UnwrapMode.Square => "Square",
        UnwrapMode.FollowQuads => "Follow quads",
        _ => "Planar",
    };

    public static string Name(QuadSpacing spacing) => spacing switch
    {
        QuadSpacing.Even => "Even",
        QuadSpacing.LengthAverage => "Length average",
        _ => "Length",
    };

    /// <summary>Unwraps the addressed faces (per mesh), writing explicit UVs at their material's texel density.</summary>
    public static void Unwrap(EditorContext ctx, UnwrapMode mode)
    {
        var options = For(ctx);
        options.LastUnwrap = mode;
        var size = SizeOf(ctx);
        var active = ActiveFace(ctx);
        int count = 0;
        bool done = Edit(ctx, $"Unwrap {Name(mode)}", faces =>
        {
            foreach (var group in faces.GroupBy(f => f.Mesh))
            {
                var list = group.Select(f => f.Index).ToList();
                int reference = active is { } a && a.Mesh == group.Key ? a.Index : -1;
                float density = ctx.Materials.Get(group.Key.Faces[reference >= 0 ? reference : list[0]].Material).TexelsPerMeter;
                Chip.Core.Texturing.Unwrap.Apply(group.Key, list, mode, size, options.Spacing, reference, density);
                count += list.Count;
            }
        });
        if (done)
            ctx.Status(mode == UnwrapMode.FollowQuads
                ? $"Unwrapped {Plural(count)} with follow quads ({Name(options.Spacing).ToLowerInvariant()} spacing)"
                : $"Unwrapped {Plural(count)}: {Name(mode).ToLowerInvariant()}");
    }

    /// <summary>Removes explicit UVs so the faces show their planar projection again.</summary>
    public static void ClearExplicit(EditorContext ctx)
    {
        int count = Faces(ctx).Count(f => f.Face.UVs != null);
        if (count == 0) { ctx.Status("These faces already use their projection"); return; }
        if (Edit(ctx, "Clear Explicit UVs", faces => { foreach (var f in faces) f.Face.UVs = null; }))
            ctx.Status($"Cleared explicit UVs on {Plural(count)}; they use their projection again");
    }

    static TextureOptions For(EditorContext ctx) => TextureOptions.For(ctx);

    public static string Plural(int faces) => faces == 1 ? "1 face" : $"{faces} faces";
}
