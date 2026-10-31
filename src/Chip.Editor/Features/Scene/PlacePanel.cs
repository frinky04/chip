using System.Numerics;
using Chip.Core.Scene;
using Chip.Editor.Features.References;
using Chip.Editor.Framework;
using Chip.Editor.UI;
using ImGuiNET;
using static Chip.Editor.UI.Widgets;

namespace Chip.Editor.Features.Scene;

/// <summary>
/// The Place tab of the dock: everything you can put in the level ready-made. The project's prefabs (linked copies),
/// entity classes (from the game project, else the built-ins) and reference models. Click one to arm the entity tool with it, then click in the
/// viewport; Esc puts the tool down.
/// </summary>
public sealed class PlacePanel : IDockPanel
{
    public const string PanelId = "place";
    string _filter = "";

    public string Id => PanelId;
    public string Title => "Place";
    public Icon Icon => Icon.MapPin;
    public int Order => 20;
    public string? Command => "scene.place";

    public void Draw(EditorContext ctx)
    {
        if (ctx.Tools.OfType<EntityTool>().FirstOrDefault() is not { } tool) return;
        bool armed = ctx.ActiveTool == tool;
        bool placingReference = armed && tool.ClassName.Trim() == ReferenceModels.ClassName;

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##filter", "Filter prefabs and classes", ref _filter, 64);
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextDim);
        ImGui.TextWrapped(armed ? "Click in the viewport to place. Esc puts the tool down." : "Pick something, then click in the viewport to place it.");
        ImGui.PopStyleColor();
        ImGui.Dummy(Theme.Px(0, 2));

        DrawPrefabs(ctx);

        float refsH = ImGui.GetFrameHeightWithSpacing() * (ReferenceModels.BuiltIns().Count + 3) + ImGui.GetTextLineHeightWithSpacing() * 2;
        Heading("Entities", EntityClasses.ProjectRoot is { } root ? Path.GetFileName(root) : "built-in");
        ImGui.PushStyleColor(ImGuiCol.ChildBg, Theme.Bg);
        if (ImGui.BeginChild("##classes", new Vector2(0, MathF.Max(Theme.Px(120), ImGui.GetContentRegionAvail().Y - refsH))))
        {
            if (ClassPicker.Draw(armed && !placingReference ? tool.ClassName : null, _filter, references: false, show: ClassPicker.Point) is { } picked)
            {
                tool.ClassName = picked;
                ctx.SetTool(tool);
                ctx.Status($"Click to place {EntityClasses.Get(picked).Label}");
            }
        }
        ImGui.EndChild();
        ImGui.PopStyleColor();
        if (EntityClasses.Problem is { } problem)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.Error);
            ImGui.TextWrapped(problem);
            ImGui.PopStyleColor();
        }

        ImGui.Dummy(Theme.Px(0, 4));
        Heading("Reference models", "editor only");
        foreach (var name in ReferenceModels.BuiltIns())
            if (ImGui.Selectable($"{name}##ref", placingReference && string.Equals(tool.Model, name, StringComparison.OrdinalIgnoreCase)))
                ArmReference(ctx, tool, name);
        if (placingReference && !ReferenceModels.IsBuiltInName(tool.Model)) ImGui.Selectable(ReferenceModels.DisplayName(tool.Model), true);
        if (ImGui.Button("From file…", new Vector2(-1, 0)) && ReferencePicker.ChooseFile() is { } file) ArmReference(ctx, tool, file);
        Tip("A .glb or .gltf (your door, a prop) drawn in the level to size holes and check scale");
    }

    /// <summary>
    /// The project's prefab files: click one to arm the prefab tool with it. Up to six rows show; more scroll. Levels
    /// that aren't saved yet have no project to look in.
    /// </summary>
    void DrawPrefabs(EditorContext ctx)
    {
        if (ctx.Tools.OfType<PrefabTool>().FirstOrDefault() is not { } tool) return;
        var items = PrefabIndex.Of(ctx).Get(ctx);
        Heading("Prefabs", ctx.Session.PrefabRoot is { } root ? Path.GetFileName(root) : "save the level first");
        var shown = items.Where(p => _filter.Length == 0 || p.RelativePath.Contains(_filter, StringComparison.OrdinalIgnoreCase)).ToList();
        if (shown.Count == 0)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextDim);
            ImGui.TextWrapped(ctx.Session.PrefabRoot == null ? "Prefabs live in a level's project. Save the level to see them."
                : items.Count == 0 ? "None yet. Select a prop and use Edit > Save as prefab." : "No prefab matches the filter.");
            ImGui.PopStyleColor();
            ImGui.Dummy(Theme.Px(0, 4));
            return;
        }
        bool armed = ctx.ActiveTool == tool;
        float h = ImGui.GetFrameHeight();
        float rowH = h + ImGui.GetStyle().ItemSpacing.Y;
        ImGui.PushStyleColor(ImGuiCol.ChildBg, Theme.Bg);
        if (ImGui.BeginChild("##prefabs", new Vector2(0, rowH * Math.Min(shown.Count, 6) + Theme.Px(4))))
            foreach (var p in shown)
            {
                // Drawn like the entity classes below: the icon, the name, and its folder when there is one.
                var at = ImGui.GetCursorScreenPos();
                if (ImGui.Selectable($"##{p.FullPath}", armed && tool.Prefab?.FullPath == p.FullPath, ImGuiSelectableFlags.None, new Vector2(0, h)))
                {
                    tool.Prefab = p;
                    ctx.SetTool(tool);
                    ctx.Status($"Click to place {p.Name}");
                }
                Tip(p.RelativePath);
                var dl = ImGui.GetWindowDrawList();
                Icons.Draw(dl, Icon.Package, at + new Vector2(Theme.Px(4) + Fonts.IconSize * 0.5f, h * 0.5f), Theme.PrefabU32);
                float textY = MathF.Round(at.Y + (h - ImGui.GetTextLineHeight()) * 0.5f);
                float x = at.X + Theme.Px(10) + Fonts.IconSize;
                dl.AddText(new Vector2(x, textY), ImGui.ColorConvertFloat4ToU32(Theme.Text), p.Name);
                if (Path.GetDirectoryName(p.RelativePath) is { Length: > 0 } folder)
                    dl.AddText(new Vector2(x + ImGui.CalcTextSize(p.Name).X + Theme.Px(8), textY), ImGui.ColorConvertFloat4ToU32(Theme.TextDim), folder.Replace('\\', '/'));
            }
        ImGui.EndChild();
        ImGui.PopStyleColor();
        ImGui.Dummy(Theme.Px(0, 4));
    }

    static void ArmReference(EditorContext ctx, EntityTool tool, string model)
    {
        tool.ClassName = ReferenceModels.ClassName;
        tool.Model = model;
        ctx.SetTool(tool);
        var (loaded, error) = ReferenceModels.Get(model);
        ctx.Status(loaded != null ? $"Click to place {ReferenceModels.DisplayName(model)}. On a wall it sits in the wall if it fits, else against it" : error!, error: loaded == null);
    }
}
