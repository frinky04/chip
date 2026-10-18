using System.Numerics;
using Chip.Core.Geometry;
using Chip.Core.Scene;
using Chip.Core.Texturing;
using Chip.Editor.Framework;
using ImGuiNET;

namespace Chip.Editor.Features.Texturing;

/// <summary>
/// Texturing beyond the basics (SPEC §8): unwraps, numpad shift/scale/rotate, justify, flip face UVs, fit options,
/// texture lock, Wrap / Wrap to Selection / Flow mouse chords, and the fast texture tool.
/// </summary>
public sealed class TexturingFeature : IEditorFeature
{
    const float RotateStep = 15f;

    public void Register(EditorContext ctx)
    {
        var c = ctx.Commands;
        var options = TextureOptions.For(ctx);
        var size = TextureOps.SizeOf(ctx);
        bool Has() => TextureOps.HasFaces(ctx);

        ctx.AddTool(new FastTextureTool());
        FastTextureTool.RegisterReferences(c);

        // ---- Unwraps (also keys 1-5 inside the fast texture tool) ----
        (UnwrapMode Mode, string Id)[] unwraps =
        [
            (UnwrapMode.UseExisting, "unwrapExisting"), (UnwrapMode.Conforming, "unwrapConforming"), (UnwrapMode.Square, "unwrapSquare"),
            (UnwrapMode.FollowQuads, "unwrapFollowQuads"), (UnwrapMode.Planar, "unwrapPlanar"),
        ];
        foreach (var (mode, id) in unwraps)
            c.Add(new Command
            {
                Id = $"texture.{id}", Label = $"Unwrap: {TextureOps.Name(mode).ToLowerInvariant()}", Menu = "Texture", Group = "5-unwrap",
                Order = (int)mode, CanExecute = Has, Execute = () => TextureOps.Unwrap(ctx, mode), Description = mode switch
                {
                    UnwrapMode.UseExisting => "Freeze the current UVs as explicit UVs",
                    UnwrapMode.Conforming => "Unfold connected faces flat, keeping every face's shape, at 512 px/m",
                    UnwrapMode.Square => "Lay quads out as equal squares along their rows",
                    UnwrapMode.FollowQuads => "Straighten quad strips and grids from the active face",
                    _ => "Project all faces onto one plane at 512 px/m",
                },
            });
        c.Add("texture.clearUVs", "Clear explicit UVs", () => TextureOps.ClearExplicit(ctx), "Texture",
            canExecute: () => Has() && TextureOps.Faces(ctx).Any(f => f.Face.UVs != null), group: "5-unwrap", order: 10);

        // ---- Numpad: shift by one grid step, scale, rotate ----
        (string Id, string Label, string Key, int X, int Y)[] shifts =
        [
            ("shiftLeft", "Shift texture left", "Num4", -1, 0), ("shiftRight", "Shift texture right", "Num6", 1, 0),
            ("shiftUp", "Shift texture up", "Num8", 0, 1), ("shiftDown", "Shift texture down", "Num2", 0, -1),
            ("shiftUpLeft", "Shift texture up left", "Num7", -1, 1), ("shiftUpRight", "Shift texture up right", "Num9", 1, 1),
            ("shiftDownLeft", "Shift texture down left", "Num1", -1, -1), ("shiftDownRight", "Shift texture down right", "Num3", 1, -1),
        ];
        foreach (var (id, label, key, x, y) in shifts)
            c.Add($"texture.{id}", label, () => Shift(ctx, x, y), hotkey: key, canExecute: Has);
        c.AddReference("Texture", "Num1-4, Num6-9", "shift texture one grid step");

        (string Id, string Label, string Key, float X, float Y)[] scales =
        [
            ("scaleWider", "Scale texture wider", "Ctrl+Alt+Num6", 2, 1), ("scaleNarrower", "Scale texture narrower", "Ctrl+Alt+Num4", 0.5f, 1),
            ("scaleTaller", "Scale texture taller", "Ctrl+Alt+Num8", 1, 2), ("scaleShorter", "Scale texture shorter", "Ctrl+Alt+Num2", 1, 0.5f),
        ];
        foreach (var (id, label, key, x, y) in scales)
            c.Add($"texture.{id}", label, () => TextureOps.Edit(ctx, "Scale Texture",
                faces => Justify.Scale(faces, new Vector2(x, y), size, options.TreatAsOne)), hotkey: key, canExecute: Has);
        c.AddReference("Texture", "Ctrl+Alt+Num4/6/8/2", "scale texture");

        c.Add("texture.rotateCW", "Rotate texture clockwise", () => TextureOps.Edit(ctx, "Rotate Texture",
            faces => Justify.Rotate(faces, RotateStep, size, options.TreatAsOne)), "Texture", "Alt+Num9", Has, "4-adjust", 0);
        c.Add("texture.rotateCCW", "Rotate texture anticlockwise", () => TextureOps.Edit(ctx, "Rotate Texture",
            faces => Justify.Rotate(faces, -RotateStep, size, options.TreatAsOne)), "Texture", "Alt+Num7", Has, "4-adjust", 1);
        c.Add("texture.flipFaceU", "Flip face UVs horizontally", () => TextureOps.Edit(ctx, "Flip Face UVs",
            faces => Justify.Flip(faces, horizontal: true, size, options.TreatAsOne)), "Texture", "Alt+R", Has, "4-adjust", 2);
        c.Add("texture.flipFaceV", "Flip face UVs vertically", () => TextureOps.Edit(ctx, "Flip Face UVs",
            faces => Justify.Flip(faces, horizontal: false, size, options.TreatAsOne)), "Texture", "Alt+T", Has, "4-adjust", 3);

        // ---- Justify ----
        (JustifyMode Mode, string Id, string Label, string Key)[] justify =
        [
            (JustifyMode.Left, "justifyLeft", "Justify left", "Alt+Num4"), (JustifyMode.Right, "justifyRight", "Justify right", "Alt+Num6"),
            (JustifyMode.Top, "justifyTop", "Justify top", "Alt+Num8"), (JustifyMode.Bottom, "justifyBottom", "Justify bottom", "Alt+Num2"),
            (JustifyMode.Center, "justifyCenter", "Justify centre", "Alt+Num5"),
        ];
        foreach (var (mode, id, label, key) in justify)
            c.Add($"texture.{id}", label, () => TextureOps.Edit(ctx, label,
                faces => Justify.Align(faces, mode, size, options.TreatAsOne)), "Texture", key, Has, "6-justify", (int)mode);

        // ---- Fit options and texture lock (the Fit command itself is texture.fit) ----
        c.Add("texture.fitWidth", "Fit width", () => Fit(ctx, FitMode.Width), "Texture", canExecute: Has, group: "7-fit", order: 0);
        c.Add("texture.fitHeight", "Fit height", () => Fit(ctx, FitMode.Height), "Texture", canExecute: Has, group: "7-fit", order: 1);
        c.Add("texture.keepRatio", "Keep ratio when fitting", () => options.KeepRatio = !options.KeepRatio, "Texture",
            group: "7-fit", order: 2, isChecked: () => options.KeepRatio);
        c.Add("texture.treatAsOne", "Treat selection as one", () => options.TreatAsOne = !options.TreatAsOne, "Texture",
            group: "7-fit", order: 3, isChecked: () => options.TreatAsOne);
        c.Add("texture.lock", "Texture lock", () =>
        {
            ctx.Session.TextureLock = !ctx.Session.TextureLock;
            ctx.Status(ctx.Session.TextureLock ? "Texture lock on: textures follow moved objects" : "Texture lock off: textures stay world-aligned when objects move");
        }, "Texture", group: "8-lock", isChecked: () => ctx.Session.TextureLock);

        // ---- Mouse chords in the viewport ----
        ctx.AddMouseBinding(new MouseBinding(ImGuiMouseButton.Right, Ctrl: false, Shift: false, Alt: true, "Texture", "wrap texture onto face", WrapOnto));
        ctx.AddMouseBinding(new MouseBinding(ImGuiMouseButton.Right, Ctrl: false, Shift: true, Alt: true, "Texture", "wrap texture to selection", WrapToSelection));
        ctx.AddMouseBinding(new MouseBinding(ImGuiMouseButton.Right, Ctrl: true, Shift: false, Alt: true, "Texture", "flow texture along quads", Flow));
    }

