using Chip.Core.Geometry;
using Chip.Editor.Framework;
using Chip.Editor.UI;
using ImGuiNET;
using static Chip.Editor.UI.Widgets;

namespace Chip.Editor.Features.Pipeline;

/// <summary>Per-mesh export collision for the selected objects (Godot import suffixes).</summary>
public sealed class CollisionSection : IInspectorSection
{
    public static readonly (CollisionMode Mode, string Label, string Tip)[] Modes =
    [
        (CollisionMode.Default, "Default", "Follows the document's static collision setting (-col: rendered with a trimesh collider)"),
        (CollisionMode.None, "None", "Rendered, never collides"),
        (CollisionMode.Only, "Collision only", "Invisible trimesh collider (-colonly)"),
        (CollisionMode.ConvexOnly, "Convex only", "Invisible convex-hull collider (-convcolonly), the cheapest for physics"),
    ];

    public string Id => "collision";
    public int Order => 880;
    public bool Visible(EditorContext ctx) => ctx.Sel.Mode == Chip.Core.Scene.SelectMode.Object && !ctx.Sel.IsEmpty;

    public void Draw(EditorContext ctx)
    {
        var modes = ctx.Sel.MeshIds().Select(ctx.Doc.Find).OfType<PolyMesh>().Select(m => m.Collision).Distinct().ToList();
        if (modes.Count == 0) return;
        if (!Heading("Collision")) return;
        RowLabel("Mode", "How the selected meshes collide in the exported .glb");
        ImGui.SetNextItemWidth(-1);
        string preview = modes.Count == 1 ? Modes.First(m => m.Mode == modes[0]).Label : "Mixed";
        if (ImGui.BeginCombo("##collision", preview))
        {
            foreach (var (mode, label, tip) in Modes)
            {
                if (ImGui.Selectable(label, modes.Count == 1 && modes[0] == mode)) PipelineActions.SetCollision(ctx, mode);
                Tip(tip);
            }
            ImGui.EndCombo();
        }
        if (modes.Count == 1)
        {
            ImGui.PushTextWrapPos(0);
            ImGui.TextColored(Theme.TextDim, Modes.First(m => m.Mode == modes[0]).Tip);
            ImGui.PopTextWrapPos();
        }
    }
}
