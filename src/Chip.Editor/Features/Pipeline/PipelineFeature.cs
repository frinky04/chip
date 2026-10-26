using Chip.Editor.Framework;
using NativeFileDialogSharp;

namespace Chip.Editor.Features.Pipeline;

/// <summary>
/// The export pipeline (SPEC §11): export settings, per-mesh collision, export on save (watch mode), OBJ export and
/// glTF/OBJ import. Watch mode hooks <c>Session.Saved</c>, so every way of saving re-exports.
/// </summary>
public sealed class PipelineFeature : IEditorFeature
{
    public void Register(EditorContext ctx)
    {
        var window = new ExportSettingsWindow();
        ctx.AddPanel(window.Draw);
        ctx.AddSection(new CollisionSection());
        ctx.Session.Saved += path => PipelineActions.ExportAfterSave(ctx, path);

        var c = ctx.Commands;
        c.Add("pipeline.exportObj", "Export OBJ…", () => ExportObj(ctx), "File", group: "2-export", order: 2);
        c.Add("pipeline.exportOnSave", "Export on save", () => PipelineActions.ToggleExportOnSave(ctx), "File", "Ctrl+Alt+E",
            group: "2-export", order: 3, isChecked: () => ctx.Doc.Export.ExportOnSave);
        c.Add("pipeline.exportSettings", "Export settings…", () => window.Open = !window.Open, "File",
            group: "2-export", order: 4, isChecked: () => window.Open);
        c.Add("pipeline.import", "Import mesh…", () => Import(ctx), "File", group: "3-import", order: 0);
    }

    static void ExportObj(EditorContext ctx)
    {
        var result = Dialog.FileSave("obj");
        if (!result.IsOk) return;
        var path = result.Path.EndsWith(".obj", StringComparison.OrdinalIgnoreCase) ? result.Path : result.Path + ".obj";
        PipelineActions.ExportObj(ctx, path);
    }

    static void Import(EditorContext ctx)
    {
        var result = Dialog.FileOpen("glb,gltf,obj");
        if (result.IsOk) PipelineActions.Import(ctx, result.Path);
    }
}
