using Chip.Core.IO;
using Chip.Core.Scene;
using Chip.Editor.Framework;
using NativeFileDialogSharp;

namespace Chip.Editor.Features.Base;

/// <summary>New / open / save / export / quit.</summary>
public sealed class FileFeature : IEditorFeature
{
    EditorContext _ctx = null!;

    public void Register(EditorContext ctx)
    {
        _ctx = ctx;
        var c = ctx.Commands;
        c.Add("file.new", "New", New, "File", "Ctrl+N", group: "1-doc", order: 0);
        c.Add("file.open", "Open...", Open, "File", "Ctrl+O", group: "1-doc", order: 1);
        c.Add("file.save", "Save", () => Save(false), "File", "Ctrl+S", group: "1-doc", order: 2);
        c.Add("file.saveAs", "Save as...", () => Save(true), "File", "Ctrl+Shift+S", group: "1-doc", order: 3);
        c.Add("file.export", "Export GLB", () => Export(false), "File", "Ctrl+E", group: "2-export", order: 0);
        c.Add("file.exportAs", "Export GLB as...", () => Export(true), "File", "Ctrl+Shift+E", group: "2-export", order: 1);
        c.Add("file.quit", "Quit", () => ctx.Quit(), "File", group: "9-quit");
    }

    void New() => _ctx.ConfirmDiscard(() =>
    {
        _ctx.CancelInteraction();
        _ctx.Session.Reset(new Document(), null);
        _ctx.Status("New document");
    });

    void Open() => _ctx.ConfirmDiscard(() =>
    {
        var result = Dialog.FileOpen("chip");
        if (!result.IsOk) return;
        try
        {
            _ctx.CancelInteraction();
            _ctx.Session.Reset(DocumentSerializer.Load(result.Path), result.Path);
            _ctx.FrameAll();
            _ctx.Status($"Opened {Path.GetFileName(result.Path)}");
        }
        catch (Exception e) { _ctx.Status($"Couldn't open {Path.GetFileName(result.Path)}: {e.Message}", true); }
    });

    /// <summary>Saves; returns false if cancelled or failed (the unsaved-changes dialog relies on this).</summary>
    public static bool Save(EditorContext ctx, bool saveAs)
    {
        var path = ctx.Session.Path;
        if (saveAs || path == null)
        {
            var result = Dialog.FileSave("chip");
            if (!result.IsOk) return false;
            path = EnsureExtension(result.Path, ".chip");
        }
        try
        {
            DocumentSerializer.Save(ctx.Doc, path);
            ctx.Status($"Saved {Path.GetFileName(path)}");
            ctx.Session.MarkSaved(path); // after the status, so Saved handlers (export on save) can report over it
            return true;
        }
        catch (Exception e)
        {
            ctx.Status($"Couldn't save {Path.GetFileName(path)}: {e.Message} Choose another folder and try again.", true);
            return false;
        }
    }

    void Save(bool saveAs) => Save(_ctx, saveAs);

    void Export(bool exportAs)
    {
        var path = _ctx.Doc.ExportPath;
        if (exportAs || path == null)
        {
            var result = Dialog.FileSave("glb");
            if (!result.IsOk) return;
            path = EnsureExtension(result.Path, ".glb");
            if (path != _ctx.Doc.ExportPath) _ctx.Session.Do("Set Export Path", () => _ctx.Doc.ExportPath = path);
        }
        try
        {
            var r = GlbExporter.Export(_ctx.ExportDocument(), _ctx.Materials, path);
            _ctx.Status($"Exported {Features.Pipeline.PipelineActions.Describe(r.Meshes, r.Entities)} to {Path.GetFileName(path)}{Features.Pipeline.PipelineActions.Report(r)}");
        }
        catch (Exception e) { _ctx.Status($"Export failed: {e.Message} Choose a writable folder and try again.", true); }
    }

    static string EnsureExtension(string path, string ext) =>
        path.EndsWith(ext, StringComparison.OrdinalIgnoreCase) ? path : path + ext;
}
