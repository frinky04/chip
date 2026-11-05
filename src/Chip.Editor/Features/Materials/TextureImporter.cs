using System.Numerics;
using Chip.Core.Scene;
using Chip.Editor.Framework;
using Chip.Editor.UI;
using ImGuiNET;
using static Chip.Editor.UI.Widgets;

namespace Chip.Editor.Features.Materials;

/// <summary>
/// Images dropped on the window go into the project's texture folder (<see cref="TextureImport"/>), asking first when one
/// would replace a texture. Copying runs on a worker, since a dropped folder of large images takes a while to scale.
/// </summary>
public sealed class TextureImporter(ProjectTextures project)
{
    const string Title = "Replace textures?";

    ImportPlan? _asking;
    string _folder = "";
    bool _request;
    Task<TextureImportResult>? _running;
    IReadOnlyList<string> _rejected = [];

    /// <summary>Takes files or folders dropped on the window.</summary>
    public void Drop(EditorContext ctx, IReadOnlyList<string> paths)
    {
        if (_running != null || _asking != null) { ctx.Status("Chip is still importing the last textures. Drop these again when it's done", error: true); return; }
        if (ctx.Materials.ProjectFolder is not { } folder)
        {
            ctx.Status("Save the level first. Dropped textures go in its project, or in a folder beside it", error: true);
            return;
        }
        ImportPlan plan;
        try { plan = TextureImport.Inspect(paths, folder, ctx.Materials); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ctx.Status($"Couldn't read the dropped files: {e.Message}", error: true);
            return;
        }
        _folder = folder;
        if (plan.Items.Count == 0)
        {
            ctx.Status(plan.Rejected.Count > 0 ? $"Nothing imported: {plan.Rejected[0]}" : "Those textures are already in the project", error: plan.Rejected.Count > 0);
            return;
        }
        if (plan.Items.Any(i => i.Clash != null)) { _asking = plan; _request = true; }
        else Start(ctx, plan, keepBoth: false);
    }

    void Start(EditorContext ctx, ImportPlan plan, bool keepBoth)
    {
        var (folder, max) = (_folder, ctx.ImportMaxSize());
        _rejected = plan.Rejected;
        // Before the copies land, so a rescan never sees one without the settings that keep faces where they are.
        var unsaved = plan.Items.Where(i => i.Material is { } m && !ctx.Materials.CarryToProject(m)).Select(i => i.Material!.Name).ToList();
        if (unsaved.Count > 0) _rejected = [.. _rejected, $"{unsaved[0]}'s settings couldn't be saved in {MaterialLibrary.SidecarName}, so they last until Chip closes"];
        _running = Task.Run(() => TextureImport.Run(plan, folder, keepBoth, max));
        ctx.Status(plan.Items.Count == 1 ? "Importing 1 texture…" : $"Importing {plan.Items.Count} textures…");
    }

    /// <summary>The per-frame host callback: the replace question, and finishing an import (never mid-gesture).</summary>
    public void Update(EditorContext ctx)
    {
        DrawQuestion(ctx);
        if (_running is not { IsCompleted: true } running || ctx.Interacting) return;
        _running = null;
        project.ReloadQuietly(ctx);
        var result = running.Result;
        if (result.FirstMaterial != null && ctx.Materials.Contains(result.FirstMaterial)) ctx.ActiveMaterial = result.FirstMaterial;
        if (result.Imported > 0 && !ctx.DockShowing(MaterialBrowser.PanelId)) ctx.ToggleDock(MaterialBrowser.PanelId);
        ctx.Status(Summary(result, _rejected), error: result.Problems.Count > 0 || _rejected.Count > 0);
    }

    void DrawQuestion(EditorContext ctx)
    {
        if (_request)
        {
            ImGui.OpenPopup(Title);
            _request = false;
        }
        if (_asking is not { } plan) return;
        ImGui.SetNextWindowPos(ImGui.GetMainViewport().GetCenter(), ImGuiCond.Appearing, new Vector2(0.5f));
        ImGui.SetNextWindowSize(new Vector2(Theme.Px(440), 0));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Theme.Px(20, 16));
        bool open = true;
        if (ImGui.BeginPopupModal(Title, ref open, ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize))
        {
            var clashes = plan.Items.Where(i => i.Clash != null).Select(i => Path.GetFileNameWithoutExtension(i.Clash!)).ToList();
            string names = string.Join(", ", clashes.Take(5)) + (clashes.Count > 5 ? $" and {clashes.Count - 5} more" : "");
            ImGui.PushTextWrapPos(0);
            ImGui.TextColored(Theme.TextDim, $"The project already has {names}.");
            ImGui.TextColored(Theme.TextDim, "Replacing keeps how it tiles on faces. Keeping both gives the new image a new name, and leaves out other maps such as normals.");
            ImGui.PopTextWrapPos();
            ImGui.Dummy(Theme.Px(0, 4));
            float width = (ImGui.GetContentRegionAvail().X - Theme.Px(12)) / 3;
            PushPrimary();
            bool replace = ImGui.Button("Replace", new Vector2(width, 0));
            PopPrimary();
            ImGui.SameLine(0, Theme.Px(6));
            bool both = ImGui.Button("Keep both", new Vector2(width, 0));
            ImGui.SameLine(0, Theme.Px(6));
            bool cancel = ImGui.Button("Cancel", new Vector2(width, 0)) || ImGui.IsKeyPressed(ImGuiKey.Escape);
            if (replace || both || cancel)
            {
                _asking = null;
                ImGui.CloseCurrentPopup();
                if (!cancel) Start(ctx, plan, keepBoth: both);
            }
            ImGui.EndPopup();
        }
        else if (!open) _asking = null;
        ImGui.PopStyleVar();
    }

    /// <summary>The status line for a finished import: what happened, then the first thing that didn't.</summary>
    public static string Summary(TextureImportResult result, IReadOnlyList<string> rejected)
    {
        var parts = new List<string>
        {
            result.Imported == 1 ? "Imported 1 texture" : $"Imported {result.Imported} textures",
        };
        if (result.Replaced > 0) parts.Add($"{result.Replaced} replaced");
        if (result.Resized > 0) parts.Add($"{result.Resized} scaled down");
        if (result.OtherMaps > 0) parts.Add(result.OtherMaps == 1 ? "1 other map, such as a normal map, isn't listed" : $"{result.OtherMaps} other maps, such as normal maps, aren't listed");
        var left = result.Problems.Concat(rejected).ToList();
        if (left.Count > 0) parts.Add(left.Count == 1 ? $"1 skipped: {left[0]}" : $"{left.Count} skipped, first: {left[0]}");
        return string.Join("; ", parts);
    }
}