    /// <summary>Moves the image one grid step (in texels at 512 px/m) in a numpad direction on the face.</summary>
    static void Shift(EditorContext ctx, int x, int y)
    {
        float step = ctx.Grid * TextureMapping.TexelsPerMeter;
        // Image right = UVs decrease at a fixed point; image up = V increases (V points down the image).
        TextureOps.Edit(ctx, "Shift Texture", faces => Justify.Shift(faces, new Vector2(-x * step, y * step), TextureOps.SizeOf(ctx)));
    }

    public static void Fit(EditorContext ctx, FitMode? mode = null)
    {
        var options = TextureOptions.For(ctx);
        var fit = options.FitOptions with { Mode = mode ?? options.Fit };
        TextureOps.Edit(ctx, "Fit Texture", faces => Justify.Fit(faces, fit, TextureOps.SizeOf(ctx)));
    }

    /// <summary>Selected faces in <paramref name="mesh"/> that share an edge with <paramref name="face"/>, the active one first.</summary>
    static List<int> SelectedNeighbours(EditorContext ctx, PolyMesh mesh, int face)
    {
        if (ctx.Sel.Mode != SelectMode.Face) return [];
        var selected = ctx.Sel.Items.Where(i => i.Mesh == mesh.Id && i.A != face && i.A < mesh.Faces.Count).Select(i => i.A).ToList();
        if (ctx.Sel.Active is { } a && a.Mesh == mesh.Id && selected.Remove(a.A)) selected.Insert(0, a.A);
        return selected.Where(s => UvSpace.SharedEdge(mesh, s, face) != null).ToList();
    }

