using Chip.Core.Scene;
using Chip.Editor.Framework;
using NativeFileDialogSharp;

namespace Chip.Editor.Features.Materials;

/// <summary>
/// Materials and assets: library folders (bundled textures, bundled tool materials, <c>CHIP_TEXTURE_DIRS</c>, folders
/// added this session), the Materials tab of the dock, replace material, and applying per-material texel density.
/// </summary>
public sealed class MaterialsFeature : IEditorFeature
{
    public void Register(EditorContext ctx)
    {
        var lib = ctx.Materials;
        lib.AddFolder(Path.Combine(AppContext.BaseDirectory, "assets", "tools"));
        foreach (var folder in ctx.TextureFolders) lib.AddFolder(folder); // from settings (Preferences > Paths)
        foreach (var folder in MaterialLibrary.EnvironmentFolders()) lib.AddFolder(folder);
        lib.Rescan();
        var missing = lib.Folders.Where(f => !Directory.Exists(f) && !f.StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase)).ToList();
        if (missing.Count > 0)
            ctx.Status($"Texture folder not found: {string.Join(", ", missing)}. Fix {MaterialLibrary.EnvVar}, then Texture > Rescan texture folders", error: true);

        var browser = MaterialBrowser.For(ctx);
        var replace = ReplaceMaterialDialog.For(ctx);
        ctx.AddDockPanel(browser);
        ctx.AddPanel(browser.Update);
        ctx.AddPanel(new SurfaceMaterials().Update);
        ctx.AddPanel(replace.Draw);

        var c = ctx.Commands;
        bool HasFaceTargets() => ctx.Sel.FaceSets(ctx.Doc).Count > 0;

        c.Add("materials.browser", "Materials", () => ctx.ToggleDock(MaterialBrowser.PanelId), "View", "T",
            group: "5-panels", isChecked: () => ctx.DockShowing(MaterialBrowser.PanelId));
        c.Add("materials.replace", "Replace material…", () => replace.Show(ctx), "Texture",
            canExecute: () => ctx.Doc.Meshes.Any(m => m.Faces.Count > 0), group: "1-material", order: 5);
        c.Add("materials.applyDensity", "Reset scale to material density", () => ctx.ForFaces("Apply Material Density", (mesh, faces) =>
        {
            foreach (var f in faces) lib.ApplyDensity(mesh.Faces[f]);
        }), "Texture", canExecute: HasFaceTargets, group: "2-align", order: 10);
        c.Add("materials.addFolder", "Add texture folder…", () => AddFolder(ctx), "Texture", group: "8-library", order: 0);
        c.Add("materials.rescan", "Rescan texture folders", () => Rescan(ctx), "Texture", group: "8-library", order: 1);
    }

    static void AddFolder(EditorContext ctx)
    {
        var result = Dialog.FolderPicker();
        if (!result.IsOk) return;
        if (!ctx.Materials.AddFolder(result.Path)) { ctx.Status($"{result.Path} is already in the library"); return; }
        Rescan(ctx);
    }

    /// <summary>Re-reads every library folder and reports what changed.</summary>
    public static void Rescan(EditorContext ctx)
    {
        int before = ctx.Materials.All.Count;
        var r = ctx.Materials.Rescan();
        MaterialBrowser.For(ctx).Thumbs.Refresh();
        ctx.Session.Touch(); // sizes may have changed: rebuild the scene's UVs
        var notes = new List<string>();
        if (r.Duplicates > 0) notes.Add(r.Duplicates == 1 ? "1 duplicate name skipped" : $"{r.Duplicates} duplicate names skipped");
        if (r.Unreadable > 0) notes.Add(r.Unreadable == 1 ? "1 unreadable image" : $"{r.Unreadable} unreadable images");
        if (r.MissingFolders > 0) notes.Add(r.MissingFolders == 1 ? "1 folder not found" : $"{r.MissingFolders} folders not found");
        if (r.MissingFolders > 0) notes[^1] += $" (check {MaterialLibrary.EnvVar})";
        int delta = r.Materials - before;
        string head = $"{r.Materials} materials" + (delta > 0 ? $" ({delta} new)" : delta < 0 ? $" ({-delta} gone)" : "");
        ctx.Status(notes.Count == 0 ? head : $"{head}; {string.Join(", ", notes)}", error: r.MissingFolders > 0);
    }
}
