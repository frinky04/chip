using Chip.Core.Scene;
using Chip.Core.Texturing;
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
        c.Add("texture.fit", "Fit to face", () => ctx.ForFaces("Fit Texture", (mesh, faces) =>
        {
            foreach (var f in faces)
            {
                var info = ctx.Materials.Get(mesh.Faces[f].Material);
                mesh.Faces[f].Map = mesh.Faces[f].Map.FitTo(mesh.FacePoints(f), info.Width, info.Height);
            }
        }), "Texture", "Ctrl+Alt+Num5", HasFaceTargets, "2-align", 2);
        c.Add("texture.flipU", "Flip horizontally", () => ctx.EditMapping("Flip U", m => m with { ScaleU = -m.ScaleU }), "Texture", null, HasFaceTargets, "3-flip", 0);
        c.Add("texture.flipV", "Flip vertically", () => ctx.EditMapping("Flip V", m => m with { ScaleV = -m.ScaleV }), "Texture", null, HasFaceTargets, "3-flip", 1);

        ctx.AddMouseBinding(new MouseBinding(ImGuiMouseButton.Right, Ctrl: false, Shift: true, Alt: false, "Texture", "pick up material", context =>
        {
            if (context.RaycastFace() is not { } hit) return;
            context.ActiveMaterial = hit.Mesh.Faces[hit.Face].Material;
            context.Status($"Picked up {context.ActiveMaterial}");
        }));
    }
}