    /// <summary>Alt+RMB: continue the selected face's texture onto the clicked neighbour, which joins the selection.</summary>
    static void WrapOnto(EditorContext ctx)
    {
        if (ctx.RaycastFace(selectable: true) is not { } hit) return;
        var mesh = hit.Mesh;
        if (ctx.Sel.Mode != SelectMode.Face || ctx.Sel.IsEmpty)
        {
            ctx.Status("Select a face first, then Alt+RMB a face next to it to wrap the texture onto it", error: true);
            return;
        }
        if (ctx.Sel.Items.Contains(ElementRef.Face(mesh.Id, hit.Face)))
        {
            ctx.Status("That face is already selected. Alt+RMB a face next to the selection");
            return;
        }
        if (SelectedNeighbours(ctx, mesh, hit.Face) is not [var src, ..])
        {
            ctx.Status("Alt+RMB a face that shares an edge with a selected face", error: true);
            return;
        }
        ctx.Session.Do("Wrap Texture", [mesh.Id], () =>
        {
            Wrap.Across(mesh, src, hit.Face, TextureOps.SizeOf(ctx));
            ctx.Sel.Add(ElementRef.Face(mesh.Id, hit.Face));
        });
        ctx.Status("Wrapped texture onto the face. Alt+RMB the next face to keep going");
    }

    /// <summary>Shift+Alt+RMB: wrap the clicked face's texture across every connected selected face.</summary>
    static void WrapToSelection(EditorContext ctx)
    {
        if (ctx.RaycastFace(selectable: true) is not { } hit) return;
        var mesh = hit.Mesh;
        var targets = ctx.Sel.Mode == SelectMode.Face
            ? ctx.Sel.Items.Where(i => i.Mesh == mesh.Id && i.A != hit.Face && i.A < mesh.Faces.Count).Select(i => i.A).ToList()
            : [];
        if (targets.Count == 0)
        {
            ctx.Status("Select the faces to wrap onto, then Shift+Alt+RMB the face whose texture to use", error: true);
            return;
        }
        int done = 0;
        ctx.Session.Do("Wrap Texture to Selection", [mesh.Id], () => done = Wrap.ToSelection(mesh, hit.Face, targets, TextureOps.SizeOf(ctx)).Count);
        ctx.Status(done == 0
            ? "None of the selected faces connect to that face. Pick a face that touches the selection"
            : $"Wrapped texture onto {TextureOps.Plural(done)}" + (done < targets.Count ? $" ({targets.Count - done} not connected)" : ""));
    }

    /// <summary>Ctrl+Alt+RMB: from the selected quad, flow its texture through the clicked quad and on along the strip.</summary>
    static void Flow(EditorContext ctx)
    {
        if (ctx.RaycastFace(selectable: true) is not { } hit) return;
        var mesh = hit.Mesh;
        var options = TextureOptions.For(ctx);
        if (SelectedNeighbours(ctx, mesh, hit.Face) is not [var src, ..])
        {
            ctx.Status("Select a quad, then Ctrl+Alt+RMB the next quad of the strip to flow the texture along it", error: true);
            return;
        }
        if (Wrap.Strip(mesh, src, hit.Face).Count == 0)
        {
            ctx.Status("Flow follows quads. Select a quad and Ctrl+Alt+RMB a quad next to it", error: true);
            return;
        }
        int count = 0;
        ctx.Session.Do("Flow Texture", [mesh.Id], () =>
        {
            var strip = Wrap.Flow(mesh, src, hit.Face, TextureOps.SizeOf(ctx), options.Spacing);
            foreach (var f in strip) ctx.Sel.Items.Add(ElementRef.Face(mesh.Id, f));
            count = strip.Count;
        });
        ctx.Status($"Flowed texture along {TextureOps.Plural(count)}");
    }
}
