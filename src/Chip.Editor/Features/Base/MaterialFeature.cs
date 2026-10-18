using Chip.Core.Scene;
using Chip.Core.Texturing;
using Chip.Editor.Features.Texturing;
using Chip.Editor.Framework;
using ImGuiNET;

namespace Chip.Editor.Features.Base;

/// <summary>Active material, apply / pick up, blockout slots, and the basic texture alignment commands.</summary>
public sealed class MaterialFeature : IEditorFeature
{
    public void Register(EditorContext ctx)
    {
        var c = ctx.Commands;
        bool HasFaceTargets() => !ctx.Sel.IsEmpty && ctx.Sel.Mode is SelectMode.Face or SelectMode.Object;

        c.Add("texture.apply", "Apply active material", () => ctx.ForFaces("Apply Material", (mesh, faces) =>
        {
            foreach (var f in faces) mesh.Faces[f].Material = ctx.ActiveMaterial;
        }), "Texture", "Shift+T", HasFaceTargets, "1-material");

        for (int i = 0; i < 10; i++)
        {
            int slot = i;
            c.Add($"texture.slot{slot + 1}", $"Material slot {slot + 1}", () =>
            {
                if (slot >= ctx.Materials.All.Count) return;
                ctx.ActiveMaterial = ctx.Materials.All[slot].Name;
                ctx.Status($"Active material: {ctx.ActiveMaterial}");
            }, hotkey: $"Shift+Alt+{(slot + 1) % 10}");
        }
        c.AddReference("Texture", "Shift+Alt+1..0", "material slot");

        c.Add("texture.alignGrid", "Align to grid", () => ctx.ForFaces("Align to Grid", (mesh, faces) =>
        {
            foreach (var f in faces) mesh.Faces[f].Map = TextureMapping.AlignToGrid(mesh.FaceNormal(f), mesh.Faces[f].Map);
        }), "Texture", "Ctrl+Shift+G", HasFaceTargets, "2-align", 0);
        c.Add("texture.alignFace", "Align to face", () => ctx.ForFaces("Align to Face", (mesh, faces) =>
        {
            foreach (var f in faces) mesh.Faces[f].Map = TextureMapping.AlignToFace(mesh.FaceNormal(f), mesh.Faces[f].Map);
        }), "Texture", "Ctrl+Shift+F", HasFaceTargets, "2-align", 1);
        // Fit honours the fit options (mode, Keep Ratio, Treat as One) from the texture section.
        c.Add("texture.fit", "Fit to face", () => TexturingFeature.Fit(ctx), "Texture", "Ctrl+Alt+Num5", HasFaceTargets, "2-align", 2);
        c.Add("texture.flipU", "Flip horizontally", () => FlipTexture(ctx, horizontal: true), "Texture", "Alt+D", HasFaceTargets, "3-flip", 0);
        c.Add("texture.flipV", "Flip vertically", () => FlipTexture(ctx, horizontal: false), "Texture", "Alt+E", HasFaceTargets, "3-flip", 1);

        ctx.AddMouseBinding(new MouseBinding(ImGuiMouseButton.Right, Ctrl: false, Shift: true, Alt: false, "Texture", "pick up material", context =>
        {
            if (context.RaycastFace() is not { } hit) return;
            context.ActiveMaterial = hit.Mesh.Faces[hit.Face].Material;
            context.Status($"Picked up {context.ActiveMaterial}");
        }));
    }

    /// <summary>Mirrors the texture: projections negate their scale (mirror about the texture origin), explicit UVs mirror in place.</summary>
    static void FlipTexture(EditorContext ctx, bool horizontal)
    {
        var size = TextureOps.SizeOf(ctx);
        TextureOps.Edit(ctx, horizontal ? "Flip U" : "Flip V", faces =>
        {
            foreach (var f in faces)
            {
                if (UvSpace.IsExplicit(f.Face)) Justify.Flip([f], horizontal, size);
                else f.Face.Map = horizontal ? f.Face.Map with { ScaleU = -f.Face.Map.ScaleU } : f.Face.Map with { ScaleV = -f.Face.Map.ScaleV };
            }
        });
    }
}
